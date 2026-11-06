using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient : IModule, IEventUpdate, IEventRoundEnded {
    internal sealed class Peer {
        public string Account, Name;
        public ushort Character;
    }
    private readonly Dictionary<string, PrivateMessage> messages = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, long> messageOrder = new(2048);
    private readonly Dictionary<string, double> retry = new(StringComparer.Ordinal);
    private readonly List<PrivateMessage> outbox = new();
    internal readonly List<Peer> Peers = new();
    private InteractionViews views;
    private IDisposable receiver;
    private string account = "", endpoint = "";
    private uint permissions;
    private double nextHello, nextReceipts;
    private readonly Dictionary<(string From, string Id), (bool Read, double After)> receipts = new();
    private readonly List<(string From, string Id, bool Read)> receiptBatch = new(8);
    private bool ready;
    private bool replaying;
    private InteractionRecovery recovery;
    private string recoveryScope = "";
    private double nextRecoveryLoad;
    private string recoveryError = "";
    internal InteractionRecovery Recovery => recovery;
    public bool Connected => ready;
    private NetworkConnection lastConnection;
    public event Action MessagesChanged;
    public string Account => account;
    public IEnumerable<PrivateMessage> Messages => messages.Values;
    public bool Multiplayer => GameMain.Client != null;
    public bool Allows(InteractionAction action) => !Multiplayer || ready && (permissions & 1u << (int)action) != 0;

    public void Attach(Harmony harmony) {
        LightEffectService.Attach(harmony);
        receiver = INet.Net.Listen(InteractionProtocol.Channel, Receive);
        views = new InteractionViews(this);
        GUIRuntime.Instance.Input.Shortcut += Shortcut;
        GUIRuntime.Instance.Input.WorldPrimaryPressed += FlickerPressed;
        GUIRuntime.Instance.Updating += UpdateView;
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventRoundEnded>(this);

    }
    public void RoundStart() {
        ResetLights();
        ResetAnnouncements();
        views?.Reset();
    }
    public void OnRoundEnd() => RoundStart();
    public void Release() {
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventRoundEnded>(this);
        RoundStart();
        if (GUIRuntime.Instance != null) { GUIRuntime.Instance.Input.Shortcut -= Shortcut; GUIRuntime.Instance.Input.WorldPrimaryPressed -= FlickerPressed; GUIRuntime.Instance.Updating -= UpdateView; }
        receiver?.Dispose(); receiver = null;
        views?.Dispose();
        ClearMessages(); outbox.Clear(); retry.Clear(); Peers.Clear();
        MessagesChanged = null; AnnouncementsChanged = null;
    }
    private void UpdateView() { LightEffectService.Instance.Update(); UpdateAnnouncements(); views.Update(); }
    public void OnUpdate(double fixedDeltaTime) {
        var connection = GameMain.Client?.ClientPeer?.ServerConnection;
        if (connection != lastConnection) { ResetLights(); ResetAnnouncements(); lastConnection = connection; receipts.Clear(); ready = false; nextHello = 0; }
        string currentEndpoint = connection?.Endpoint?.ToString() ?? "";
        if (currentEndpoint.Length == 0) { ready = false; nextHello = 0; return; }
        if (endpoint != currentEndpoint) {
            if (endpoint.Length > 0) { ClearMessages(); outbox.Clear(); retry.Clear(); Peers.Clear(); views.Reset(); }
            endpoint = currentEndpoint;
            ready = false;
            nextHello = 0;
        }
        double now = GUIRuntime.Now;
        if (!ready && now >= nextHello) { nextHello = now + 3; replaying = true; Send(InteractionPacket.Hello, null); }
        if (!ready) { return; }
        if (recovery == null && now >= nextRecoveryLoad) { LoadLocalRecovery(now); }
        FlushReceipts(now);
        for (int i = 0; i < outbox.Count; i++) {
            PrivateMessage queued = outbox[i];
            if (queued.Status != MessageStatus.Queued || retry[queued.Id] > now) { continue; }
            retry[queued.Id] = now + 2;
            if (queued.Kind == MessageKind.Private) { Send(InteractionPacket.MessageSend, message => { message.WriteString(queued.To); message.WriteString(queued.Id); message.WriteString(queued.Text); }); }
            else { Send(InteractionPacket.ChannelSend, message => { message.WriteByte((byte)queued.Kind); message.WriteString(queued.Thread); message.WriteString(queued.Id); message.WriteString(queued.Text); }); }
            break;
        }
    }
    private bool Send(InteractionPacket packet, Action<IWriteMessage> write) => INet.Net.Send(InteractionProtocol.Channel, output => { InteractionProtocol.Header(output, packet); write?.Invoke(output); });
    public PrivateMessage Queue(string target, string name, string text) => QueueMessage(target, name, text, MessageKind.Private, "");
    internal PrivateMessage QueueMessage(string target, string name, string text, MessageKind kind, string thread) {
        if (!ready || kind == MessageKind.Private && !Allows(InteractionAction.SendPrivateMessage) || recovery == null || outbox.Count >= 32 || !InteractionProtocol.ValidText(text, InteractionProtocol.MaxMessageLength)) { throw new InvalidOperationException("Проверьте подключение, длину сообщения и размер очереди"); }
        var message = new PrivateMessage { Id = Guid.NewGuid().ToString("N"), From = account, To = target, SenderName = GameMain.Client.Name, RecipientName = name,
            Text = text, Kind = kind, Thread = thread, Status = MessageStatus.Queued, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        TrimMessages();
        messages.Add(account + ":" + message.Id, message);
        retry.Add(message.Id, 0);
        outbox.Add(message);
        try { recovery.SaveOutbox(outbox); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { outbox.Remove(message); retry.Remove(message.Id); messages.Remove(account + ":" + message.Id); throw new InvalidOperationException("Не удалось сохранить очередь сообщений: " + error.Message); }
        MessagesChanged?.Invoke();
        return message;
    }
    public void Read(PrivateMessage message) {
        if (message.Kind != MessageKind.Private || message.To != account || message.Status >= MessageStatus.Read || message.Sequence == 0) { return; }
        Receipt(message.From, message.Id, true);
    }
    private void Receipt(string from, string id, bool read) {
        var key = (from, id);
        if (receipts.TryGetValue(key, out var pending)) {
            if (read && !pending.Read) { receipts[key] = (true, 0); }
        } else if (receipts.Count < 2000) { receipts.Add(key, (read, 0)); }
    }
    private void FlushReceipts(double now) {
        if (receipts.Count == 0 || now < nextReceipts) { return; }
        nextReceipts = now + .25;
        receiptBatch.Clear();
        foreach (var receipt in receipts) {
            if (receipt.Value.After > now) { continue; }
            receiptBatch.Add((receipt.Key.From, receipt.Key.Id, receipt.Value.Read));
            if (receiptBatch.Count == 8) { break; }
        }
        if (receiptBatch.Count == 0) { return; }
        if (!Send(InteractionPacket.ReceiptBatch, output => {
            output.WriteByte((byte)receiptBatch.Count);
            foreach (var receipt in receiptBatch) { output.WriteString(receipt.From); output.WriteString(receipt.Id); output.WriteBoolean(receipt.Read); }
        })) { return; }
        foreach (var receipt in receiptBatch) { receipts[(receipt.From, receipt.Id)] = (receipt.Read, now + 2); }
    }
    private void Receive(IReadMessage message) {
        if (!InteractionProtocol.Read(message, out var packet)) { return; }
        switch (packet) {
            case InteractionPacket.Flicker:
                ReceiveFlicker(message); break;
            case InteractionPacket.GMAnnouncementReceipt:
                ReadAnnouncementReceipt(message); break;
            case InteractionPacket.GMAnnouncement:
                DisplayAnnouncement(GMAnnouncement.Read(message)); break;
            case InteractionPacket.Snapshot:
                uint oldPermissions = permissions;
                byte oldAnnouncementFormats = announcementFormats;
                string authenticated = message.ReadString();
                uint nextPermissions = message.ReadUInt32(); byte nextFormats = message.ReadByte();
                string scope = endpoint + "|" + authenticated;
                if (account.Length > 0 && account != authenticated) { ClearMessages(); outbox.Clear(); retry.Clear(); views.Reset(); }
                account = authenticated;
                if (recoveryScope != scope) {
                    views.Reset();
                    recovery = null; recoveryScope = scope; nextRecoveryLoad = 0; recoveryError = "";
                    ClearMessages(); outbox.Clear(); retry.Clear();
                    views.BindRecovery(null);
                }
                permissions = nextPermissions; announcementFormats = nextFormats;
                if ((oldPermissions & 1u << (int)InteractionAction.ViewGMFeed) != 0 && !Allows(InteractionAction.ViewGMFeed)) {
                    var removed = new List<string>();
                    foreach (var entry in messages) { if (entry.Value.Kind != MessageKind.Private && entry.Value.Thread != account) { removed.Add(entry.Key); } }
                    foreach (string id in removed) { messages.Remove(id); }
                    messageOrder.Clear(); foreach (var entry in messages) { if (entry.Value.Sequence > 0) { messageOrder.Enqueue(entry.Key, entry.Value.Sequence); } }
                    views.Reset(); MessagesChanged?.Invoke();
                }
                Peers.Clear();
                int peers = message.ReadByte();
                if (peers > 128) { throw new InvalidOperationException("Invalid peer count"); }
                for (int i = 0; i < peers; i++) { Peers.Add(new Peer { Account = message.ReadString(), Name = message.ReadString(), Character = message.ReadUInt16() }); }
                if (message.BitPosition > message.LengthBits) { throw new ArgumentException("Оборвано согласование Primary Package"); }
                ready = true;
                if (recovery == null && GUIRuntime.Now >= nextRecoveryLoad) { LoadLocalRecovery(GUIRuntime.Now); }
                if (oldAnnouncementFormats != announcementFormats) { views.RefreshAnnouncementFormats(); }
                MessagesChanged?.Invoke();
                break;
            case InteractionPacket.Message:
                var incoming = new PrivateMessage { Kind = (MessageKind)message.ReadByte(), Thread = message.ReadString(), Sequence = message.ReadInt64(), Id = message.ReadString(), From = message.ReadString(), To = message.ReadString(), SenderName = message.ReadString(), RecipientName = message.ReadString(), Text = message.ReadString(), Timestamp = message.ReadInt64(), Status = (MessageStatus)message.ReadByte() };
                if (!CanView(incoming) || incoming.Sequence <= 0 || !Guid.TryParseExact(incoming.Id, "N", out _) || !Enum.IsDefined(incoming.Status)) { return; }
                string key = incoming.From + ":" + incoming.Id;
                bool known = messages.TryGetValue(key, out var existing);
                if (known && existing.Sequence > 0 && existing.Sequence != incoming.Sequence) { return; }
                if (!known) { TrimMessages(); }
                if (!known || existing.Sequence == 0) { messageOrder.Enqueue(key, incoming.Sequence); }
                if (!known || existing.Status <= incoming.Status) { messages[key] = incoming; }
                if (incoming.From == account) {
                    bool changed = false;
                    for (int i = outbox.Count - 1; i >= 0; i--) { if (outbox[i].Id == incoming.Id) { outbox.RemoveAt(i); retry.Remove(incoming.Id); changed = true; } }
                    if (changed) { try { recovery.SaveOutbox(outbox); } catch (System.IO.IOException error) { views.Error("Подтверждение получено, но очередь не сохранена: " + error.Message); } }
                } else {
                    if (incoming.Kind == MessageKind.Private) {
                        var receiptKey = (incoming.From, incoming.Id);
                        if (receipts.TryGetValue(receiptKey, out var pendingReceipt) && incoming.Status >= (pendingReceipt.Read ? MessageStatus.Read : MessageStatus.Delivered)) { receipts.Remove(receiptKey); }
                        if (incoming.Status < MessageStatus.Delivered) { Receipt(incoming.From, incoming.Id, known && existing.Status == MessageStatus.Read); }
                    }
                    if (!known && !replaying && incoming.Kind != MessageKind.Roleplay && incoming.Status < MessageStatus.Read) { views.Notify(incoming); }
                }
                MessagesChanged?.Invoke();
                break;
            case InteractionPacket.HistoryEnd:
                replaying = false; MessagesChanged?.Invoke();
                break;
            case InteractionPacket.Error:
                foreach (PrivateMessage queued in outbox) { retry[queued.Id] = GUIRuntime.Now + 30; }
                views.Error(message.ReadString());
                break;
        }
    }
    private void LoadLocalRecovery(double now) {
        nextRecoveryLoad = now + 5;
        try {
            var loaded = new InteractionRecovery(endpoint, account);
            views.BindRecovery(loaded, preserveCurrent: true);
            recovery = loaded;
            foreach (var queued in loaded.Data.Outbox) {
                string key = account + ":" + queued.Id;
                if (queued.From != account || messages.TryGetValue(key, out var received) && received.Sequence > 0) { continue; }
                outbox.Add(queued); messages[key] = queued; retry[queued.Id] = 0;
            }
            recoveryError = "";
            TryBindAnnouncements(loaded);
        } catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException) {
            string message = "Локальные сообщения и черновики недоступны: " + error.Message + ". Вход в игровой мир продолжается";
            if (message != recoveryError) { recoveryError = message; ModLog.Error(message); views.Error(message); }
        }
    }
    private void ClearMessages() { receipts.Clear(); messages.Clear(); messageOrder.Clear(); }
    private void TrimMessages() {
        while (messages.Count >= 2000 && messageOrder.TryDequeue(out string key, out _)) { messages.Remove(key); }
    }
    private bool CanView(PrivateMessage message) => MessageAccess.CanView(message, account, Allows(InteractionAction.ViewGMFeed));
    private void Shortcut(Keys key) {
        if (!PlayView.Running) { return; }
        if (key == Keys.F5) { views.ToggleGMMessage(); GUIRuntime.Instance.Input.ConsumePointer(); }
        else if (key == Keys.F2) { views.ToggleInteraction(); GUIRuntime.Instance.Input.ConsumePointer(); }
    }
}
