using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;
using PrimaryPackage.Interactions;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Server side of the live submarine editor: the single authority over editor operations.
/// Every client (not only editors) gets a snapshot on round join and then the ordered operation stream,
/// so a player who connects hours into a round receives the current world instead of the history.
/// </summary>
public sealed partial class SubEditorServer : IModule, IRoundEnded, IEventUpdate, IEventClientDisconnected, IEventRoundEnded
{
    private enum SyncPhase : byte { Streaming, AwaitingApplication, Live }

    private sealed class Session
    {
        public Client Client;
        public byte Actor;
        /// <summary>The client's number for this join attempt; chunks carry it and acknowledgements must echo it.</summary>
        public uint Join;
        public ulong Token;
        public long LastCursor;
        public SyncPhase Phase;
        public bool Live => Phase == SyncPhase.Live;
        public ulong SnapshotRevision, AppliedRevision;
        public ushort SnapshotNativeId;
        public SnapshotCursor Cursor;
        public ChunkWindow Window;
        public readonly Dictionary<int, byte[]> Chunks = new();
        public int Produced;
        public bool Finished;
        public bool Failed;
        public int RequestsThisSecond;
        public long LastSecondMs;
        public readonly RequestJournal Requests = new();
    }

    private readonly Dictionary<byte, Session> sessions = new();
    private Authority authority;
    private bool[] createdByEditor;
    private ulong autosavedRev;
    private long lastAutosave;
    private IDisposable receiver;
    private static SubEditorServer instance;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long NowMs => Clock.ElapsedMilliseconds;
    private static readonly INet.NetChannel Channel = INet.Net.Channel(SubEditorProtocol.Channel);
    private readonly record struct RoutedPacket(ulong Token, Action<IWriteMessage> WritePayload) : INet.INetPacket
    {
        public int SizeHintBytes => 0;
        public void Write(IWriteMessage output) { SubEditorProtocol.WriteServerHeader(output, Token); WritePayload(output); }
    }
    private static ushort NativeEventId => GameMain.Server?.EntityEventManager?.ID ?? 0;

    private static void RetainBaseNativeEvents(List<NetEntityEvent> events)
    {
        int write = 0;
        for (int i = 0; i < events.Count; i++)
        {
            NetEntityEvent ev = events[i];
            if (ev?.Entity is not MapEntity entity || entity.ID < SubRegistry.EditorIdLimit)
            {
                events[write++] = ev;
            }
        }
        if (write < events.Count) { events.RemoveRange(write, events.Count - write); }
    }

    internal static void FilterNativeEvents(Client client, List<NetEntityEvent> events)
    {
        SubEditorServer server = instance;
        if (server?.authority == null || server.authority.Rev == 0 || events == null || events.Count == 0 || client == null) { return; }

        if (!server.sessions.TryGetValue(client.SessionId, out Session session) || !session.Live)
        {
            RetainBaseNativeEvents(events);
            return;
        }

        if (client.NeedsMidRoundSync) { return; }
        ushort cut;
        if (session.AppliedRevision >= server.authority.Rev) { return; }
        else if (server.authority.TryGetFrame(session.AppliedRevision + 1, out ReadOnlySpan<byte> bytes) && OpFrame.TryRead(bytes, out OpFrame.View frame))
        { cut = frame.NativeEventId; }
        else
        {
            server.Send(session, w => w.WriteByte((byte)ServerPacket.Restart));
            server.sessions.Remove(client.SessionId);
            RetainBaseNativeEvents(events);
            return;
        }
        int write = 0;
        for (int i = 0; i < events.Count; i++)
        {
            NetEntityEvent ev = events[i];
            if (ev?.Entity is not MapEntity entity || entity.ID < SubRegistry.EditorIdLimit || !NetIdUtils.IdMoreRecent(ev.ID, cut))
            {
                events[write++] = ev;
            }
        }
        if (write < events.Count) { events.RemoveRange(write, events.Count - write); }
    }

    public void Attach(Harmony harmony)
    {
        instance = this;
        ExactIds.Attach(harmony);
        SubIds.Attach(harmony);
        SubRegistry.Attach(harmony);
        CircuitOps.Attach(harmony);
        NativeEventOrder.Attach(harmony);
        InventoryOps.Attach(harmony);
        harmony.Patch(
            AccessTools.Method(typeof(Character), nameof(Character.CanAccessInventory),
                new[] { typeof(Inventory), typeof(CharacterInventory.AccessLevel) }),
            prefix: new HarmonyMethod(typeof(SubEditorServer), nameof(CanAccessInventoryPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(Character), nameof(Character.CanInteractWith),
                new[] { typeof(Item), typeof(float).MakeByRefType(), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(SubEditorServer), nameof(CanInteractWithItemPrefix)));
        SubCommands.Register();
        receiver = INet.Net.Listen(SubEditorProtocol.Channel, Receive);
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventRoundEnded>(this);
    }

    public void RoundStart() => Reset();

    public void RoundEnd() => OnRoundEnd();

    public void OnRoundEnd()
    {
        Autosave(NowMs);
        Reset();
    }

    public void Release()
    {
        receiver?.Dispose(); receiver = null;
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventRoundEnded>(this);
        sessions.Clear(); authority = null; createdByEditor = null; instance = null;
    }

    public void OnClientDisconnected(Client client) { sessions.Remove(client.SessionId); }

    public static bool IsEditorCharacter(Character character)
    {
        if (character == null || instance == null) { return false; }
        foreach (Session s in instance.sessions.Values)
        {
            if (s.Client?.Character == character) { return true; }
        }
        return false;
    }

    public static bool CanAccessInventoryPrefix(Character __instance, Inventory inventory, ref bool __result)
    {
        if (IsEditorCharacter(__instance))
        {
            __result = true;
            return false;
        }
        return true;
    }

    public static bool CanInteractWithItemPrefix(Character __instance, Item item, ref float distanceToItem, ref bool __result)
    {
        if (IsEditorCharacter(__instance))
        {
            distanceToItem = 0.0f;
            __result = true;
            return false;
        }
        return true;
    }

    private void Reset()
    {
        sessions.Clear();
        authority = new Authority(new OpRing(4 << 20, 16384), new EntityVersions());
        createdByEditor = new bool[EntityVersions.Capacity];
        SubRegistry.Reset();
        autosavedRev = 0; lastAutosave = NowMs;
    }

    /// <summary>
    /// The same rule the client applies before opening the editor (F4): the server owner, or anyone with the console permission.
    /// (There is no console command named "subeditor" to grant, so a per-command check here refused every edit from a non-owner.)
    /// </summary>
    private static bool Allowed(Client c) => PermissionService.Owner(c) || c.HasPermission(ClientPermissions.ConsoleCommands);

    private static bool RoundActive => GameMain.GameSession != null && Submarine.MainSub != null;

    // ---------------------------------------------------------------------------------------------- receive

    private void Receive(IReadMessage m, Client sender)
    {
        if (authority == null || sender == null || !RoundActive) { return; }
        var packet = (ClientPacket)m.ReadByte();
        if (packet != ClientPacket.Hello)
        {
            if (m.LengthBits - m.BitPosition < 64 || !sessions.TryGetValue(sender.SessionId, out Session session) || m.ReadUInt64() != session.Token) { return; }
        }
        switch (packet)
        {
            case ClientPacket.Hello:
            {
                uint join = m.ReadUInt32();
                ushort version = m.LengthBits - m.BitPosition >= 16 ? m.ReadUInt16() : (ushort)0;
                if (version != SubEditorProtocol.Version)
                {
                    sessions.Remove(sender.SessionId);
                    INet.Net.Send(SubEditorProtocol.Channel, sender.Connection, w => { w.WriteByte((byte)ServerPacket.Notice); w.WriteString("Несовместимая версия сетевого редактора. Обновите Primary package на сервере и всех клиентах."); });
                    INet.Net.Send(Channel, sender.Connection, new RoutedPacket(0, w => { w.WriteByte((byte)ServerPacket.Incompatible); w.WriteUInt16(SubEditorProtocol.Version); w.WriteUInt32(join); }));
                    break;
                }
                StartJoin(sender, join);
                break;
            }
            case ClientPacket.Bye: sessions.Remove(sender.SessionId); break;
            case ClientPacket.SnapAck:
                {
                    uint join = m.ReadUInt32();
                    if (sessions.TryGetValue(sender.SessionId, out Session a) && a.Join == join) { OnAck(a, (int)m.ReadVariableUInt32()); }
                    break;
                }
            case ClientPacket.Missing: OnMissing(sender, m.ReadString()); break;
            case ClientPacket.Cursor:
                {
                    float x = m.ReadSingle(), y = m.ReadSingle(); byte flags = m.ReadByte();
                    ushort sel = m.BitPosition < m.LengthBits ? m.ReadUInt16() : (ushort)0;
                    RelayCursor(sender, x, y, flags, sel);
                    break;
                }
            case ClientPacket.Resend: Resend(sender, m.ReadUInt64(), m.ReadUInt64()); break;
            case ClientPacket.Applied:
                if (m.LengthBits - m.BitPosition >= 64 && sessions.TryGetValue(sender.SessionId, out Session applied))
                {
                    ulong revision = m.ReadUInt64();
                    if (applied.Finished && revision >= applied.SnapshotRevision && revision <= authority.Rev)
                    {
                        applied.Phase = SyncPhase.Live;
                        applied.AppliedRevision = Math.Max(applied.AppliedRevision, revision);
                        OnAck(applied, applied.Produced);
                    }
                }
                break;
            case ClientPacket.Request: HandleRequest(sender, m); break;
            case ClientPacket.Undo: HandleHistory(sender, m.ReadUInt32(), m.ReadUInt32(), undo: true); break;
            case ClientPacket.Redo: HandleHistory(sender, m.ReadUInt32(), m.ReadUInt32(), undo: false); break;
        }
    }

    private void Send(Session s, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
        INet.Net.Send(Channel, s.Client.Connection, new RoutedPacket(s.Token, write), method);

    private void Reply(Client c, ServerPacket p, uint requestId, SubmitStatus status, int conflict, ulong rev)
    {
        INet.Net.Send(Channel, c.Connection, new RoutedPacket(sessions.TryGetValue(c.SessionId, out Session session) ? session.Token : 0, w =>
        {
            w.WriteByte((byte)p); w.WriteUInt32(requestId); w.WriteByte((byte)status); w.WriteUInt64(rev); w.WriteInt32(conflict);
        }));
    }

    // ---------------------------------------------------------------------------------------------- join

    /// <summary>Where an editor's cursor is, passed on to every other joined client so that editors see each other work. Unreliable: only the latest position matters.</summary>
    private void RelayCursor(Client sender, float x, float y, byte flags, ushort sel = 0)
    {
        if (!sessions.TryGetValue(sender.SessionId, out Session from) || !from.Live || !Allowed(sender) || !float.IsFinite(x) || !float.IsFinite(y)) { return; }
        long now = NowMs;
        if ((flags & PresenceFlags.Closed) == 0 && now - from.LastCursor < PresenceFlags.SendIntervalMs / 2) { return; }
        from.LastCursor = now;
        byte actor = from.Actor;
        foreach (Session to in sessions.Values)
        {
            if (!to.Live || to == from || to.Client?.Connection == null) { continue; }
            Send(to, w =>
            {
                w.WriteByte((byte)ServerPacket.Presence); w.WriteByte(actor); w.WriteByte(flags);
                w.WriteSingle(x); w.WriteSingle(y); w.WriteUInt16(sel);
            }, (flags & PresenceFlags.Closed) != 0 ? DeliveryMethod.Reliable : DeliveryMethod.Unreliable);
        }
    }

    /// <summary>A peer cannot build a submarine the server spawned: the world differs there, so say so where the server's operators look.</summary>
    private static void OnMissing(Client client, string description)
    {
        string text = $"{NetworkMember.ClientLogName(client)} cannot build an editor-spawned submarine: {(description.Length > 200 ? description[..200] : description)}";
        GameServer.Log(text, ServerLog.MessageType.Error);
        ModLog.Once("subeditor: " + text);
    }

    private void StartJoin(Client client, uint join)
    {
        if (sessions.TryGetValue(client.SessionId, out Session previous) && previous.Join == join && ReferenceEquals(previous.Client, client))
        { SendWelcome(previous); return; }
        var ids = new SortedSet<ushort>();
        EntityVersions v = authority.Versions;
        for (int id = 1; id < EntityVersions.Capacity; id++)
        {
            if (v.Latest((ushort)id) == 0) { continue; }
            ids.Add((ushort)id);
            // A touched container drags its contents along, they must exist on the joiner as well.
            if (EntityOps.Find((ushort)id) is Item item)
            {
                void AddNested(Item it)
                {
                    foreach (Item inner in it.ContainedItems) { if (ids.Add(inner.ID)) { AddNested(inner); } }
                }
                AddNested(item);
            }
        }
        long now = NowMs;
        var s = new Session
        {
            Client = client,
            Actor = client.SessionId,
            Join = join,
            Token = NewSessionToken(),
            Cursor = new SnapshotCursor(v, ids.ToArray(), tailLimit: SubEditorProtocol.BatchEntities),
            Window = new ChunkWindow(SubEditorProtocol.WindowChunks, SubEditorProtocol.BaseRtoMs, SubEditorProtocol.MaxRtoMs, SubEditorProtocol.StallMs, now),
        };
        sessions[client.SessionId] = s;
        SendWelcome(s);
    }

    private static ulong NewSessionToken()
    {
        Span<byte> token = stackalloc byte[16]; Guid.NewGuid().TryWriteBytes(token);
        ulong result = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(token);
        return result == 0 ? 1UL : result;
    }

    private void SendWelcome(Session session) => Send(session, w =>
    { w.WriteByte((byte)ServerPacket.Welcome); w.WriteUInt16(SubEditorProtocol.Version); w.WriteUInt32(session.Join); });

    /// <summary>
    /// The spawned submarines, taken in the same tick as the cut revision: a submarine added or removed while a slow join was streaming
    /// is then neither missing from the snapshot nor repeated by the frames after it. The client builds them before any entity (see ApplySnapshot).
    /// </summary>
    private static byte[] SubsChunk()
    {
        var w = new ByteWriter(256);
        try
        {
            SubSpawn[] subs = SubRegistry.Spawned.ToArray();
            SubListPayload.Write(ref w, subs);
            byte[] chunk = new byte[w.Length + 1];
            chunk[0] = (byte)ChunkKind.Subs; w.Written.CopyTo(chunk.AsSpan(1));
            return chunk;
        }
        finally { w.Dispose(); }
    }

    private void OnAck(Session s, int cumulative)
    {
        s.Window.OnAck(cumulative, NowMs);
        foreach (int done in s.Chunks.Keys.Where(k => k < s.Window.Acked).ToList()) { s.Chunks.Remove(done); }
        if (s.Phase == SyncPhase.Streaming && s.Finished && s.Window.Acked >= s.Produced)
        { s.Phase = SyncPhase.AwaitingApplication; }
    }

    private void Resend(Client c, ulong from, ulong to)
    {
        if (!sessions.TryGetValue(c.SessionId, out Session s) || !s.Live) { return; }
        if (to - from > 4096 || !authority.CanResend(from, to))
        {
            Send(s, w => w.WriteByte((byte)ServerPacket.Restart));
            sessions.Remove(c.SessionId);
            return;
        }
        for (ulong rev = from; rev <= to; rev++) { SendFrame(s, rev); }
    }

    public void OnUpdate(double fixedDeltaTime)
    {
        long now = NowMs;
        Autosave(now);
        if (sessions.Count == 0) { return; }
        List<byte> dead = null;
        foreach (Session s in sessions.Values)
        {
            if (s.Client?.Connection == null) { (dead ??= new List<byte>()).Add(s.Client?.SessionId ?? 0); continue; }
            if (!s.Live)
            {
                Produce(s);
                if (s.Failed)
                {
                    Send(s, w => { w.WriteByte((byte)ServerPacket.Notice); w.WriteString("SubEditor snapshot exceeds its limits or could not settle; reconnecting."); });
                    Send(s, w => w.WriteByte((byte)ServerPacket.Restart));
                    (dead ??= new List<byte>()).Add(s.Client.SessionId);
                    continue;
                }
                s.Window.Poll(now);
                if (s.Window.Stalled)
                {
                    // Tell the client to start over rather than leave it waiting for chunks that will never come.
                    Send(s, w => w.WriteByte((byte)ServerPacket.Restart));
                    (dead ??= new List<byte>()).Add(s.Client.SessionId);
                    continue;
                }
                while (s.Window.CanSend)
                {
                    int index = s.Window.Take(now);
                    if (!s.Chunks.TryGetValue(index, out byte[] bytes)) { continue; }
                    uint idx = (uint)index, join = s.Join;
                    Send(s, w => { w.WriteByte((byte)ServerPacket.SnapChunk); w.WriteUInt32(join); w.WriteVariableUInt32(idx); SubEditorProtocol.WriteBlob(w, bytes); });
                }
            }
        }
        if (dead != null) { foreach (byte d in dead) { sessions.Remove(d); } }
    }

    /// <summary>Edits live only in the round; a periodic copy on disk keeps them if the server stops. Written only when something changed.</summary>
    private void Autosave(long now)
    {
        if (authority == null || authority.Rev == autosavedRev || now - lastAutosave < SubEditorProtocol.AutosaveMs || !RoundActive) { return; }
        lastAutosave = now;
        autosavedRev = authority.Rev;
        string path = System.IO.Path.Combine(LiveSave.ServerFolder, "_autosave.sub");
        if (!LiveSave.Write(Submarine.MainSub, path)) { ModLog.Once($"subeditor: autosave to {path} failed"); }
    }

    // ------------------------------------------------------------------------------------- snapshot production

    private void Produce(Session s)
    {
        if (s.Finished || s.Cursor.Phase == CursorPhase.Failed || s.Produced - s.Window.Acked >= 4 * SubEditorProtocol.WindowChunks)
        {
            if (s.Cursor.Phase == CursorPhase.Failed) { s.Failed = true; }
            return;
        }
        var batch = new ushort[SubEditorProtocol.BatchEntities * 2];
        long start = Stopwatch.GetTimestamp();
        for (int round = 0; round < 8; round++)
        {
            int n = s.Cursor.Next(batch);
            if (n > 0 && !Emit(s, batch.AsSpan(0, n))) { s.Failed = true; return; }
            if (s.Cursor.IsFinalBatch)
            {
                ulong cut = s.Cursor.Complete(authority.Rev);
                s.SnapshotRevision = cut; s.SnapshotNativeId = NativeEventId;
                AddCircuitChunks(s, cut);
                if (s.Failed) { return; }
                AddChunk(s, SubsChunk());
                byte[] done = new byte[11];
                int pos = 0; done[pos++] = (byte)ChunkKind.Done; VarInt.Write(done, ref pos, cut); VarInt.Write(done, ref pos, s.SnapshotNativeId);
                AddChunk(s, done.AsSpan(0, pos).ToArray());
                s.Finished = true;
                return;
            }
            if (n == 0 || (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency > 0.5) { return; }
        }
    }

    private void AddChunk(Session s, byte[] bytes)
    {
        s.Chunks[s.Produced++] = bytes;
        s.Window.OnProduced(s.Produced);
    }

    private bool Emit(Session s, ReadOnlySpan<ushort> ids)
    {
        byte[] chunk = Serialize(ids);
        if (chunk.Length > SubEditorProtocol.MaxChunkBytes && ids.Length > 1)
        {
            int half = ids.Length / 2;
            return Emit(s, ids.Slice(0, half)) && Emit(s, ids.Slice(half));
        }
        if (chunk.Length > SubEditorProtocol.MaxChunkBytes) { ModLog.Once($"subeditor: entity {ids[0]} is too large to snapshot"); return false; }
        AddChunk(s, chunk);
        return true;
    }

    private byte[] Serialize(ReadOnlySpan<ushort> ids)
    {
        var w = new ByteWriter(4096);
        try
        {
            w.V((ulong)ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                ushort id = ids[i];
                MapEntity e = EntityOps.Find(id);
                w.V(id);
                if (e == null)
                {
                    // Submarine metadata is versioned under the submarine's own id.
                    if (Submarine.Loaded.FirstOrDefault(x => x.ID == id) is Submarine owner)
                    {
                        var meta = new ByteWriter(256);
                        MetaPayload.Write(ref meta, SubMeta.Capture(owner.Info));
                        w.U8((byte)EntityState.Meta); w.Bytes(meta.Written);
                        meta.Dispose();
                    }
                    else { w.U8((byte)EntityState.Gone); }
                    continue;
                }
                bool full = createdByEditor[id];
                w.U8((byte)(full ? EntityState.Full : EntityState.InPlace));
                w.V(SubRegistry.WireId(e.Submarine));
                if (full)
                {
                    var root = new XElement(EntityOps.SpawnRoot);
                    EntityOps.SaveInto(root, e);
                    w.Bytes(System.Text.Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)));
                }
                else { w.Bytes(EntityOps.CaptureStateBytes(e)); }
            }
            ReadOnlySpan<byte> raw = w.Written;
            if (raw.Length > 512)
            {
                using var ms = new MemoryStream();
                ms.WriteByte((byte)ChunkKind.DataBrotli);
                using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) { br.Write(raw); }
                return ms.ToArray();
            }
            byte[] plain = new byte[raw.Length + 1];
            plain[0] = (byte)ChunkKind.Data; raw.CopyTo(plain.AsSpan(1));
            return plain;
        }
        finally { w.Dispose(); }
    }

    // ---------------------------------------------------------------------------------------------- operations

    private void HandleRequest(Client sender, IReadMessage m)
    {
        uint requestId = m.ReadUInt32();
        uint join = m.ReadUInt32();
        if (!sessions.TryGetValue(sender.SessionId, out Session s) || s.Join != join || !s.Live || !Allowed(sender))
        {
            ModLog.Once($"subeditor: request from {sender.Name} refused ({(s == null ? "no join" : !s.Live ? "snapshot not finished" : "no permission")})");
            Reply(sender, ServerPacket.Denied, requestId, SubmitStatus.Malformed, -1, authority.Rev);
            return;
        }
        if (ReplayRequest(sender, s, requestId)) { return; }
        long now = NowMs;
        if (now - s.LastSecondMs >= 1000) { s.RequestsThisSecond = 0; s.LastSecondMs = now; }
        if (++s.RequestsThisSecond > 120)
        {
            ModLog.Once($"subeditor: request flood from {sender.Name} throttled");
            Reply(sender, ServerPacket.Reject, requestId, SubmitStatus.Malformed, -1, authority.Rev);
            return;
        }
        var kind = (OpKind)m.ReadByte();
        ulong baseRev = m.ReadUInt64();
        uint count = m.ReadVariableUInt32();
        if (count > FrameApplier.MaxTouchedForApply || (m.LengthBits - m.BitPosition) < (long)count * 16) { Reply(sender, ServerPacket.Reject, requestId, SubmitStatus.Malformed, -1, authority.Rev); return; }
        var ids = new ushort[count];
        for (int i = 0; i < ids.Length; i++) { ids[i] = m.ReadUInt16(); }
        byte[] payload = SubEditorProtocol.ReadBlob(m, SubEditorProtocol.MaxFrameBytes);
        if (payload == null) { Reply(sender, ServerPacket.Reject, requestId, SubmitStatus.Malformed, -1, authority.Rev); return; }

        SubmitResult result;
        try { result = Execute(s.Actor, kind, baseRev, ids, payload); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException)
        {
            ModLog.Error($"subeditor: {kind} failed: {e}");
            result = new SubmitResult(SubmitStatus.Malformed, authority.Rev, -1);
        }
        if (result.Status != SubmitStatus.Accepted) { ModLog.Error($"subeditor: {kind} from {sender.Name} refused: {result.Status}{(result.ConflictId >= 0 ? $" (entity {result.ConflictId})" : "")}"); }
        ModLog.Message($"subeditor: {kind} from {sender.Name} (request {requestId}, {ids.Length} ids, {payload.Length} bytes) -> {result.Status}, revision {authority.Rev}");
        FinishOp(sender, requestId, result);
    }

    private bool ReplayRequest(Client sender, Session session, uint id)
    {
        if (session.Requests.TryGet(id, out SubmitResult previous))
        {
            Reply(sender, previous.Status == SubmitStatus.Accepted ? ServerPacket.Ack : ServerPacket.Reject, id, previous.Status, previous.ConflictId, previous.Rev);
            if (previous.Status == SubmitStatus.Accepted) { SendFrame(session, previous.Rev); }
            return true;
        }
        if (session.Requests.Begin(id)) { return false; }
        Reply(sender, ServerPacket.Reject, id, SubmitStatus.Malformed, -1, authority.Rev);
        return true;
    }

    private void HandleHistory(Client sender, uint requestId, uint join, bool undo)
    {
        if (!sessions.TryGetValue(sender.SessionId, out Session s) || s.Join != join || !s.Live || !Allowed(sender)) { Reply(sender, ServerPacket.Denied, requestId, SubmitStatus.Malformed, -1, authority.Rev); return; }
        if (ReplayRequest(sender, s, requestId)) { return; }
        // Bringing a submarine back needs its ids again; if another one has taken them since, say so and keep the step in the history.
        if (authority.TryPeek(s.Actor, undo, out OpKind next, out byte[] nextPayload) && next == OpKind.SpawnSub
            && SubSpawnPayload.TryRead(nextPayload, out SubSpawn wanted) && !SubRegistry.CanLoad(wanted))
        {
            Reply(sender, ServerPacket.Reject, requestId, SubmitStatus.Conflict, wanted.SubmarineId, authority.Rev);
            return;
        }
        authority.NativeEventId = NativeEventId;
        SubmitResult r = undo ? authority.Undo(s.Actor, FrameApplier.ApplyTransaction, applyIsAtomic: true) : authority.Redo(s.Actor, FrameApplier.ApplyTransaction, applyIsAtomic: true);
        if (r.Status == SubmitStatus.Accepted && authority.TryGetFrame(r.Rev, out ReadOnlySpan<byte> frame) && OpFrame.TryRead(frame, out OpFrame.View v))
        {
            // Undo of a spawn deletes, undo of a delete re-creates from XML: the editor-created flag follows the frame kind.
            if (v.Kind == OpKind.Spawn || v.Kind == OpKind.Delete)
            {
                Span<ushort> touched = stackalloc ushort[v.Count]; v.ReadIds(touched);
                foreach (ushort id in touched) { createdByEditor[id] = v.Kind == OpKind.Spawn; }
            }
            else if (v.Kind == OpKind.Batch) { TrackCreated(v.Payload.ToArray()); }
        }
        FinishOp(sender, requestId, r);
    }

    private void FinishOp(Client sender, uint requestId, SubmitResult r)
    {
        if (sessions.TryGetValue(sender.SessionId, out Session session)) { session.Requests.Complete(requestId, r); }
        if (r.Status != SubmitStatus.Accepted) { Reply(sender, ServerPacket.Reject, requestId, r.Status, r.ConflictId, authority.Rev); return; }
        Reply(sender, ServerPacket.Ack, requestId, r.Status, -1, r.Rev);
        Broadcast(r.Rev);
    }

    private void Broadcast(ulong rev)
    {
        foreach (Session t in sessions.Values) { if (t.Client?.Connection != null) { SendFrame(t, rev); } }
    }

    private void SendFrame(Session s, ulong rev)
    {
        if (!authority.TryGetFrame(rev, out ReadOnlySpan<byte> frame)) { return; }
        byte[] copy = frame.ToArray();
        Send(s, w => { w.WriteByte((byte)ServerPacket.Op); SubEditorProtocol.WriteBlob(w, copy); });
    }

    /// <summary>An operation whose effect on the world has been performed, with everything the authority needs to record it.</summary>
    private sealed class Prepared
    {
        public OpKind Kind, UndoKind;
        public ushort[] Ids;
        public byte[] Redo, Undo;
        public SubmitResult Failure;
        public bool Record = true;
        public Action Rollback;
        public bool Ok => Failure.Status == SubmitStatus.Accepted;
    }

    private Prepared Fail(SubmitStatus status, int conflict = -1) =>
        new Prepared { Failure = new SubmitResult(status, authority.Rev, conflict) };

    private SubmitResult Execute(byte actor, OpKind kind, ulong baseRev, ushort[] ids, byte[] payload)
    {
        authority.NativeEventId = NativeEventId;
        Prepared p;
        try { p = kind == OpKind.Batch ? PerformBatch(actor, baseRev, payload) : Perform(actor, kind, baseRev, ids, payload); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException)
        {
            ModLog.Error($"subeditor: {kind} failed: {e}");
            return new SubmitResult(SubmitStatus.Malformed, authority.Rev, -1);
        }
        if (!p.Ok) { return p.Failure; }
        if (p.Redo.Length > SubEditorProtocol.MaxFrameBytes || p.Record && p.Undo.Length > SubEditorProtocol.MaxFrameBytes || p.Ids.Length > OpFrame.MaxTouched)
        {
            Revert(p);
            return new SubmitResult(SubmitStatus.Malformed, authority.Rev, -1);
        }
        SubmitResult r = authority.Submit(actor, p.Kind, baseRev, p.Ids, p.Redo, p.Undo, p.Record, p.UndoKind);
        if (r.Status != SubmitStatus.Accepted)
        {
            // The effect is already in the world: take it back so the server never holds a change that no client was told about.
            // (A removed submarine comes back from the same record that undo uses.)
            Revert(p);
            return r;
        }
        if (p.Kind == OpKind.Spawn) { foreach (ushort id in p.Ids) { createdByEditor[id] = true; } }
        if (p.Kind == OpKind.Delete) { foreach (ushort id in p.Ids) { createdByEditor[id] = false; } }
        if (p.Kind == OpKind.Batch) { TrackCreated(p.Redo); }
        return r;
    }

    /// <summary>Marks entities created or removed by the sub-operations of a batch.</summary>
    private void TrackCreated(byte[] batch)
    {
        if (!BatchPayload.TryRead(batch, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] subs)) { return; }
        foreach (BatchPayload.Sub s in subs)
        {
            if (s.Kind == OpKind.Spawn) { foreach (ushort id in s.Ids) { createdByEditor[id] = true; } }
            else if (s.Kind == OpKind.Delete) { foreach (ushort id in s.Ids) { createdByEditor[id] = false; } }
        }
    }

    /// <summary>Applies already validated bytes through the same path clients use, without re-encoding to an OpFrame.</summary>
    private static bool ApplyDirect(OpKind kind, ushort[] ids, byte[] payload) =>
        FrameApplier.Apply(kind, ids, payload);

    private static void Revert(Prepared operation)
    {
        if (operation.Rollback != null) { operation.Rollback(); return; }
        FrameApplier.Restore(new BatchPayload.Sub(operation.UndoKind, operation.Ids, operation.Undo));
    }

    private Prepared ApplyPrepared(Prepared operation)
    {
        if (WorldMutationCoordinator.ApplyPrepared(operation.Kind, operation.Ids, operation.Redo, () => Revert(operation)))
        { return operation; }
        return Fail(SubmitStatus.Malformed);
    }

    private Prepared CaptureAndApply(OpKind kind, ushort[] ids, byte[] payload, ushort[] dependencies = null)
    {
        if (!FrameApplier.CaptureInverse(kind, ids, payload, out BatchPayload.Sub inverse)) { return Fail(SubmitStatus.Malformed); }
        var operation = new Prepared { Kind = kind, UndoKind = inverse.Kind, Ids = inverse.Ids, Redo = payload, Undo = inverse.Payload };
        Prepared result = ApplyPrepared(operation);
        return result.Ok && dependencies != null ? WithDependencies(result, dependencies) : result;
    }

    private Prepared Perform(byte actor, OpKind kind, ulong baseRev, ushort[] ids, byte[] payload)
    {
        if (kind == OpKind.Spawn) { return PerformSpawn(actor, baseRev, payload); }
        if (kind == OpKind.Wire) { return PerformWire(actor, baseRev, payload); }
        if (kind == OpKind.WireNew) { return PerformWireNew(actor, baseRev, payload); }
        if (kind == OpKind.SubMeta) { return PerformMeta(actor, baseRev, ids, payload); }
        if (kind == OpKind.SpawnSub) { return PerformSpawnSub(payload); }
        if (kind == OpKind.DeleteSub) { return PerformDeleteSub(actor, baseRev, ids, payload); }
        if (kind == OpKind.Waypoints) { return PerformWaypoints(actor, baseRev); }
        if (kind == OpKind.Circuit) { return PerformCircuit(actor, baseRev, ids, payload); }
        if (ids.Length == 0 || !OpFrame.AreStrictlyAscending(ids) || !EntityOps.AllExist(ids)) { return Fail(SubmitStatus.Malformed); }
        int conflict = authority.Versions.FirstConflict(ids, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }

        switch (kind)
        {
            case OpKind.Transform:
                {
                    var records = new TransformRecord[ids.Length];
                    if (!TransformPayload.TryRead(payload, ids.Length, records)) { return Fail(SubmitStatus.Malformed); }
                    return CaptureAndApply(kind, ids, payload);
                }
            case OpKind.Property:
                {
                    return CaptureAndApply(kind, ids, payload);
                }
            case OpKind.Flip:
                {
                    var records = new FlipRecord[ids.Length];
                    if (!FlipPayload.TryRead(payload, ids.Length, records)) { return Fail(SubmitStatus.Malformed); }
                    return CaptureAndApply(kind, ids, payload);
                }
            case OpKind.State:
                {
                    if (!StatePayload.TryRead(payload, ids.Length, out _)) { return Fail(SubmitStatus.Malformed); }
                    return CaptureAndApply(kind, ids, payload);
                }
            case OpKind.Inventory:
                {
                    if (!InventoryPayload.TryRead(payload, ids.Length, out InventoryPlacement[] targets)) { return Fail(SubmitStatus.Malformed); }
                    var old = new InventoryPlacement[ids.Length];
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (EntityOps.Find(ids[i]) is not Item item || !EntityOps.CapturePlacement(item, out old[i])) { return Fail(SubmitStatus.Malformed); }
                    }
                    ushort[] touched = InventoryOps.Touched(ids, old, targets);
                    int inventoryConflict = authority.Versions.FirstConflict(touched, actor, baseRev);
                    if (inventoryConflict >= 0) { return Fail(SubmitStatus.Conflict, inventoryConflict); }
                    return CaptureAndApply(kind, ids, payload, touched);
                }
            case OpKind.Delete:
                {
                    if (payload.Length != 0) { return Fail(SubmitStatus.Malformed); }
                    List<MapEntity> all = EntityOps.WithContents(ids);
                    ushort[] full = all.Select(e => e.ID).ToArray();
                    int c2 = authority.Versions.FirstConflict(full, actor, baseRev);
                    if (c2 >= 0) { return Fail(SubmitStatus.Conflict, c2); }
                    var places = new InventoryPlacement[full.Length];
                    for (int i = 0; i < full.Length; i++) { if (all[i] is Item item) { InventoryOps.Capture(item, out places[i]); } }
                    ushort[] touched = full.Concat(InventoryOps.Touched(full, places, ReadOnlySpan<InventoryPlacement>.Empty)).Distinct().OrderBy(id => id).ToArray();
                    int inventoryConflict = authority.Versions.FirstConflict(touched, actor, baseRev);
                    if (inventoryConflict >= 0) { return Fail(SubmitStatus.Conflict, inventoryConflict); }
                    return CaptureAndApply(kind, full, payload, touched);
                }
            case OpKind.Link:
                {
                    int maxPairs = Math.Max(ids.Length * (ids.Length - 1) / 2, ids.Length * 4);
                    if (!LinkPayload.TryRead(payload, maxPairs, out var pairs)) { return Fail(SubmitStatus.Malformed); }
                    foreach (var pair in pairs) { if (Array.BinarySearch(ids, pair.A) < 0 || Array.BinarySearch(ids, pair.B) < 0) { return Fail(SubmitStatus.Malformed); } }
                    return CaptureAndApply(kind, ids, payload);
                }
            default:
                return Fail(SubmitStatus.Malformed);
        }
    }

    private static Prepared WithDependencies(Prepared operation, ushort[] touched)
    {
        if (operation.Ids.AsSpan().SequenceEqual(touched)) { return operation; }
        var redo = new ByteWriter(operation.Redo.Length + 32);
        var undo = new ByteWriter(operation.Undo.Length + 32);
        try
        {
            BatchPayload.Write(ref redo, new[] { new BatchPayload.Sub(operation.Kind, operation.Ids, operation.Redo) });
            BatchPayload.Write(ref undo, new[] { new BatchPayload.Sub(operation.UndoKind, operation.Ids, operation.Undo) });
            return new Prepared { Kind = OpKind.Batch, UndoKind = OpKind.Batch, Ids = touched, Redo = redo.Written.ToArray(), Undo = undo.Written.ToArray() };
        }
        finally { redo.Dispose(); undo.Dispose(); }
    }

    private Prepared PerformSpawn(byte actor, ulong baseRev, byte[] payload)
    {
        if (!SpawnPayload.TryRead(payload, out ushort subId, out byte[] xml)) { ModLog.Error("subeditor: PerformSpawn SpawnPayload.TryRead failed"); return Fail(SubmitStatus.Malformed); }
        Submarine sub = SubRegistry.Find(subId);
        int[] blockIds = EntityOps.ElementIds(xml);
        if (sub == null && subId != SubRegistry.WorldId) { ModLog.Error($"subeditor: PerformSpawn unknown submarine wire id {subId}"); return Fail(SubmitStatus.Malformed); }
        if (blockIds == null || blockIds.Length == 0) { ModLog.Error("subeditor: PerformSpawn blockIds is null or empty"); return Fail(SubmitStatus.Malformed); }
        if (blockIds.Distinct().Count() != blockIds.Length) { ModLog.Error("subeditor: PerformSpawn duplicate blockIds in xml payload"); return Fail(SubmitStatus.Malformed); }
        if (!EntityOps.TryFindIdOffset(blockIds.Length, out int offset)) { ModLog.Error($"subeditor: PerformSpawn TryFindIdOffset failed for {blockIds.Length} entities"); return Fail(SubmitStatus.Malformed); }
        List<MapEntity> loaded = EntityOps.LoadAt(xml, sub, offset);
        if (loaded == null || loaded.Count == 0) { ModLog.Error("subeditor: PerformSpawn LoadAt returned null or empty"); return Fail(SubmitStatus.Malformed); }
        List<MapEntity> all = EntityOps.WithContents(loaded.Select(e => e.ID).ToArray());
        ushort[] ids = all.Select(e => e.ID).ToArray();
        var placements = new InventoryPlacement[ids.Length];
        for (int i = 0; i < ids.Length; i++) { if (all[i] is Item item) { InventoryOps.Capture(item, out placements[i]); } }
        ushort[] touched = ids.Concat(InventoryOps.Touched(ids, ReadOnlySpan<InventoryPlacement>.Empty, placements)).Distinct().OrderBy(id => id).ToArray();
        int conflict = authority.Versions.FirstConflict(touched, actor, baseRev);
        if (conflict >= 0) { EntityOps.Delete(ids); return Fail(SubmitStatus.Conflict, conflict); }
        var w = new ByteWriter(256); SpawnPayload.Write(ref w, subId, EntityOps.SaveXml(all));
        var p = new Prepared { Kind = OpKind.Spawn, UndoKind = OpKind.Delete, Ids = ids, Redo = w.Written.ToArray(), Undo = Array.Empty<byte>() };
        w.Dispose();
        return WithDependencies(p, touched);
    }

    private Prepared PerformWire(byte actor, ulong baseRev, byte[] payload)
    {
        if (!WirePayload.TryRead(payload, out WireState[] targets)) { return Fail(SubmitStatus.Malformed); }
        if (!EntityOps.CanApplyWires(targets)) { return Fail(SubmitStatus.Malformed); }
        var current = new WireState[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            if (!EntityOps.CaptureWire(targets[i].WireId, out current[i])) { return Fail(SubmitStatus.Malformed); }
        }
        ushort[] ids = EntityOps.WireTouchedIds(targets, current);
        if (!EntityOps.AllExist(ids)) { return Fail(SubmitStatus.Malformed); }
        int conflict = authority.Versions.FirstConflict(ids, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }
        var w = new ByteWriter(128); WirePayload.Write(ref w, current);
        var p = new Prepared { Kind = OpKind.Wire, UndoKind = OpKind.Wire, Ids = ids, Redo = payload, Undo = w.Written.ToArray() };
        w.Dispose();
        return ApplyPrepared(p);
    }

    /// <summary>
    /// Runs the sub-operations in order against the real world, then records them as one revision.
    /// Undo replays the inverse sub-operations in reverse order, so the whole batch is undone atomically.
    /// </summary>
    private Prepared PerformBatch(byte actor, ulong baseRev, byte[] payload)
    {
        if (!BatchPayload.TryRead(payload, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] subs)) { return Fail(SubmitStatus.Malformed); }
        var redo = new List<BatchPayload.Sub>(); var undo = new List<BatchPayload.Sub>();
        var touched = new SortedSet<ushort>();
        bool complete = false;
        try
        {
            foreach (BatchPayload.Sub sub in subs)
            {
                Prepared p = sub.Kind is OpKind.Batch or OpKind.WireNew or OpKind.SpawnSub or OpKind.DeleteSub or OpKind.Circuit ? Fail(SubmitStatus.Malformed) : Perform(actor, sub.Kind, baseRev, sub.Ids, sub.Payload);
                if (!p.Ok)
                {
                    return p;
                }
                if (p.Kind == OpKind.Batch && BatchPayload.TryRead(p.Redo, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] forward)) { redo.AddRange(forward); }
                else { redo.Add(new BatchPayload.Sub(p.Kind, p.Ids, p.Redo)); }
                if (p.UndoKind == OpKind.Batch && BatchPayload.TryRead(p.Undo, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] backward)) { undo.AddRange(backward.Reverse()); }
                else { undo.Add(new BatchPayload.Sub(p.UndoKind, p.Ids, p.Undo)); }
                foreach (ushort id in p.Ids) { touched.Add(id); }
            }
            var wr = new ByteWriter(256); BatchPayload.Write(ref wr, redo.ToArray());
            var wu = new ByteWriter(256); BatchPayload.Write(ref wu, undo.AsEnumerable().Reverse().ToArray());
            var result = new Prepared { Kind = OpKind.Batch, UndoKind = OpKind.Batch, Ids = touched.ToArray(), Redo = wr.Written.ToArray(), Undo = wu.Written.ToArray() };
            wr.Dispose(); wu.Dispose();
            complete = true;
            return result;
        }
        finally
        {
            if (!complete)
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    if (!ApplyDirect(undo[i].Kind, undo[i].Ids, undo[i].Payload)) { throw new InvalidOperationException("Editor batch rollback failed"); }
                }
            }
        }
    }


    private Prepared PerformMeta(byte actor, ulong baseRev, ushort[] ids, byte[] payload)
    {
        if (ids.Length != 1 || ids[0] == 0 || SubRegistry.Find(ids[0]) is not Submarine sub || !MetaPayload.TryRead(payload, out MetaPayload.Pair[] pairs) || !SubMeta.KnowsAll(pairs)) { return Fail(SubmitStatus.Malformed); }
        int conflict = authority.Versions.FirstConflict(ids, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }
        var w = new ByteWriter(128); MetaPayload.Write(ref w, SubMeta.CaptureKeys(sub.Info, pairs));
        var p = new Prepared { Kind = OpKind.SubMeta, UndoKind = OpKind.SubMeta, Ids = ids, Redo = payload, Undo = w.Written.ToArray() };
        w.Dispose();
        return ApplyPrepared(p);
    }

    /// <summary>The request names a saved submarine and a position; the server picks the id window, builds it, and announces the result.</summary>
    private Prepared PerformSpawnSub(byte[] payload)
    {
        if (!SubSpawnPayload.TryRead(payload, out SubSpawn request) || SubRegistry.Spawned.Count >= SubRegistry.MaxSpawned) { return Fail(SubmitStatus.Malformed); }
        SubmarineInfo info = SubRegistry.FindInfo(request.Name, request.Hash);
        XElement blueprint = EntityOps.Blueprint(info?.SubmarineElement);
        int[] fileIds = blueprint == null ? null : EntityOps.ElementIds(blueprint);
        if (fileIds == null || fileIds.Length == 0 || fileIds.Distinct().Count() != fileIds.Length || !EntityOps.TryFindIdOffset(fileIds.Length, out int offset)) { return Fail(SubmitStatus.Malformed); }
        Submarine sub = SubRegistry.Spawn(info, offset, new Microsoft.Xna.Framework.Vector2(request.X, request.Y), request.Thalamus, out SubSpawn spawn);
        if (sub == null) { return Fail(SubmitStatus.Malformed); }
        var w = new ByteWriter(128); SubSpawnPayload.Write(ref w, spawn);
        var p = new Prepared { Kind = OpKind.SpawnSub, UndoKind = OpKind.DeleteSub, Ids = new[] { spawn.SubmarineId }, Redo = w.Written.ToArray(), Undo = Array.Empty<byte>() };
        w.Dispose();
        return p;
    }

    private Prepared PerformDeleteSub(byte actor, ulong baseRev, ushort[] ids, byte[] payload)
    {
        if (ids.Length != 1 || payload.Length != 0) { return Fail(SubmitStatus.Malformed); }
        int index = SubRegistry.Spawned.ToList().FindIndex(x => x.SubmarineId == ids[0]);
        if (index < 0) { return Fail(SubmitStatus.Malformed); }
        int conflict = authority.Versions.FirstConflict(ids, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }
        SubSpawn spawn = SubRegistry.Spawned[index];
        if (SubRegistry.Find(ids[0]) is not Submarine removed || !SubRegistry.Capture(removed, spawn, out spawn)) { return Fail(SubmitStatus.Malformed); }
        var w = new ByteWriter(128); SubSpawnPayload.Write(ref w, spawn);
        var p = new Prepared { Kind = OpKind.DeleteSub, UndoKind = OpKind.SpawnSub, Ids = ids, Redo = Array.Empty<byte>(), Undo = w.Written.ToArray() };
        w.Dispose();
        // Entities the removed submarine held are gone; their ids may be handed to another submarine, which must not inherit "created by an editor".
        if (SubRegistry.Find(ids[0]) is Submarine doomed)
        {
            for (int i = 0; i < MapEntity.MapEntityList.Count; i++) { MapEntity e = MapEntity.MapEntityList[i]; if (e?.Submarine == doomed) { createdByEditor[e.ID] = false; } }
        }
        SubRegistry.Unload(ids[0]);
        return p;
    }

    /// <summary>
    /// A wire drawn by an editor exists only on that client. The server creates it under an id from its own window,
    /// connects it, and records spawn+connect as one revision; undo deletes the wire (which disconnects it).
    /// </summary>
    private Prepared PerformWireNew(byte actor, ulong baseRev, byte[] payload)
    {
        if (!WireNewPayload.TryRead(payload, SubEditorProtocol.MaxFrameBytes, out ushort subId, out byte[] xml, out WireState drawn)) { return Fail(SubmitStatus.Malformed); }
        Submarine sub = SubRegistry.Find(subId);
        if (sub == null && subId != SubRegistry.WorldId) { return Fail(SubmitStatus.Malformed); }
        int[] drawnIds = EntityOps.ElementIds(xml);
        if (drawnIds == null || drawnIds.Length != 1 || drawnIds[0] != drawn.WireId || !EntityOps.TryFindIdOffset(1, out int offset)) { return Fail(SubmitStatus.Malformed); }
        if (drawn.End0.ItemId == 0 && drawn.End1.ItemId == 0 || !EntityOps.CanApplyWire(drawn, creating: true)) { return Fail(SubmitStatus.Malformed); }
        ushort wireId = (ushort)offset;   // the loader numbers a single entity at the window's first id
        WireState final = drawn with { WireId = wireId };
        ushort[] touched = EntityOps.WireTouchedIds(new[] { final }, Array.Empty<WireState>());
        int conflict = authority.Versions.FirstConflict(touched, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }
        List<MapEntity> loaded = EntityOps.LoadAt(xml, sub, offset);
        if (loaded == null || loaded.Count != 1 || loaded[0].ID != wireId || loaded[0] is not Item item || item.GetComponent<Barotrauma.Items.Components.Wire>() == null)
        {
            if (loaded != null) { EntityOps.Delete(loaded.Select(e => e.ID).OrderBy(i => i).ToArray()); }
            return Fail(SubmitStatus.Malformed);
        }
        if (!EntityOps.ApplyWire(final))
        {
            EntityOps.Delete(new[] { wireId });
            return Fail(SubmitStatus.Malformed);
        }
        var sp = new ByteWriter(256); SpawnPayload.Write(ref sp, subId, EntityOps.SaveXml(loaded));
        var wp = new ByteWriter(128); WirePayload.Write(ref wp, new[] { final });
        var redo = new ByteWriter(512);
        BatchPayload.Write(ref redo, new[] { new BatchPayload.Sub(OpKind.Spawn, new[] { wireId }, sp.Written.ToArray()), new BatchPayload.Sub(OpKind.Wire, touched, wp.Written.ToArray()) });
        var undo = new ByteWriter(64);
        BatchPayload.Write(ref undo, new[] { new BatchPayload.Sub(OpKind.Delete, new[] { wireId }, Array.Empty<byte>()) });
        var p = new Prepared { Kind = OpKind.Batch, UndoKind = OpKind.Batch, Ids = touched, Redo = redo.Written.ToArray(), Undo = undo.Written.ToArray() };
        sp.Dispose(); wp.Dispose(); redo.Dispose(); undo.Dispose();
        return p;
    }

    // ---------------------------------------------------------------------------------------------- console commands

    /// <summary>Session ids start at 1, so 0 is free for the server console.</summary>
    private const byte ConsoleActor = 0;

    /// <summary>
    /// <c>spawnsub</c> / <c>deletesub</c> run through the same pipeline as the editor's Add/Remove submarine: one revision,
    /// one broadcast to every connected editor session, a snapshot entry for anyone who joins later.
    /// </summary>
    private (bool Ok, string Text) RunConsoleOp(Client client, OpKind kind, ushort[] ids, byte[] payload, Func<string> done)
    {
        byte actor = client?.SessionId ?? ConsoleActor;
        SubmitResult result;
        try { result = Execute(actor, kind, authority.Rev, ids, payload); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException)
        {
            ModLog.Once($"subeditor: {kind} from the console failed: {e.Message}");
            return (false, $"The server could not do that: {e.Message}");
        }
        if (result.Status != SubmitStatus.Accepted) { return (false, "The server refused that (the submarine may not exist, no entity id window is free, or its file cannot be built)."); }
        Broadcast(result.Rev);
        return (true, done());
    }

    internal static (bool Ok, string Text) SpawnFromConsole(Client client, Microsoft.Xna.Framework.Vector2? cursor, string[] args)
    {
        SubEditorServer s = instance;
        if (s?.authority == null || !RoundActive) { return (false, "There is no running round to add a submarine to."); }
        if (args.Length == 0) { return (false, "Give the name of the submarine: spawnsub [name] [x y]"); }
        SubmarineInfo info = null;
        int nameTokens = 0;
        for (int len = args.Length; len >= 1; len--)
        {
            string candidate = string.Join(" ", args.Take(len));
            info = SubmarineInfo.SavedSubmarines.FirstOrDefault(i => !i.IsFileCorrupted &&
                (i.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) || i.DisplayName.Value.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
            if (info != null) { nameTokens = len; break; }
        }
        if (info == null) { return (false, $"Could not find a submarine matching \"{string.Join(" ", args)}\"."); }

        string[] remaining = args.Skip(nameTokens).ToArray();
        bool thalamus = remaining.Any(a => bool.TryParse(a, out bool on) && on);
        string[] numbers = remaining.Where(a => !bool.TryParse(a, out _)).ToArray();
        Microsoft.Xna.Framework.Vector2 at;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (numbers.Length >= 2 && float.TryParse(numbers[0], System.Globalization.NumberStyles.Float, invariant, out float x) && float.TryParse(numbers[1], System.Globalization.NumberStyles.Float, invariant, out float y) && float.IsFinite(x) && float.IsFinite(y))
        {
            at = new Microsoft.Xna.Framework.Vector2(x, y);
        }
        else if (cursor.HasValue) { at = cursor.Value; }
        else
        {
            // No cursor on a console: next to the main submarine, a gap wide enough that the two hulls cannot start overlapping.
            Submarine main = Submarine.MainSub;
            at = main.WorldPosition + new Microsoft.Xna.Framework.Vector2((main.Borders.Width + info.Dimensions.X) / 2f + 500f, 0f);
        }

        var request = new SubSpawn(info.Name, info.MD5Hash?.StringRepresentation ?? "", 1, 0, at.X, at.Y, thalamus);
        var w = new ByteWriter(128);
        byte[] payload;
        try { SubSpawnPayload.Write(ref w, request); payload = w.Written.ToArray(); }
        finally { w.Dispose(); }
        return s.RunConsoleOp(client, OpKind.SpawnSub, Array.Empty<ushort>(), payload, () =>
        {
            SubSpawn made = SubRegistry.Spawned[^1];
            return $"Added submarine \"{made.Name}\" (id {made.SubmarineId}) at {made.X:0}, {made.Y:0}. Remove it with: deletesub {made.SubmarineId}";
        });
    }

    internal static (bool Ok, string Text) DeleteFromConsole(Client client, string[] args)
    {
        SubEditorServer s = instance;
        if (s?.authority == null || !RoundActive) { return (false, "There is no running round."); }
        IReadOnlyList<SubSpawn> spawned = SubRegistry.Spawned;
        if (args.Length == 0)
        {
            return (false, spawned.Count == 0 ? "No submarine was added with spawnsub or the editor." : "Give a name or an id: deletesub [name|id]. Added so far: " + string.Join(", ", spawned.Select(x => $"{x.Name} (id {x.SubmarineId})")));
        }
        // The most recently added match, so that repeating the command peels submarines off in reverse order.
        string candidate = string.Join(" ", args);
        int index = ushort.TryParse(args[0], out ushort number)
            ? spawned.ToList().FindLastIndex(x => x.SubmarineId == number)
            : spawned.ToList().FindLastIndex(x => x.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (index < 0) { return (false, $"No submarine added with spawnsub or the editor matches \"{args[0]}\"."); }
        SubSpawn target = spawned[index];
        return s.RunConsoleOp(client, OpKind.DeleteSub, new[] { target.SubmarineId }, Array.Empty<byte>(), () => $"Removed submarine \"{target.Name}\" (id {target.SubmarineId}).");
    }

    // ---------------------------------------------------------------------------------------------- notices

    private void Notice(Client c, string text) =>
        INet.Net.Send(Channel, c.Connection, new RoutedPacket(sessions.TryGetValue(c.SessionId, out Session session) ? session.Token : 0,
            w => { w.WriteByte((byte)ServerPacket.Notice); w.WriteString(text); }));

    /// <summary>
    /// Waypoint generation is shared engine code, so the server runs it and records the difference as one undoable batch:
    /// remove the old path waypoints, create the new ones.
    /// </summary>
    private Prepared PerformWaypoints(byte actor, ulong baseRev)
    {
        Submarine sub = Submarine.MainSub;
        if (sub == null) { return Fail(SubmitStatus.Malformed); }
        using var world = new EditorWorldScope(sub);
        List<MapEntity> old = WayPoint.WayPointList.Where(wp => wp.SpawnType == SpawnType.Path).Cast<MapEntity>().ToList();
        ushort[] oldIds = old.Select(e => e.ID).OrderBy(i => i).ToArray();
        int conflict = oldIds.Length == 0 ? -1 : authority.Versions.FirstConflict(oldIds, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }
        byte[] oldXml = EntityOps.SaveXml(old);
        bool complete = false;
        try
        {
            if (!WayPoint.GenerateSubWaypoints(sub))
            {
                return Fail(SubmitStatus.Malformed);
            }
            List<MapEntity> generated = WayPoint.WayPointList.Where(wp => wp.SpawnType == SpawnType.Path).Cast<MapEntity>().ToList();
            byte[] generatedXml = EntityOps.SaveXml(generated);
            EntityOps.Delete(generated.Select(e => e.ID).OrderBy(id => id).ToArray());
            if (!EntityOps.TryFindIdOffset(generated.Count, out int offset) || EntityOps.LoadAt(generatedXml, sub, offset) is not List<MapEntity> now)
            {
                return Fail(SubmitStatus.Malformed);
            }
            ushort[] newIds = now.Select(e => e.ID).OrderBy(i => i).ToArray();
            ushort subId = SubRegistry.WireId(sub);

            var wNew = new ByteWriter(256); SpawnPayload.Write(ref wNew, subId, EntityOps.SaveXml(now));
            var wOld = new ByteWriter(256); SpawnPayload.Write(ref wOld, subId, oldXml);
            var redo = new List<BatchPayload.Sub>(); var undo = new List<BatchPayload.Sub>();
            if (oldIds.Length > 0) { redo.Add(new BatchPayload.Sub(OpKind.Delete, oldIds, Array.Empty<byte>())); undo.Add(new BatchPayload.Sub(OpKind.Spawn, oldIds, wOld.Written.ToArray())); }
            if (newIds.Length > 0) { redo.Add(new BatchPayload.Sub(OpKind.Spawn, newIds, wNew.Written.ToArray())); undo.Insert(0, new BatchPayload.Sub(OpKind.Delete, newIds, Array.Empty<byte>())); }
            wNew.Dispose(); wOld.Dispose();
            if (redo.Count == 0) { return Fail(SubmitStatus.Malformed); }
            var wr = new ByteWriter(256); BatchPayload.Write(ref wr, redo.ToArray());
            var wu = new ByteWriter(256); BatchPayload.Write(ref wu, undo.ToArray());
            var touched = new SortedSet<ushort>(oldIds.Concat(newIds));
            var p = new Prepared { Kind = OpKind.Batch, UndoKind = OpKind.Batch, Ids = touched.ToArray(), Redo = wr.Written.ToArray(), Undo = wu.Written.ToArray() };
            wr.Dispose(); wu.Dispose();
            complete = true;
            return p;
        }
        finally
        {
            if (!complete)
            {
                EntityOps.Delete(WayPoint.WayPointList.Where(wp => wp.SpawnType == SpawnType.Path).Select(wp => wp.ID).OrderBy(id => id).ToArray());
                if (oldIds.Length > 0 && EntityOps.LoadExact(oldXml, sub) == null) { ModLog.Error("subeditor: waypoint rollback failed"); }
            }
        }
    }
}
