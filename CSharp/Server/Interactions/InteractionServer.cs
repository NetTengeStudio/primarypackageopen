using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Barotrauma;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer : IModule, ILoadCompleted, IEventUpdate, IEventClientConnected, IEventClientDisconnected, IEventRoundEnded, IEventChatMessage {
    private sealed class Session {
        public Client Client;
        public string Account;
        public double Refill, NextMessage, NextRoleplay, NextAnnouncement;
        public double Tokens = 32;
        public int HistoryCursor;
        public bool Syncing;
        public readonly List<PrivateMessage> Replay = new();
        public readonly Dictionary<string, double> Actions = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, GMAnnouncementReceipt> Announcements = new();
        public readonly Queue<Guid> AnnouncementOrder = new();
    }
    private readonly Dictionary<byte, Session> sessions = new();
    private PermissionService permissions;
    private PrivateMessageService messages;
    private double nextMaintenance;
    private bool live, rosterDirty;
    private IDisposable receiver;
    private static InteractionServer instance;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public void Attach(Harmony harmony) {
        instance = this;
        harmony.Patch(AccessTools.Method(typeof(Client), nameof(Client.SetPermissions)), postfix: ModuleHost.Bind(typeof(InteractionServer), nameof(RosterChanged)));
        harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.SetClientCharacter)), postfix: ModuleHost.Bind(typeof(InteractionServer), nameof(RosterChanged)));
        receiver = INet.Net.Listen(InteractionProtocol.Channel, Receive);
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventClientConnected>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventRoundEnded>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventChatMessage>(this);
    }
    public void LoadCompleted() {
        string directory = ModAssets.Resolve("Data/Interactions");
        permissions = new PermissionService(directory);
        try { messages = new PrivateMessageService(directory); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { ModLog.Error("Private messages unavailable; journal retained: " + error.Message); }
        live = true;
    }
    public void RoundStart() => rosterDirty = true;
    public void Release() {
        live = false;
        receiver?.Dispose(); receiver = null;
        messages?.Dispose(); messages = null;
        permissions = null; sessions.Clear(); instance = null;
    }
    public static void RosterChanged() { if (instance != null) { instance.rosterDirty = true; } }
    public void OnClientConnected(Client client) => rosterDirty = true;
    public void OnClientDisconnected(Client client) { sessions.Remove(client.SessionId); rosterDirty = true; }
    public void OnRoundEnd() => RoundStart();
    private Session GetSession(Client sender, double now)
    {
        string account = PermissionService.Account(sender);
        if (account.Length == 0) { throw new UnauthorizedAccessException("Authenticated account required"); }
        if (!sessions.TryGetValue(sender.SessionId, out var session) || session.Client != sender || session.Account != account)
        {
            session = new Session { Client = sender, Account = account, Refill = now };
            sessions[sender.SessionId] = session;
        }
        session.Tokens = Math.Min(32, session.Tokens + Math.Max(0, now - session.Refill) * 16);
        session.Refill = now;
        if (session.Tokens < 1) { throw new InvalidOperationException("Слишком много запросов"); }
        session.Tokens--;
        return session;
    }
    private void Require(Client sender, InteractionAction action)
    {
        if (!permissions.Allows(sender, action)) { throw new UnauthorizedAccessException("Нет разрешения: " + action); }
    }
    private void Receive(IReadMessage message, Client sender)
    {
        if (!live || sender?.Connection == null || message.LengthBytes > 32768) { return; }
        try
        {
            if (!InteractionProtocol.Read(message, out var packet)) { return; }
            double now = Now;
            Session session = GetSession(sender, now);
            switch (packet)
            {
                case InteractionPacket.Hello:
                    SendSnapshot(session);
                    if (messages == null) { break; }
                    session.Replay.Clear();
                    session.Replay.AddRange(messages.History(session.Account));
                    session.Replay.AddRange(permissions.Allows(sender, InteractionAction.ViewGMFeed) ? messages.StaffHistory : messages.SupportHistory(session.Account));
                    session.Replay.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
                    session.HistoryCursor = Math.Max(0, session.Replay.Count - 1000);
                    session.Syncing = true;
                    break;
                case InteractionPacket.MessageSend:
                    if (messages == null) { throw new InvalidOperationException("Хранилище личных сообщений недоступно"); }
                    Require(sender, InteractionAction.SendPrivateMessage);
                    if (now < session.NextMessage) { throw new InvalidOperationException("Подождите перед следующим сообщением"); }
                    session.NextMessage = now + .4;
                    string target = message.ReadString(), id = message.ReadString(), text = message.ReadString();
                    Client recipient = FindAccount(target);
                    if (recipient == null)
                    {
                        bool known = false;
                        foreach (var old in messages.History(session.Account)) { if (old.From == target || old.To == target) { known = true; break; } }
                        if (!known) { throw new ArgumentException("Получатель недоступен"); }
                    }
                    PrivateMessage stored = messages.Store(session.Account, target, sender.Name, recipient?.Name ?? target, id, text);
                    SendMessage(sender, stored);
                    if (recipient != null)
                    {
                        SendMessage(recipient, stored);
                    }
                    break;
                case InteractionPacket.ReceiptBatch:
                    if (messages == null) { throw new InvalidOperationException("Хранилище сообщений недоступно"); }
                    int receipts = message.ReadByte();
                    if (receipts < 1 || receipts > 8) { throw new ArgumentException("Недопустимый пакет подтверждений"); }
                    for (int i = 0; i < receipts; i++) {
                        string receiptSender = message.ReadString(), receiptId = message.ReadString(); bool read = message.ReadBoolean();
                        var receipt = messages.Acknowledge(session.Account, receiptSender, receiptId, read);
                        SendMessage(sender, receipt);
                        var original = FindAccount(receiptSender); if (original != null) { SendMessage(original, receipt); }
                    }
                    break;
                case InteractionPacket.MessageAck:
                case InteractionPacket.MessageRead:
                    if (messages == null) { throw new InvalidOperationException("Хранилище личных сообщений недоступно"); }
                    string from = message.ReadString(), messageId = message.ReadString();
                    PrivateMessage acknowledged = messages.Acknowledge(session.Account, from, messageId, packet == InteractionPacket.MessageRead);
                    SendMessage(sender, acknowledged);
                    Client originalSender = FindAccount(from);
                    if (originalSender != null) { SendMessage(originalSender, acknowledged); }
                    break;
                case InteractionPacket.ChannelSend:
                    if (messages == null) { throw new InvalidOperationException("Хранилище сообщений недоступно"); }
                    MessageKind kind = (MessageKind)message.ReadByte();
                    string thread = message.ReadString(), channelId = message.ReadString(), channelText = message.ReadString();
                    if (kind != MessageKind.Support) { throw new ArgumentException("Недопустимый канал отправки"); }
                    bool staff = permissions.Allows(sender, InteractionAction.ViewGMFeed);
                    if (!staff) { thread = session.Account; }
                    if (now < session.NextMessage) { throw new InvalidOperationException("Подождите перед следующим сообщением"); }
                    session.NextMessage = now + .4;
                    Publish(messages.StoreInChannel(session.Account, staff && thread != session.Account ? thread : "gm", sender.Name, "Игровые мастера", channelId, channelText, kind, thread));
                    break;
                case InteractionPacket.GMAnnouncement:
                    Announce(message, session, now); break;

            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            INet.Net.Send(InteractionProtocol.Channel, sender.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.Error); output.WriteString(error is IOException ? "Хранилище недоступно: операция не подтверждена" : error.Message); });
            ModLog.Once("Interaction request rejected: " + error.Message);
        }
    }
    private void SendSnapshot(Session session)
    {
        INet.Net.Send(InteractionProtocol.Channel, session.Client.Connection, output =>
        {
            InteractionProtocol.Header(output, InteractionPacket.Snapshot);
            output.WriteString(session.Account);
            uint mask = 0;
            foreach (InteractionAction action in Enum.GetValues<InteractionAction>()) { if ((action != InteractionAction.SendPrivateMessage || messages != null) && permissions.Allows(session.Client, action)) { mask |= 1u << (int)action; } }
            output.WriteUInt32(mask);
            output.WriteByte(permissions.AnnouncementFormats(session.Client));
            var clients = GameMain.Server.ConnectedClients;
            int count = Math.Min(128, clients.Count);
            output.WriteByte((byte)count);
            for (int i = 0; i < count; i++) { output.WriteString(PermissionService.Account(clients[i])); output.WriteString(clients[i].Name); output.WriteUInt16(clients[i].Character is { Removed: false } body ? body.ID : (ushort)0); }
        });
    }
    private static Client FindAccount(string account)
    {
        foreach (Client client in GameMain.Server.ConnectedClients) { if (PermissionService.Account(client) == account) { return client; } }
        return null;
    }
    private static void SendMessage(Client recipient, PrivateMessage message)
    {
        string account = PermissionService.Account(recipient);
        if (!MessageAccess.CanView(message, account, instance.permissions.Allows(recipient, InteractionAction.ViewGMFeed))) { return; }
        INet.Net.Send(InteractionProtocol.Channel, recipient.Connection, output =>
        {
            InteractionProtocol.Header(output, InteractionPacket.Message);
            output.WriteByte((byte)message.Kind); output.WriteString(message.Thread);
            output.WriteInt64(message.Sequence); output.WriteString(message.Id); output.WriteString(message.From); output.WriteString(message.To);
            output.WriteString(message.SenderName); output.WriteString(message.RecipientName); output.WriteString(message.Text); output.WriteInt64(message.Timestamp); output.WriteByte((byte)message.Status);
        });
    }
    public void OnUpdate(double fixedDeltaTime)
    {
        if (!live) { return; }
        double now = Now;
        if (rosterDirty)
        {
            rosterDirty = false;
            foreach (Session session in sessions.Values) { SendSnapshot(session); }
        }
        foreach (Session session in sessions.Values)
        {
            if (messages == null) { break; }
            var history = session.Replay;
            if (session.Syncing)
            {
                int limit = Math.Min(history.Count, session.HistoryCursor + 8);
                for (; session.HistoryCursor < limit; session.HistoryCursor++) { SendMessage(session.Client, history[session.HistoryCursor]); }
                session.Syncing = session.HistoryCursor < history.Count;
                if (!session.Syncing) { history.Clear(); INet.Net.Send(InteractionProtocol.Channel, session.Client.Connection, output => InteractionProtocol.Header(output, InteractionPacket.HistoryEnd)); }
            }
        }
        if (now < nextMaintenance) { return; }
        nextMaintenance = now + 1;
        foreach (var session in sessions.Values) {
            if (session.Actions.Count == 0) { continue; }
            expiredIds.Clear();
            foreach (var entry in session.Actions) { if (now - entry.Value > 120) { expiredIds.Add(entry.Key); } }
            foreach (string id in expiredIds) { session.Actions.Remove(id); }
        }
    }
    private readonly List<string> expiredIds = new();
    internal static void RecordEmote(Client sender, string text) { if (!string.IsNullOrWhiteSpace(text)) { instance?.OnChatMessage("**" + text + "**", sender, ChatMessageType.Default, null); } }
    private void Publish(PrivateMessage entry) {
        foreach (var session in sessions.Values) {
            if (permissions.Allows(session.Client, InteractionAction.ViewGMFeed) || entry.Kind == MessageKind.Support && entry.Thread == session.Account) { SendMessage(session.Client, entry); }
        }
    }
    public bool? OnChatMessage(string text, Client sender, ChatMessageType type, ChatMessage message) {
        if (!live || messages == null || sender?.Character is not { IsDead: false } || type is not (ChatMessageType.Default or ChatMessageType.Radio)) { return null; }
        if (!RoleplaySyntax.IsAction(text) || !InteractionProtocol.ValidText(text, InteractionProtocol.MaxMessageLength)) { return null; }
        try {
            var session = GetSession(sender, Now);
            if (Now < session.NextRoleplay) { return null; }
            session.NextRoleplay = Now + .5;
            Publish(messages.StoreInChannel(session.Account, "gm", sender.Character.Name, "Игровые мастера", Guid.NewGuid().ToString("N"), text, MessageKind.Roleplay, session.Account));
        } catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { ModLog.Once("RP feed: " + error.Message); }
        return null;
    }
}

