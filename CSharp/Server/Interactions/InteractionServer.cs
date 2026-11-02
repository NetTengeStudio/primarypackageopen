using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer : IModule, ILoadCompleted, IEventUpdate, IEventClientConnected, IEventClientDisconnected, IEventRoundEnded, IEventChatMessage
{
    private sealed class Session
    {
        public Client Client;
        public string Account;
        public double Refill, NextMessage, NextQuery, NextSpawn;
        public double Tokens = 32;
        public int HistoryCursor;
        public bool Syncing;
        public readonly List<PrivateMessage> Replay = new();
        public double NextRoleplay, NextAction, NextDirector, NextAnnouncement;
        public readonly List<(DeviceLease Lease, ushort Component, string Field)> DeviceReplay = new();
        public int DeviceCursor;
        public bool WorldReady, WorldReadyRequested;
        public WorldBootstrapPeer Bootstrap;
        public double WorldIngressBytes = 4096, WorldIngressAt;
        public ushort NativeSyncProgress;
        public double NativeSyncProgressAt;
        public PendingTranslation Translation;
        public readonly Queue<ConfirmedTranslation> TranslationOutbox = new();
        public readonly Dictionary<Guid, bool> PendingTransformSnapshots = new();
        public readonly HashSet<Guid> TransformSnapshots = new();
        public WorldCapabilities Capabilities;
        public Guid InspectedDevice;
        public readonly Dictionary<(ushort Component, string Field), (string Value, ulong Revision)> DeviceObserved = new();
        public readonly List<(ushort Component, DeviceEditor.Field Field)> DeviceSchema = new();
        public int DeviceObserveCursor;
        public readonly List<DeviceSnapshotValue> DeviceSnapshot = new();
        public DeviceLease DeviceSnapshotLease;
        public int DeviceSnapshotCursor;
        public readonly HashSet<Guid> DeviceBindings = new();
        public readonly Queue<DeviceLease> DeviceOffers = new();
        public readonly HashSet<Guid> PendingDeviceOffers = new();
        public readonly Dictionary<string, double> Actions = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, GMAnnouncementReceipt> Announcements = new();
        public readonly Queue<Guid> AnnouncementOrder = new();
    }
    private readonly Dictionary<byte, Session> sessions = new();
    private readonly List<WorldQueryResult> query = new();
    private PermissionService permissions;
    private PrivateMessageService messages;
    private GMToolsAdapter gmTools;
    private double nextMaintenance;
    private bool live;
    private IDisposable receiver;
    private bool rosterDirty;
    private sealed class DirectedState { public string Actor; public DirectorEvent Event; public double End; }
    private readonly Dictionary<string, DirectedState> activeDirected = new(StringComparer.Ordinal);
    private long directorSequence;
    private readonly List<DirectorEvent> directedEvents = new(16);
    private static InteractionServer instance;
    internal static bool AllowsAction(Client client, InteractionAction action) => instance?.live == true && instance.permissions.Allows(client, action);
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public void Attach(Harmony harmony)
    {
        instance = this;
        NativeWorld.Attach(harmony); AttachWorldBootstrap(harmony);
        WorldQueryService.Instance.Attach(harmony);
        LightEffectService.Attach(harmony);
        harmony.Patch(AccessTools.Method(typeof(Client), nameof(Client.SetPermissions)), postfix: ModuleHost.Bind(typeof(InteractionServer), nameof(RosterChanged)));
        harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.SetClientCharacter)), postfix: ModuleHost.Bind(typeof(InteractionServer), nameof(RosterChanged)));
        harmony.Patch(AccessTools.Method(typeof(ServerEntityEventManager), nameof(ServerEntityEventManager.InitClientMidRoundSync)), prefix: ModuleHost.Bind(typeof(InteractionServer), nameof(AllowEditedWorldSync)));
        receiver = ModNet.Listen(InteractionProtocol.Channel, Receive);
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventClientConnected>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventRoundEnded>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventChatMessage>(this);
    }
    public void LoadCompleted()
    {
        string directory = ModAssets.Resolve("Data/Interactions");
        permissions = new PermissionService(directory);
        try { messages = new PrivateMessageService(directory); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { ModLog.Error("Private messages unavailable; journal retained: " + error.Message); }
        DirectorCatalog.Load();
        gmTools = new GMToolsAdapter(permissions);
        gmTools.TryAttach();
        InitializeScenes();
        live = true;
    }
    public void RoundStart()
    {
        rosterDirty = true;
        scenes.Clear();
        ResetWorldBootstrap(); ClearDevices();
        activeDirected.Clear();
        query.Clear();
        WorldQueryService.Instance.Clear();
        CharacterCreationService.Instance.Clear();
        LightEffectService.Instance.Clear();
    }
    public void Release()
    {
        live = false;
        receiver?.Dispose(); receiver = null; worldReceiver?.Dispose(); worldReceiver = null; NativeWorld.Clear();
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventClientConnected>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventRoundEnded>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventChatMessage>(this);
        messages?.Dispose();
        gmTools?.Dispose();
        gmTools = null;
        messages = null;
        permissions = null;
        sessions.Clear();
        RoundStart();
        instance = null;
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
                    WorldCapabilities capabilities = (WorldCapabilities)message.ReadUInt32(); byte schema = message.ReadByte();
                    if (schema != WorldContract.SchemaVersion || (capabilities & WorldContract.Supported) != WorldContract.Supported) {
                        GameMain.Server.DisconnectClient(sender, PeerDisconnectPacket.Custom("Несовместимая версия Primary Package Live Editor: требуется схема " + WorldContract.SchemaVersion + ". Обновите клиент и сервер вместе")); return;
                    }
                    session.Capabilities = capabilities;
                    SendSnapshot(session);
                    foreach (var effect in LightEffectService.Instance.Active)
                    {
                        int index = -1;
                        for (int c = 0; c < effect.Component.Item.Components.Count && c <= byte.MaxValue; c++) { if (effect.Component.Item.Components[c] == effect.Component) { index = c; break; } }
                        if (index < 0) { continue; }
                        byte component = (byte)index;
                        ModNet.Send(InteractionProtocol.Channel, sender.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.Flicker); output.WriteUInt16(effect.Component.Item.ID); output.WriteByte(component); output.WriteSingle(effect.Remaining); output.WriteString(effect.Id.ToString("N")); });
                    }
                    if (messages == null) { break; }
                    session.Replay.Clear();
                    session.Replay.AddRange(messages.History(session.Account));
                    session.Replay.AddRange(permissions.Allows(sender, InteractionAction.ViewGMFeed) ? messages.StaffHistory : messages.SupportHistory(session.Account));
                    session.Replay.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
                    session.HistoryCursor = Math.Max(0, session.Replay.Count - 1000);
                    session.Syncing = true;
                    break;
                case InteractionPacket.Query:
                    bool characters = message.ReadBoolean();
                    Require(sender, characters ? InteractionAction.QueryCharacters : InteractionAction.QueryItems);
                    if (now < session.NextQuery) { return; }
                    session.NextQuery = now + .15;
                    Vector2 center = new(message.ReadSingle(), message.ReadSingle());
                    float radius = message.ReadSingle();
                    string filter = message.ReadString();
                    if (filter.Length > 80) { throw new ArgumentException("Слишком длинный фильтр"); }
                    WorldQueryService.Instance.Query(center, radius, characters, filter, query);
                    ModNet.Send(InteractionProtocol.Channel, sender.Connection, output =>
                    {
                        InteractionProtocol.Header(output, InteractionPacket.QueryResult);
                        output.WriteUInt16((ushort)query.Count);
                        foreach (var result in query)
                        {
                            Entity entity = result.Entity;
                            output.WriteUInt16(entity.ID);
                            output.WriteBoolean(entity is Character);
                            output.WriteSingle(result.DistanceSquared);
                            output.WriteString(entity is Character c ? c.Name : ((Item)entity).Name);
                            output.WriteString(entity is Character character ? (character.IsDead ? "Мёртв" : "Жив") + " · " + (character.Info?.Job?.Prefab.Identifier.Value ?? "") : "Состояние " + ((Item)entity).Condition.ToString("F0") + " · " + (((Item)entity).Container?.Name ?? "Мир"));
                        }
                    });
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
                case InteractionPacket.EntityAction:
                    Require(sender, InteractionAction.ManageEntities);
                    string requestId = message.ReadString();
                    ushort entityId = message.ReadUInt16();
                    var operation = (EntityOperation)message.ReadByte();
                    string value = message.ReadString();
                    Vector2 point = new(message.ReadSingle(), message.ReadSingle());
                    if (!Guid.TryParseExact(requestId, "N", out _) || value.Length > 128 || !Enum.IsDefined(operation)) { throw new ArgumentException("Недопустимое действие"); }
                    if (session.Actions.ContainsKey(requestId)) { break; }
                    if (now < session.NextAction) { throw new InvalidOperationException("Подождите завершения действия"); }
                    session.NextAction = now + .2;
                    if (operation is EntityOperation.SpawnCreature or EntityOperation.SpawnDormantCreature) { Require(sender, InteractionAction.SpawnCharacter); if (now < session.NextSpawn) { throw new InvalidOperationException("Создание уже выполняется"); } session.NextSpawn = now + 2; }
                    EntityActions.Execute(entityId, operation, value, point);
                    session.Actions.Add(requestId, now);
                    ModLog.Message("GM action " + session.Account + " " + operation + " " + entityId);
                    break;
                case InteractionPacket.DirectorFailure:
                    string failedId = message.ReadString(), reason = message.ReadString();
                    if (!Guid.TryParseExact(failedId, "N", out Guid failedEffect) || !InteractionProtocol.ValidText(reason, 160) ||
                        !activeDirected.TryGetValue(failedId, out var failedState) || failedState.Event.Target.Length > 0 && failedState.Event.Target != session.Account) { throw new UnauthorizedAccessException("Недопустимый отчёт об эффекте"); }
                    if (failedState.Event.Target.Length > 0) { activeDirected.Remove(failedId); scenes.FailEffect(failedEffect, reason); PublishSceneChanges(); }
                    foreach (Session observer in sessions.Values) {
                        if (!permissions.Allows(observer.Client, InteractionAction.DirectEvents)) { continue; }
                        ModNet.Send(InteractionProtocol.Channel, observer.Client.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.DirectorFailure); output.WriteString(failedId); output.WriteString(sender.Name + ": " + reason); });
                        SendDirectorStatus(observer.Client);
                    }
                    ModLog.Error("Director client failure " + session.Account + " " + failedId + ": " + reason);
                    break;
                case InteractionPacket.WorldBootstrapApplied:
                    AcknowledgeBootstrapReplay(message, session, now); break;
                case InteractionPacket.GMAnnouncement:
                    Announce(message, session, now); break;
                case InteractionPacket.RevivePlayer:
                    if (now < session.NextSpawn) { throw new InvalidOperationException("Возрождение уже выполняется"); }
                    session.NextSpawn = now + 2; RevivePlayer(message, session); break;
                case InteractionPacket.DeviceUninspect:
                    session.InspectedDevice = Guid.Empty; session.DeviceSnapshotLease = null; session.DeviceSnapshot.Clear(); session.DeviceSchema.Clear(); session.DeviceObserved.Clear(); break;
                case InteractionPacket.WorldExport:
                    ExportWorldEdits(session); break;
                case InteractionPacket.WorldTranslate:
                    Require(sender, InteractionAction.ManageEntities); TranslateWorld(message, session); break;
                case InteractionPacket.WorldTransformSelection:
                    Require(sender, InteractionAction.ManageEntities); InspectTransforms(message, session); break;
                case InteractionPacket.WorldEditFailure:
                    ReportEditFailure(message, session); break;
                case InteractionPacket.WorldReady:
                    QueueDeviceReplay(session, now);
                    break;
                case InteractionPacket.DeviceBindAck:
                    BindDevice(message, session);
                    break;
                case InteractionPacket.EditHistory:
                    Require(sender, InteractionAction.ManageEntities); SendEditHistory(session);
                    break;
                case InteractionPacket.EditReverse:
                    Require(sender, InteractionAction.ManageEntities); ReverseEdit(message, session);
                    break;
                case InteractionPacket.DeviceInspect:
                    Require(sender, InteractionAction.ManageEntities);
                    InspectDevice(sender, message.ReadUInt16());
                    break;
                case InteractionPacket.DeviceChange:
                    Require(sender, InteractionAction.ManageEntities);
                    if (now < session.NextAction) { throw new InvalidOperationException("Подождите завершения действия"); }
                    session.NextAction = now + .2;
                    ChangeDevice(message, sender);
                    break;
                case InteractionPacket.ScenePut:
                    Require(sender, InteractionAction.DirectEvents);
                    Guid sceneId = Guid.ParseExact(message.ReadString(), "N");
                    uint revision = message.ReadUInt32();
                    SceneProgram program = SceneWire.Read(message, out SceneBindings bindings);
                    scenes.Arm(sceneId, revision, session.Account, program, bindings, now, sceneWorld);
                    PublishSceneChanges();
                    break;
                case InteractionPacket.SceneRemove:
                    Require(sender, InteractionAction.DirectEvents);
                    scenes.Remove(Guid.ParseExact(message.ReadString(), "N"), message.ReadUInt32());
                    PublishSceneChanges();
                    break;
                case InteractionPacket.SceneList:
                    Require(sender, InteractionAction.DirectEvents);
                    SendScenes(sender);
                    break;
                case InteractionPacket.DirectorList:
                    Require(sender, InteractionAction.DirectEvents);
                    SendDirectorStatus(sender);
                    break;
                case InteractionPacket.Director:
                case InteractionPacket.DirectorBatch:
                    Require(sender, InteractionAction.DirectEvents);
                    int eventCount = packet == InteractionPacket.DirectorBatch ? message.ReadByte() : 1;
                    if (eventCount < 1 || eventCount > 16) { throw new ArgumentException("Недопустимый размер последовательности"); }
                    directedEvents.Clear();
                    for (int i = 0; i < eventCount; i++) {
                        DirectorEvent directed = DirectorEvent.Read(message);
                        DirectorEvent.Validate(directed);
                        if (GameMain.GameSession?.IsRunning != true && directed.Kind is not (DirectorKind.Stop or DirectorKind.StopOne)) { throw new InvalidOperationException("Раунд не запущен"); }
                        if (session.Actions.ContainsKey(directed.Id)) { continue; }
                        if (directed.Target.Length == 0 && directed.Kind is DirectorKind.Sound or DirectorKind.Scare or DirectorKind.Hallucination) { throw new ArgumentException("Выберите игрока для персонального эффекта"); }
                        if (directed.Kind == DirectorKind.StopOne) {
                            if (!activeDirected.TryGetValue(directed.Resource, out var stopping)) { throw new ArgumentException("Событие уже завершилось"); }
                            directed.Target = stopping.Event.Target;
                        }
                        if (directed.Kind is not (DirectorKind.Stop or DirectorKind.StopOne) && directed.Target.Length > 0 && FindAccount(directed.Target) == null) { throw new ArgumentException("Игрок отключился"); }
                        for (int j = 0; j < directedEvents.Count; j++) { if (directedEvents[j].Id == directed.Id) { throw new ArgumentException("Повтор идентификатора события"); } }
                        directedEvents.Add(directed);
                    }
                    if (directedEvents.Count == 0) { break; }
                    int additions = 0; foreach (var valueEvent in directedEvents) { if (valueEvent.Kind is not (DirectorKind.Stop or DirectorKind.StopOne)) { additions++; } }
                    PruneDirected(now);
                    if (activeDirected.Count + additions > 16) { throw new InvalidOperationException("Уже запланировано 16 событий; остановите ненужные"); }
                    if (now < session.NextDirector && !(eventCount == 1 && directedEvents[0].Kind is DirectorKind.Stop or DirectorKind.StopOne)) { throw new InvalidOperationException("Подождите перед следующим событием"); }
                    session.NextDirector = now + .25;
                    foreach (var directed in directedEvents) {
                        PublishDirector(directed, session.Account);
                        session.Actions.Add(directed.Id, now);
                        ModLog.Message("Director " + session.Account + " " + directed.Kind + " " + directed.Target);
                    }
                    SendDirectorStatus(sender);
                    break;
                case InteractionPacket.PermissionList:
                    Require(sender, InteractionAction.ManagePermissions);
                    SendPermissions(sender, message.ReadString());
                    break;
                case InteractionPacket.PermissionChange:
                    Require(sender, InteractionAction.ManagePermissions);
                    string account = message.ReadString();
                    InteractionAction action = (InteractionAction)message.ReadByte();
                    byte mode = message.ReadByte();
                    if (mode > 2) { throw new ArgumentException("Invalid permission mode"); }
                    permissions.Set(sender, account, action, mode == 2 ? null : mode == 1);
                    SendPermissions(sender, account);
                    foreach (var connected in sessions.Values) { SendSnapshot(connected); }
                    break;
                case InteractionPacket.Spawn:
                    Require(sender, InteractionAction.SpawnCharacter);
                    if (now < session.NextSpawn) { throw new InvalidOperationException("Создание уже выполняется"); }
                    session.NextSpawn = now + 2;
                    var template = new CharacterTemplate
                    {
                        Name = message.ReadString(),
                        Job = message.ReadString(),
                        Head = message.ReadUInt16(),
                        Hair = message.ReadUInt16(),
                        Beard = message.ReadUInt16(),
                        Moustache = message.ReadUInt16(),
                        Attachment = message.ReadUInt16(),
                        SkinColor = message.ReadUInt16(),
                        HairColor = message.ReadUInt16(),
                        FacialHairColor = message.ReadUInt16(),
                        Control = message.ReadBoolean()
                    };
                    Vector2 position = new(message.ReadSingle(), message.ReadSingle());
                    if (template.Control) { Require(sender, InteractionAction.ControlCharacter); }
                    CharacterCreationService.Instance.Spawn(template, position, character =>
                    {
                        if (!live || !template.Control || sender.Connection == null || !permissions.Allows(sender, InteractionAction.ControlCharacter)) { return; }
                        GameMain.Server.SetClientCharacter(sender, character);
                    });
                    break;
                case InteractionPacket.Flicker:
                    Require(sender, InteractionAction.FlickerLight);
                    
                    ushort itemId = message.ReadUInt16();
                    byte componentIndex = message.ReadByte();
                    if (Entity.FindEntityByID(itemId) is not Item item || item.Removed || componentIndex >= item.Components.Count || item.Components[componentIndex] is not LightComponent light) { throw new ArgumentException("Источник света не найден"); }
                    PublishLight(Guid.NewGuid(), item, componentIndex, 3);
                    break;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or AggregateException)
        {
            if (error is AggregateException) { ModLog.Error("World transaction restoration failed: " + error); }
            ModNet.Send(InteractionProtocol.Channel, sender.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.Error); output.WriteString(error is IOException ? "Хранилище недоступно: операция не подтверждена" : error.Message); });
            ModLog.Once("Interaction request rejected: " + error.Message);
        }
    }
    private void SendSnapshot(Session session)
    {
        ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output =>
        {
            InteractionProtocol.Header(output, InteractionPacket.Snapshot);
            output.WriteString(session.Account); output.WriteString(worldEpoch.ToString("N"));
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
    private void SendPermissions(Client recipient, string account)
    {
        if (!PermissionService.ValidAccount(account)) { throw new ArgumentException("Выберите учётную запись"); }
        ModNet.Send(InteractionProtocol.Channel, recipient.Connection, output =>
        {
            InteractionProtocol.Header(output, InteractionPacket.PermissionResult);
            output.WriteString(account);
            foreach (InteractionAction action in Enum.GetValues<InteractionAction>())
            {
                byte mode = 2;
                foreach (var grant in permissions.Grants) { if (grant.Account == account && grant.Action == action) { mode = grant.Allowed ? (byte)1 : (byte)0; break; } }
                output.WriteByte(mode);
            }
            int count = 0;
            for (int i = permissions.Audit.Count - 1; i >= 0 && count < 16; i--) { if (permissions.Audit[i].Account == account) { count++; } }
            output.WriteByte((byte)count);
            for (int i = permissions.Audit.Count - 1; i >= 0 && count > 0; i--)
            {
                var change = permissions.Audit[i];
                if (change.Account != account) { continue; }
                output.WriteString(change.By); output.WriteByte((byte)change.Action); output.WriteByte(change.Inherited ? (byte)2 : change.Allowed ? (byte)1 : (byte)0); output.WriteInt64(change.ChangedAt);
                count--;
            }
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
        ModNet.Send(InteractionProtocol.Channel, recipient.Connection, output =>
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
        LightEffectService.Instance.Update();
        double now = Now;
        if (now >= nextSceneTick) { nextSceneTick = now + .1; scenes.Advance(now); PublishSceneChanges(); }
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
                if (!session.Syncing) { history.Clear(); ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => InteractionProtocol.Header(output, InteractionPacket.HistoryEnd)); }
            }
        }
        foreach (Session session in sessions.Values) { if (session.WorldReadyRequested && !session.WorldReady) { QueueDeviceReplay(session, now); } }
        PumpWorldBootstrap(now);
        ReportWorldExports();
        PumpTranslations(now);
        ObserveDevices(now);
        foreach (Session session in sessions.Values) {
            if (!session.WorldReady && session.Bootstrap?.Released != true) { continue; }
            if (!PumpTranslationOutbox(session)) { continue; }
            PumpTransformSnapshots(session);
            PumpDeviceOffers(session); PumpDeviceSnapshot(session);
            int limit = Math.Min(session.DeviceReplay.Count, session.DeviceCursor + 8);
            for (; session.DeviceCursor < limit; session.DeviceCursor++) {
                var edit = session.DeviceReplay[session.DeviceCursor];
                if (edit.Lease.Current && session.DeviceBindings.Contains(edit.Lease.Id)) { ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => WriteDeviceDelta(output, edit.Lease, edit.Component, edit.Field)); }
            }
            if (session.DeviceCursor >= session.DeviceReplay.Count) { session.DeviceReplay.Clear(); session.DeviceCursor = 0; }
        }
        if (now < nextMaintenance) { return; }
        nextMaintenance = now + 1;
        PruneDirected(now);
        PruneDevices();
        gmTools.TryAttach();
        foreach (var session in sessions.Values) {
            if (session.Actions.Count == 0) { continue; }
            expiredIds.Clear();
            foreach (var entry in session.Actions) { if (now - entry.Value > 120) { expiredIds.Add(entry.Key); } }
            foreach (string id in expiredIds) { session.Actions.Remove(id); }
        }
    }
    private void PruneDirected(double now) {
        expiredIds.Clear(); foreach (var entry in activeDirected) { if (entry.Value.End <= now) { expiredIds.Add(entry.Key); } }
        foreach (string id in expiredIds) { activeDirected.Remove(id); }
    }
    private void SendDirectorStatus(Client client) {
        PruneDirected(Now);
        ModNet.Send(InteractionProtocol.Channel, client.Connection, output => {
            InteractionProtocol.Header(output, InteractionPacket.DirectorStatus); output.WriteByte((byte)activeDirected.Count);
            foreach (var entry in activeDirected.Values) {
                output.WriteString(entry.Event.Id); output.WriteString(entry.Actor); output.WriteString(entry.Event.Target); output.WriteByte((byte)entry.Event.Kind);
                string label = DirectorCatalog.Describe(entry.Event);
                output.WriteString(label.Length > 80 ? label.Substring(0, 80) : label); output.WriteSingle((float)Math.Max(0, entry.End - Now));
            }
        });
    }
    private readonly List<string> expiredIds = new();
    internal static void RecordEmote(Client sender, string text) { if (!string.IsNullOrWhiteSpace(text)) { instance?.OnChatMessage("*" + text + "*", sender, ChatMessageType.Default, null); } }
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

