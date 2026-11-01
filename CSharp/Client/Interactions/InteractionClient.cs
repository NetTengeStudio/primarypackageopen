using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient : IModule, ILoadCompleted, IEventUpdate, IEventRoundEnded {
    internal static InteractionClient Instance { get; private set; }
    internal sealed class Peer {
        public string Account, Name;
        public ushort Character;
    }
    internal sealed class ActiveEffect {
        public string Id, Actor, Target, Name;
        public DirectorKind Kind;
        public float Remaining;
    }
    internal readonly List<ActiveEffect> Effects = new();
    internal event Action EffectsChanged;
    internal void RequestEffects() {
        if (!Multiplayer) { playback?.Snapshot(Effects); EffectsChanged?.Invoke(); return; }
        Send(InteractionPacket.DirectorList, null);
    }
    internal sealed class QueryRow {
        public ushort Id;
        public bool Character;
        public string Name, Detail;
        public float Distance;
    }
    private readonly Dictionary<string, PrivateMessage> messages = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, long> messageOrder = new(2048);
    private readonly Dictionary<string, double> retry = new(StringComparer.Ordinal);
    private readonly List<PrivateMessage> outbox = new();
    internal readonly List<Peer> Peers = new();
    internal readonly List<QueryRow> Results = new();
    private InteractionViews views;
    private IDisposable receiver;
    private string account = "", endpoint = "";
    private uint permissions;
    private double nextHello, nextReceipts;
    private readonly Dictionary<(string From, string Id), (bool Read, double After)> receipts = new();
    private readonly List<(string From, string Id, bool Read)> receiptBatch = new(8);
    private bool ready;
    private bool replaying;
    private DirectorPlayback playback;
    private InteractionRecovery recovery;
    private string recoveryScope = "";
    internal InteractionRecovery Recovery => recovery;
    private long directorSequence;
    public bool Connected => ready;
    internal int ActiveEffects => playback?.Count ?? 0;
    private bool hubRequested;
    private NetworkConnection lastConnection;
    public event Action MessagesChanged;
    public event Action ResultsChanged;
    public event Action<string, byte[], PermissionAuditEntry[]> PermissionsChanged;
    public string Account => account;
    public IEnumerable<PrivateMessage> Messages => messages.Values;
    public bool Multiplayer => GameMain.Client != null;
    public bool Allows(InteractionAction action) => !Multiplayer || ready && (permissions & 1u << (int)action) != 0;

    public void Attach(Harmony harmony) {
        Instance = this;
        WorldQueryService.Instance.Attach(harmony);
        LightEffectService.Attach(harmony);
        receiver = ModNet.Listen(InteractionProtocol.Channel, Receive);
        playback = new DirectorPlayback(DirectorFailed);
        InitializeScenes();
        PhantomRenderer.Attach(harmony);
        views = new InteractionViews(this); InitializeLiveEditor();
        GUIRuntime.Instance.Input.Shortcut += Shortcut;
        GUIRuntime.Instance.Input.WorldPressed += WorldPressed;
        GUIRuntime.Instance.Updating += UpdateView;
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventRoundEnded>(this);
        ModCommands.Register("ppgui", "Открыть панель взаимодействий Primary Package", _ => hubRequested = true, localOnClient: true);
        ModCommands.Register("ppgui_profile", "Измерения этапов PrimaryGUI", _ => {
            GUIRuntime.Instance.Profiler.Enabled = true;
            foreach (GUIPhase phase in Enum.GetValues<GUIPhase>()) { DebugConsole.NewMessage(GUIRuntime.Instance.Profiler.Snapshot(phase)); }
        }, localOnClient: true);
    }
    public void LoadCompleted() => DirectorCatalog.Load();
    public void RoundStart() {
        localScenes.Clear(); SceneRows.Clear(); ResetDevices();
        WorldQueryService.Instance.Clear(); CharacterCreationService.Instance.Clear(); LightEffectService.Instance.Clear(); views?.Reset(); playback?.Reset();
        worldSelection = null; worldSelectionCancelled = null; Effects.Clear(); EffectsChanged?.Invoke(); picks.Clear(); Results.Clear(); lastLight = null; lastComponent = -1; lastLightClick = 0;
    }
    public void OnRoundEnd() => RoundStart();
    public void Release() {
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventRoundEnded>(this);
        RoundStart();
        if (GUIRuntime.Instance != null) { GUIRuntime.Instance.Input.Shortcut -= Shortcut; GUIRuntime.Instance.Input.WorldPressed -= WorldPressed; GUIRuntime.Instance.Updating -= UpdateView; }
        receiver?.Dispose(); receiver = null; editorOverlay?.Dispose(); editorOverlay = null;
        views?.Dispose(); playback?.Dispose(); playback = null;
        ClearMessages(); outbox.Clear(); retry.Clear(); Peers.Clear();
        Instance = null;
        MessagesChanged = null; EffectsChanged = null; ResultsChanged = null; PermissionsChanged = null;
    }
    private void UpdateView() { ApplyDeviceDeltas(); UpdateLiveEditor(); if (hubRequested && PlayView.Running) { hubRequested = false; views.OpenHub(); } LightEffectService.Instance.Update(); playback?.Update(); if (!Multiplayer && GUIRuntime.Now >= nextSceneTick) { nextSceneTick = GUIRuntime.Now + .1; localScenes.Advance(GUIRuntime.Now); if (localSceneVersion != localScenes.Version) { RequestScenes(); } } views.Update(); }
    public void OnUpdate(double fixedDeltaTime) {
        var connection = GameMain.Client?.ClientPeer?.ServerConnection;
        if (connection != lastConnection) { ResetDevices(); localScenes.Clear(); lastConnection = connection; receipts.Clear(); directorSequence = 0; playback?.Reset(); ready = false; nextHello = 0; }
        string currentEndpoint = connection?.Endpoint?.ToString() ?? "";
        if (currentEndpoint.Length == 0) { ready = false; nextHello = 0; return; }
        if (endpoint != currentEndpoint) {
            if (endpoint.Length > 0) { ClearMessages(); outbox.Clear(); retry.Clear(); Peers.Clear(); views.Reset(); }
            endpoint = currentEndpoint;
            ready = false;
            nextHello = 0;
        }
        double now = GUIRuntime.Now;
        if (!ready && now >= nextHello) { nextHello = now + 3; replaying = true; Send(InteractionPacket.Hello, output => { output.WriteUInt32((uint)WorldContract.Supported); output.WriteByte(WorldContract.SchemaVersion); }); }
        if (!ready) { return; }
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
    private bool Send(InteractionPacket packet, Action<IWriteMessage> write) => ModNet.Send(InteractionProtocol.Channel, output => { InteractionProtocol.Header(output, packet); write?.Invoke(output); });
    public void Query(Vector2 center, float radius, bool characters, string filter) {
        if (!Allows(characters ? InteractionAction.QueryCharacters : InteractionAction.QueryItems)) { return; }
        if (Multiplayer) {
            Send(InteractionPacket.Query, output => { output.WriteBoolean(characters); output.WriteSingle(center.X); output.WriteSingle(center.Y); output.WriteSingle(radius); output.WriteString(filter); });
        } else {
            var results = new List<WorldQueryResult>();
            WorldQueryService.Instance.Query(center, radius, characters, filter, results);
            Results.Clear();
            foreach (var result in results) {
                Entity entity = result.Entity;
                Results.Add(new QueryRow { Id = entity.ID, Character = entity is Character, Name = entity is Character c ? c.Name : ((Item)entity).Name,
                    Distance = MathF.Sqrt(result.DistanceSquared), Detail = entity is Character character ? character.Info?.Job?.Prefab.Identifier.Value ?? "" : "Состояние " + ((Item)entity).Condition.ToString("F0") + " · " + (((Item)entity).Container?.Name ?? "Мир") });
            }
            ResultsChanged?.Invoke();
        }
    }
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
    public void RequestPermissions(string target) => Send(InteractionPacket.PermissionList, message => message.WriteString(target));
    public void SetPermission(string target, InteractionAction action, byte mode) => Send(InteractionPacket.PermissionChange, message => { message.WriteString(target); message.WriteByte((byte)action); message.WriteByte(mode); });
    public void Spawn(CharacterTemplate template, Vector2 position) {
        if (!Allows(InteractionAction.SpawnCharacter) || template.Control && !Allows(InteractionAction.ControlCharacter)) { return; }
        if (!Multiplayer) { bool control = template.Control; CharacterCreationService.Instance.Spawn(template, position, character => { if (control) { Character.Controlled = character; } }); return; }
        Send(InteractionPacket.Spawn, message => {
            message.WriteString(template.Name); message.WriteString(template.Job); message.WriteUInt16((ushort)template.Head); message.WriteUInt16((ushort)template.Hair);
            message.WriteUInt16((ushort)template.Beard); message.WriteUInt16((ushort)template.Moustache); message.WriteUInt16((ushort)template.Attachment);
            message.WriteUInt16((ushort)template.SkinColor); message.WriteUInt16((ushort)template.HairColor); message.WriteUInt16((ushort)template.FacialHairColor);
            message.WriteBoolean(template.Control); message.WriteSingle(position.X); message.WriteSingle(position.Y);
        });
    }
    public void Flicker(Item item, byte component) {
        if (!Allows(InteractionAction.FlickerLight)) { return; }
        if (!Multiplayer) { LightEffectService.Instance.Start((LightComponent)item.Components[component], 3); return; }
        Send(InteractionPacket.Flicker, output => { output.WriteUInt16(item.ID); output.WriteByte(component); });
    }
    private void Receive(IReadMessage message) {
        if (!InteractionProtocol.Read(message, out var packet)) { return; }
        switch (packet) {
            case InteractionPacket.DirectorFailure:
                message.ReadString(); views.Error(message.ReadString()); break;
            case InteractionPacket.GMAnnouncement:
                DisplayAnnouncement(GMAnnouncement.Read(message)); break;
            case InteractionPacket.WorldTransform:
                ReadWorldTransform(message); break;
            case InteractionPacket.DeviceForget:
                ForgetDevice(Guid.ParseExact(message.ReadString(), "N")); break;
            case InteractionPacket.DeviceBind:
                ReadDeviceBind(message); break;
            case InteractionPacket.EditHistory:
                ReadEditHistory(message); break;
            case InteractionPacket.DeviceSnapshot:
                ReadDevice(message); break;
            case InteractionPacket.DeviceDelta:
                ReadDeviceDelta(message); break;
            case InteractionPacket.SceneStatus:
                ReadScenes(message);
                break;
            case InteractionPacket.Snapshot:
                uint oldPermissions = permissions;
                string authenticated = message.ReadString();
                string scope = endpoint + "|" + authenticated;
                if (account.Length > 0 && account != authenticated) { ClearMessages(); outbox.Clear(); retry.Clear(); views.Reset(); }
                account = authenticated;
                if (recoveryScope != scope) {
                    recovery = new InteractionRecovery(endpoint, account); recoveryScope = scope;
                    ClearMessages(); outbox.Clear(); retry.Clear();
                    foreach (var queued in recovery.Data.Outbox) { if (queued.From == account) { outbox.Add(queued); messages[account + ":" + queued.Id] = queued; retry[queued.Id] = 0; } }
                    views.BindRecovery(recovery);
                }
                permissions = message.ReadUInt32(); ready = true;
                if ((oldPermissions & 1u << (int)InteractionAction.DirectEvents) != 0 && !Allows(InteractionAction.DirectEvents)) { SceneRows.Clear(); Effects.Clear(); views.Reset(); }
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
                MessagesChanged?.Invoke();
                break;
            case InteractionPacket.QueryResult:
                int count = message.ReadUInt16();
                if (count > InteractionProtocol.MaxQueryResults) { throw new InvalidOperationException("Invalid query count"); }
                Results.Clear();
                for (int i = 0; i < count; i++) { Results.Add(new QueryRow { Id = message.ReadUInt16(), Character = message.ReadBoolean(), Distance = MathF.Sqrt(message.ReadSingle()), Name = message.ReadString(), Detail = message.ReadString() }); }
                ResultsChanged?.Invoke();
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
            case InteractionPacket.PermissionResult:
                string target = message.ReadString();
                byte[] modes = new byte[Enum.GetValues<InteractionAction>().Length];
                for (int i = 0; i < modes.Length; i++) { modes[i] = message.ReadByte(); }
                int auditCount = message.ReadByte();
                if (auditCount > 16) { return; }
                var audit = new PermissionAuditEntry[auditCount];
                for (int i = 0; i < audit.Length; i++) { audit[i] = new PermissionAuditEntry(message.ReadString(), (InteractionAction)message.ReadByte(), message.ReadByte(), message.ReadInt64()); }
                PermissionsChanged?.Invoke(target, modes, audit);
                break;
            case InteractionPacket.DirectorStatus:
                int effectCount = message.ReadByte(); if (effectCount > 32) { return; }
                Effects.Clear();
                for (int i = 0; i < effectCount; i++) { Effects.Add(new ActiveEffect { Id = message.ReadString(), Actor = message.ReadString(), Target = message.ReadString(), Kind = (DirectorKind)message.ReadByte(), Name = message.ReadString(), Remaining = message.ReadSingle() }); }
                EffectsChanged?.Invoke();
                break;
            case InteractionPacket.HistoryEnd:
                replaying = false; MessagesChanged?.Invoke();
                break;
            case InteractionPacket.DirectorEvent:
                long sequence = message.ReadInt64();
                DirectorEvent directed = DirectorEvent.Read(message);
                if (sequence > directorSequence) { playback.Start(directed); directorSequence = sequence; }
                break;
            case InteractionPacket.Flicker:
                ushort itemId = message.ReadUInt16(); byte component = message.ReadByte(); float duration = message.ReadSingle();
                Guid lease = Guid.ParseExact(message.ReadString(), "N");
                if (itemId == 0 && duration == 0 && lease == Guid.Empty) { LightEffectService.Instance.Clear(); break; }
                if (duration == 0) { LightEffectService.Instance.Cancel(lease); break; }
                if (Entity.FindEntityByID(itemId) is Item item && component < item.Components.Count && item.Components[component] is LightComponent light) { LightEffectService.Instance.Start(light, duration, lease); }
                break;
            case InteractionPacket.Error:
                foreach (PrivateMessage queued in outbox) { retry[queued.Id] = GUIRuntime.Now + 30; }
                views.Error(message.ReadString());
                break;
        }
    }
    private void ClearMessages() { receipts.Clear(); messages.Clear(); messageOrder.Clear(); }
    private void TrimMessages() {
        while (messages.Count >= 2000 && messageOrder.TryDequeue(out string key, out _)) { messages.Remove(key); }
    }
    private bool CanView(PrivateMessage message) => MessageAccess.CanView(message, account, Allows(InteractionAction.ViewGMFeed));
    private void Shortcut(Keys key) { if (key == Keys.F4 && PlayView.Running) { ToggleLiveEditor(); GUIRuntime.Instance.Input.ConsumePointer(); } if (key == Keys.Escape) { editorDrag = editorMarquee = false; editorMoving = null; worldSelection = null; var cancel = worldSelectionCancelled; worldSelectionCancelled = null; cancel?.Invoke(); } if (key == Keys.F3 && PlayView.Running) { views.ToggleGMMessage(); GUIRuntime.Instance.Input.ConsumePointer(); } if (key == Keys.F2 && PlayView.Running) { views.ToggleInteraction(); GUIRuntime.Instance.Input.ConsumePointer(); } }
    internal void ToggleGMMessage() => views.ToggleGMMessage();
    internal void ToggleStudio() => views.ToggleStudio();
    internal void RefreshStudioKits() => views.RefreshStudioKits();
    internal void Act(ushort id, EntityOperation operation, string value = "", Vector2 point = default) {
        if (!Allows(InteractionAction.ManageEntities)) { return; }
        if (!Multiplayer) { EntityActions.Execute(id, operation, value, point); return; }
        Send(InteractionPacket.EntityAction, output => { output.WriteString(Guid.NewGuid().ToString("N")); output.WriteUInt16(id); output.WriteByte((byte)operation); output.WriteString(value); output.WriteSingle(point.X); output.WriteSingle(point.Y); });
    }
    internal void Direct(DirectorEvent value, bool preview = false) {
        if (!Allows(InteractionAction.DirectEvents)) { return; }
        DirectorEvent.Validate(value);
        if (!Multiplayer || preview) { if (!preview && value.Kind == DirectorKind.Stop && value.Target.Length == 0) { localScenes.Clear(); } playback.Start(value, !preview); if (!preview) { RequestEffects(); } return; }
        Send(InteractionPacket.Director, output => DirectorEvent.Write(output, value));
    }
    internal void DirectSequence(IReadOnlyList<DirectorEvent> events) {
        if (!Allows(InteractionAction.DirectEvents) || events.Count < 1 || events.Count > 16) { throw new ArgumentException("В последовательности должно быть от 1 до 16 событий"); }
        foreach (var value in events) { DirectorEvent.Validate(value); }
        if (!Multiplayer) { foreach (var value in events) { playback.Start(value); } return; }
        Send(InteractionPacket.DirectorBatch, output => { output.WriteByte((byte)events.Count); foreach (var value in events) { DirectorEvent.Write(output, value); } });
    }
    private Action<Vector2> worldSelection;
    private Action worldSelectionCancelled;
    internal void PickWorld(Action<Vector2> selected, Action cancelled = null) {
        worldSelectionCancelled?.Invoke(); worldSelectionCancelled = cancelled;
        worldSelection = selected ?? throw new ArgumentNullException(nameof(selected));
        GUIRuntime.Instance.Input.SetFocus(null);
        var hint = GUIRuntime.Instance.Windows.Notify("Выберите точку в мире", new Vector2(30, 30), 8);
        hint.InputTransparent = true;
        hint.Add(new Text("ЛКМ: выбрать · Escape: отменить"));
    }
    private readonly List<WorldQueryResult> picks = new();
    private Item lastLight;
    private int lastComponent = -1;
    private Vector2 lastLightPointer;
    private double lastLightClick;
    public double DoubleClickInterval { get; set; } = .3;
    private void WorldPressed(Vector2 pointer, bool primary, bool secondary) {
        if (RPWheel.RPWheelMod.IsWheelOpen || !PlayView.Running) { return; }
        Vector2 world = GameMain.GameScreen.Cam.ScreenToWorld(pointer);
        if (editorActive) { LiveWorldPressed(pointer, primary, secondary); GUIRuntime.Instance.Input.ConsumePointer(); return; }
        if (primary && worldSelection != null) {
            var selected = worldSelection; worldSelection = null; worldSelectionCancelled = null;
            GUIRuntime.Instance.Input.ConsumePointer();
            try { selected(world); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { views.Error(error.Message); }
            return;
        }
        if (secondary && Keyboard.GetState().IsKeyDown(Keys.LeftAlt)) { views.OpenHub(); GUIRuntime.Instance.Input.ConsumePointer(); return; }
        if (secondary && Allows(InteractionAction.ManageEntities)) { views.OpenWorldContext(world, pointer); GUIRuntime.Instance.Input.ConsumePointer(); return; }
        if (!primary) { return; }
        bool alt = Keyboard.GetState().IsKeyDown(Keys.LeftAlt) || Keyboard.GetState().IsKeyDown(Keys.RightAlt);
        if (!alt) { WorldQueryService.Instance.Query(world, 50 / Math.Max(.1f, GameMain.GameScreen.Cam.Zoom), true, "", picks);
        for (int i = 0; i < picks.Count && Allows(InteractionAction.ViewCharacter); i++) {
            if (picks[i].Entity is not Character character || character.InvisibleTimer > 0 || character == Character.Controlled) { continue; }
            views.OpenCharacter(character, pointer);
            GUIRuntime.Instance.Input.ConsumePointer();
            return;
        }
        }
        if (!Allows(InteractionAction.FlickerLight) || Character.Controlled is { IsDead: false } && !alt) { return; }
        WorldQueryService.Instance.Query(world, 80 / Math.Max(.1f, GameMain.GameScreen.Cam.Zoom), false, "", picks);
        Item selectedItem = null;
        int selectedComponent = -1;
        float closest = 32 * 32 / Math.Max(.01f, GameMain.GameScreen.Cam.Zoom * GameMain.GameScreen.Cam.Zoom);
        for (int i = 0; i < picks.Count; i++) {
            var item = (Item)picks[i].Entity;
            for (int c = 0; c < item.Components.Count && c <= byte.MaxValue; c++) {
                if (item.Components[c] is not LightComponent { Light: { } light }) { continue; }
                float distance = Vector2.DistanceSquared(world, light.WorldPosition);
                if (distance > closest) { continue; }
                closest = distance; selectedItem = item; selectedComponent = c;
            }
        }
        if (selectedItem == null) { return; }
        double now = GUIRuntime.Now;
        if (alt || lastLight == selectedItem && lastComponent == selectedComponent && now - lastLightClick <= Math.Clamp(DoubleClickInterval, .1, .8) && Vector2.DistanceSquared(pointer, lastLightPointer) <= 36) { Flicker(selectedItem, (byte)selectedComponent); lastLight = null; }
        else { lastLight = selectedItem; lastComponent = selectedComponent; lastLightClick = now; lastLightPointer = pointer; }
        GUIRuntime.Instance.Input.ConsumePointer();
    }
}
