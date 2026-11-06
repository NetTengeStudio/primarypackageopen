using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

public enum LinkState : byte { Off, Joining, Live }

/// <summary>
/// Client side of the live submarine editor. Every client runs it so that edits made by any GM reach everyone;
/// only clients with the permission can also open the editor (F4) and send requests.
/// </summary>
public sealed class SubEditorClient : IModule, IRoundEnded, IEventUpdate, IEventRoundEnded
{
    private readonly record struct SnapshotEntry(ushort Id, EntityState State, ushort Submarine, byte[] Xml);

    private readonly Replica replica;
    private readonly Dictionary<ushort, SnapshotEntry> entries = new();
    private readonly Dictionary<uint, Action> rollbacks = new();
    private readonly Dictionary<uint, Action> accepted = new();
    private readonly HashSet<uint> replacements = new();
    private readonly Dictionary<uint, long> pendingRequests = new();
    private readonly List<(ulong Rev, Action Done)> beforeRevision = new();
    /// <summary>Accepted requests whose cleanup waits for the server's own frame: dropping the local copy earlier would leave a gap on screen.</summary>
    private readonly List<(ulong Rev, Action Done)> afterRevision = new();
    private SubSpawn[] pendingSubs = Array.Empty<SubSpawn>();
    private readonly List<byte[]> pendingCircuit = new();
    private IDisposable receiver;
    private LiveEditorScreen editor;
    private LinkState state;
    private bool compatible, incompatible;
    private ulong sessionToken;
    private bool snapshotReady, snapshotPrepared;
    private ulong announcedRevision;
    private ulong snapshotCut;
    private ushort snapshotNativeEventId;
    private int expectedChunk;
    private long snapshotBytes, lastActivity, joinSilence = SubEditorProtocol.JoinSilenceMs;
    private uint nextRequest = 1, joinNumber;
    private static SubEditorClient instance;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static long NowMs => Clock.ElapsedMilliseconds;

    public SubEditorClient() { replica = new Replica(ApplyFrame, NativeEventsReady); }

    private static bool NativeEventsReady(OpFrame.View frame) => GameMain.Client?.EntityEventManager is { } events &&
        !GameMain.Client.MidRoundSyncing && !NetIdUtils.IdMoreRecent(frame.NativeEventId, events.LastReceivedID);

    public static LinkState State => instance?.state ?? LinkState.Off;
    public static bool Editing => instance?.editor is { IsOpen: true } editor && Screen.Selected == editor;
    public static ulong AppliedRevision => instance?.replica.Applied ?? 0;
    internal static ulong SessionToken => instance?.sessionToken ?? 0;

    public void Attach(Harmony harmony)
    {
        instance = this;
        receiver = INet.Net.Listen(SubEditorProtocol.Channel, Receive);
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        CommandBridge.Attach(harmony);
        ClientNetworkContext.Attach(harmony);
        InventoryOps.Attach(harmony);
        ExactIds.Attach(harmony);
        SubIds.Attach(harmony);
        SubRegistry.Attach(harmony);
        CircuitOps.Attach(harmony);
        LiveView.Attach(harmony);
        EditorInteractionPatches.Attach(harmony);
        SubRegistry.LoadFailed += OnSubLoadFailed;
        ModCommands.Register("deletesub",
            "deletesub [name|id]: Remove a submarine added by spawnsub or the live editor, for everyone. Without an argument lists them.",
            _ => DebugConsole.NewMessage("deletesub works in a multiplayer round with console permission.", Microsoft.Xna.Framework.Color.Orange),
            localOnClient: false, relayToServer: true,
            validArgs: () => new[] { SubRegistry.Spawned.Select(s => s.Name).Distinct().ToArray() });
        LayerTracker.Attach(harmony);
        LinkTracker.Attach(harmony);
        FlipTracker.Attach(harmony);
        LuaCsSetup.Instance?.EventService?.Subscribe<IEventRoundEnded>(this);
    }

    public void RoundEnd() => OnRoundEnd();

    public void OnRoundEnd()
    {
        if (editor != null && editor.IsOpen) { editor.Close(); }
        state = LinkState.Off;
        compatible = incompatible = false;
        sessionToken = 0;
        snapshotReady = snapshotPrepared = false;
        announcedRevision = 0;
        pendingRequests.Clear();
        replica.Reset();
        entries.Clear(); rollbacks.Clear(); accepted.Clear(); afterRevision.Clear(); replacements.Clear(); beforeRevision.Clear();
        InventoryReplication.Reset(); InventoryBridge.Reset(); WireTracker.Reset(); LayerTracker.Reset(); LiveView.Reset();
        pendingSubs = Array.Empty<SubSpawn>(); pendingCircuit.Clear();
        LiveHistory.Clear();
    }

    public void RoundStart()
    {
        if (GameMain.Client == null) { return; }
        SubRegistry.Reset();
        awaitingFiles.Clear();
        Join();
    }

    public void Release()
    {
        state = LinkState.Off;
        WireTracker.Quietly(() =>
        {
            if (editor is { IsOpen: true }) { editor.Close(); }
            foreach (Action rollback in rollbacks.Values.Reverse()) { rollback(); }
            InventoryBridge.Reset(restore: true);
        });
        OnRoundEnd();
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventRoundEnded>(this);
        SubRegistry.LoadFailed -= OnSubLoadFailed;
        receiver?.Dispose(); receiver = null;
        instance = null;
    }

    /// <summary>The world here would differ from everyone else's: tell the player and tell the server, which logs it for its operators.</summary>
    private static void OnSubLoadFailed(SubSpawn spawn, string reason, bool fileMissing)
    {
        // A file this client lacks is fetched with the game's own submarine transfer; once it is here, joining again builds the submarine from the snapshot.
        bool fetching = fileMissing && instance != null && instance.AwaitFile(spawn);
        string text = fetching
            ? $"Submarine \"{spawn.Name}\" was added to this round. Downloading it from the server..."
            : $"Submarine \"{spawn.Name}\" added to this round cannot be built here: {reason}.";
        DebugConsole.NewMessage(text, Microsoft.Xna.Framework.Color.Orange);
        global::Barotrauma.GUI.AddMessage(text, Microsoft.Xna.Framework.Color.Orange);
        if (!fetching) { INet.Net.Send(SubEditorProtocol.Channel, w => { SubEditorProtocol.WriteClientHeader(w, ClientPacket.Missing, SessionToken); w.WriteString($"{spawn.Name} ({reason})"); }); }
    }

    private readonly Dictionary<(string Name, string Hash), long> awaitingFiles = new();
    private long lastFileCheck;

    /// <summary>Asks the server for the submarine file. False when it cannot be asked for (no hash to verify it against).</summary>
    private bool AwaitFile(SubSpawn spawn)
    {
        if (spawn.Hash.Length == 0 || GameMain.Client == null) { return false; }
        var key = (spawn.Name, spawn.Hash);
        if (awaitingFiles.ContainsKey(key)) { return true; }
        awaitingFiles[key] = NowMs;
        GameMain.Client.RequestFile(FileTransferType.Submarine, spawn.Name, spawn.Hash);
        return true;
    }

    /// <summary>Joins again once a requested file has arrived; gives up on one that has not after a few minutes.</summary>
    private void PollFiles(long now)
    {
        if (awaitingFiles.Count == 0 || now - lastFileCheck < 1000) { return; }
        lastFileCheck = now;
        bool arrived = false;
        foreach (var (key, since) in awaitingFiles.ToArray())
        {
            if (SubRegistry.FindInfo(key.Name, key.Hash) != null) { awaitingFiles.Remove(key); arrived = true; }
            else if (now - since > 180_000)
            {
                awaitingFiles.Remove(key);
                global::Barotrauma.GUI.AddMessage($"Submarine \"{key.Name}\" could not be downloaded; this client's view of the round differs from the server's.", Microsoft.Xna.Framework.Color.Orange);
                INet.Net.Send(SubEditorProtocol.Channel, w => { SubEditorProtocol.WriteClientHeader(w, ClientPacket.Missing, SessionToken); w.WriteString($"{key.Name} (download did not finish)"); });
            }
        }
        if (arrived && state != LinkState.Off) { Join(); }
    }

    // ---------------------------------------------------------------------------------------------- join

    /// <param name="retry">The previous attempt went unanswered: keep the lengthened wait instead of starting over with the short one.</param>
    private void Join(bool retry = false)
    {
        WireTracker.Quietly(() =>
        {
            foreach (Action rollback in rollbacks.Values.Reverse()) { rollback(); }
            foreach (uint id in replacements) { if (accepted.TryGetValue(id, out Action cleanup)) { cleanup(); } }
            foreach (var pending in beforeRevision) { pending.Done(); }
        });
        replica.Reset();
        pendingRequests.Clear();
        compatible = false;
        sessionToken = 0;
        snapshotReady = snapshotPrepared = false;
        announcedRevision = 0;
        entries.Clear(); rollbacks.Clear(); accepted.Clear(); afterRevision.Clear(); replacements.Clear(); beforeRevision.Clear(); InventoryBridge.Reset(restore: true); WireTracker.Reset(); LayerTracker.Reset(); LiveView.Reset();
        pendingSubs = Array.Empty<SubSpawn>(); pendingCircuit.Clear();
        LiveHistory.Clear();
        expectedChunk = 0; snapshotBytes = 0;
        if (!retry) { joinSilence = SubEditorProtocol.JoinSilenceMs; }
        state = LinkState.Joining;
        SendHello();
    }

    private void SendHello()
    {
        lastActivity = NowMs;
        uint number = ++joinNumber;
        INet.Net.Send(SubEditorProtocol.Channel, w => { w.WriteByte((byte)ClientPacket.Hello); w.WriteUInt32(number); w.WriteUInt16(SubEditorProtocol.Version); });
    }

    private void Receive(IReadMessage m)
    {
        if (state == LinkState.Off) { return; }
        if (!SubEditorProtocol.ReadServerHeader(m, out ulong token, out ServerPacket packet)) { Incompatible(); return; }
        if (packet == ServerPacket.Welcome)
        {
            ushort version = m.ReadUInt16(); uint join = m.ReadUInt32();
            if (join != joinNumber) { return; }
            if (version != SubEditorProtocol.Version || token == 0) { Incompatible(); return; }
            sessionToken = token; compatible = true;
            return;
        }
        if (packet != ServerPacket.Incompatible && (token == 0 || token != sessionToken)) { return; }
        switch (packet)
        {
            case ServerPacket.Incompatible:
                m.ReadUInt16();
                if (m.ReadUInt32() == joinNumber) { Incompatible(); }
                break;
            case ServerPacket.SnapChunk: OnChunk(m); break;
            case ServerPacket.Op: OnOp(m); break;
            case ServerPacket.Ack:
            {
                uint id = m.ReadUInt32(); m.ReadByte(); ulong rev = m.ReadUInt64();
                if (!pendingRequests.Remove(id)) { break; }
                replica.Expect(rev);
                ModLog.Message($"subeditor: request {id} accepted as revision {rev}, applied here up to {replica.Applied}");
                rollbacks.Remove(id);
                if (accepted.Remove(id, out Action done))
                {
                    if (replacements.Remove(id)) { beforeRevision.Add((rev, done)); RunReplacements(replica.Applied); }
                    else { afterRevision.Add((rev, done)); }
                    RunAccepted();
                }
                break;
            }
            case ServerPacket.Notice: global::Barotrauma.GUI.AddMessage(m.ReadString(), Microsoft.Xna.Framework.Color.White); break;
            case ServerPacket.Reject: OnRejected(m, denied: false); break;
            case ServerPacket.Denied: OnRejected(m, denied: true); break;
            case ServerPacket.Restart: Join(); break;
            case ServerPacket.Presence: LiveView.OnPresence(m); break;
        }
    }

    private void OnChunk(IReadMessage m)
    {
        if (state != LinkState.Joining) { return; }
        if (!compatible) { Incompatible(); return; }
        uint number = m.ReadUInt32();
        uint index = m.ReadVariableUInt32();
        byte[] blob = SubEditorProtocol.ReadBlob(m, SubEditorProtocol.MaxChunkBytes * 2);
        if (number != joinNumber) { return; }   // left over from an attempt this client abandoned: its numbering is not ours
        lastActivity = NowMs; joinSilence = SubEditorProtocol.JoinSilenceMs;
        if (blob == null || blob.Length == 0) { return; }
        if (index != expectedChunk) { SendAck(); return; }   // duplicate or hole: the cumulative ack makes the server go back
        var kind = (ChunkKind)blob[0];
        if (kind == ChunkKind.Subs)
        {
            if (!SubListPayload.TryRead(blob.AsSpan(1), out SubSpawn[] subs)) { Join(); return; }
            pendingSubs = subs;
            expectedChunk++;
            SendAck();
            return;
        }
        if (kind == ChunkKind.Circuit)
        {
            var rd = new ByteReader(blob.AsSpan(1));
            ulong count = rd.V();
            if (rd.Failed || count > 4096 || pendingCircuit.Count + (int)count > 4096) { Join(); return; }
            for (ulong i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> frame = rd.Bytes(SubEditorProtocol.MaxFrameBytes * 2);
                if (rd.Failed) { Join(); return; }
                if (snapshotBytes > SubEditorProtocol.MaxSnapshotBytes - frame.Length) { Join(); return; }
                snapshotBytes += frame.Length;
                pendingCircuit.Add(frame.ToArray());
            }
            if (!rd.AtEnd) { Join(); return; }
            expectedChunk++;
            SendAck();
            return;
        }
        if (kind == ChunkKind.Done)
        {
            int pos = 1;
            if (!VarInt.TryRead(blob, ref pos, out ulong cut) || !VarInt.TryRead(blob, ref pos, out ulong nativeEventId) || nativeEventId > ushort.MaxValue || pos != blob.Length) { Join(); return; }
            if (snapshotReady && (snapshotCut != cut || snapshotNativeEventId != nativeEventId)) { Join(); return; }
            snapshotReady = true; snapshotCut = cut; snapshotNativeEventId = (ushort)nativeEventId;
            FinishSnapshot();
            return;
        }
        byte[] raw = kind == ChunkKind.DataBrotli ? Decompress(blob) : blob.AsSpan(1).ToArray();
        if (raw == null || !ParseChunk(raw)) { Join(); return; }
        expectedChunk++;
        SendAck();
    }

    private void SendAck()
    {
        uint ack = (uint)expectedChunk, number = joinNumber;
        INet.Net.Send(SubEditorProtocol.Channel, w => { SubEditorProtocol.WriteClientHeader(w, ClientPacket.SnapAck, sessionToken); w.WriteUInt32(number); w.WriteVariableUInt32(ack); });
    }

    private void FinishSnapshot()
    {
        if (!snapshotReady || state != LinkState.Joining) { return; }
        bool waitingForNative = GameMain.Client != null && GameMain.Client.MidRoundSyncing &&
            (GameMain.Client.EntityEventManager is not { } ev || NetIdUtils.IdMoreRecent(snapshotNativeEventId, ev.LastReceivedID));
        bool applied = false;
        WireTracker.Quietly(() => applied = ApplySnapshot(snapshotCut, snapshotNativeEventId, entitiesOnly: waitingForNative));
        if (!applied) { if (awaitingFiles.Count == 0) { Join(); } return; }
        if (waitingForNative) { return; }
        ulong cut = snapshotCut;
        snapshotReady = false;
        if (!replica.BeginAt(cut)) { Join(); return; }
        state = LinkState.Live;
        expectedChunk++;
        SendAck();
        AnnounceApplied(force: true);
    }

    private static byte[] Decompress(byte[] blob)
    {
        try
        {
            using var input = new MemoryStream(blob, 1, blob.Length - 1);
            using var br = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384]; int n; long total = 0;
            while ((n = br.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > SubEditorProtocol.MaxChunkBytes * 16L) { return null; }
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException) { return null; }
    }

    private bool ParseChunk(byte[] raw)
    {
        var rd = new ByteReader(raw);
        ulong count = rd.V();
        if (rd.Failed || count > 4096) { return false; }
        for (ulong i = 0; i < count; i++)
        {
            ulong id = rd.V(); var st = (EntityState)rd.U8();
            if (rd.Failed || id == 0 || id > ushort.MaxValue || st > EntityState.Meta) { return false; }
            if (st == EntityState.Gone) { if (!StoreEntry(new SnapshotEntry((ushort)id, st, 0, null))) { return false; } continue; }
            if (st == EntityState.Meta)
            {
                var meta = rd.Bytes(SubEditorProtocol.MaxChunkBytes);
                if (rd.Failed) { return false; }
                if (!StoreEntry(new SnapshotEntry((ushort)id, st, 0, meta.ToArray()))) { return false; }
                continue;
            }
            ulong sub = rd.V();
            var xml = rd.Bytes(SubEditorProtocol.MaxChunkBytes);
            if (rd.Failed || sub > ushort.MaxValue) { return false; }
            if (!StoreEntry(new SnapshotEntry((ushort)id, st, (ushort)sub, xml.ToArray()))) { return false; }
        }
        return rd.AtEnd;
    }

    private bool StoreEntry(SnapshotEntry entry)
    {
        long retained = snapshotBytes - (entries.TryGetValue(entry.Id, out SnapshotEntry previous) ? previous.Xml?.Length ?? 0 : 0);
        int bytes = entry.Xml?.Length ?? 0;
        if (retained > SubEditorProtocol.MaxSnapshotBytes - bytes) { return false; }
        entries[entry.Id] = entry; snapshotBytes = retained + bytes;
        return true;
    }

    /// <summary>
    /// One pass at the end so links between entities of different chunks resolve: removals, then editor-created entities
    /// (grouped per submarine so MapLoaded sees them together), then in-place state for entities that already existed.
    /// </summary>
    private bool ApplySnapshot(ulong cut = 0, ushort nativeEventId = 0, bool entitiesOnly = false)
    {
        try
        {
            ushort[] snapshotIds = entries.Keys.Order().ToArray();
            if (!snapshotPrepared)
            {
            InventoryReplication.Fence(snapshotIds, nativeEventId);
            SubRegistry.Reconcile(pendingSubs);
            foreach (SnapshotEntry e in entries.Values.Where(e => e.State == EntityState.Meta))
            {
                if (SubRegistry.Find(e.Id) is Submarine owner && MetaPayload.TryRead(e.Xml, out MetaPayload.Pair[] pairs)) { SubMeta.Apply(owner.Info, pairs); }
            }
            var gone = entries.Values.Where(e => e.State == EntityState.Gone).Select(e => e.Id).OrderBy(i => i).ToArray();
            EntityOps.DeleteExact(gone);
            SnapshotEntry[] full = entries.Values.Where(e => e.State == EntityState.Full).OrderBy(e => e.Id).ToArray();
            if (full.Length != 0)
            {
                EntityOps.DeleteExact(full.Select(e => e.Id).ToArray());
                var root = new XElement(EntityOps.SpawnRoot);
                foreach (SnapshotEntry e in full)
                {

                    foreach (XElement el in XElement.Parse(Encoding.UTF8.GetString(e.Xml)).Elements()) { root.Add(el); }
                }
                if (EntityOps.LoadExact(Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)), Submarine.MainSub) == null)
                {
                    throw new InvalidOperationException("Entity snapshot could not be loaded");
                }
            }
            snapshotPrepared = true;
            }
            if (entitiesOnly) { return true; }
            foreach (SnapshotEntry e in entries.Values)
            {
                if (e.State == EntityState.InPlace && EntityOps.Find(e.Id) is Item item && item.GetComponent<Barotrauma.Items.Components.Wire>() is { } wire)
                { wire.ClearConnections(); }
            }
            SnapshotEntry[] inplace = entries.Values.Where(e => e.State == EntityState.InPlace).OrderBy(e => e.Id).ToArray();
            if (!EntityOps.RestoreStates(inplace.Select(e => e.Id).ToArray(), inplace.Select(e => e.Xml).ToArray(), out string restoreFailure))
            { throw new InvalidOperationException("Entity state snapshot could not be restored: " + restoreFailure); }
            foreach (byte[] frame in pendingCircuit)
            {
                if (!OpFrame.TryRead(frame, out OpFrame.View view) || view.Kind != OpKind.Circuit || view.Rev != cut || !FrameApplier.Apply(view))
                {
                    throw new InvalidOperationException("Circuit snapshot could not be applied");
                }
            }
            InventoryReplication.Reconcile(snapshotIds, nativeEventId);
            return true;
        }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidOperationException or NullReferenceException)
        {
            ModLog.Error($"subeditor: snapshot did not apply cleanly: {e.Message}");
            return false;
        }
        finally { if (!entitiesOnly) { entries.Clear(); pendingCircuit.Clear(); LayerTracker.NamesChanged = true; } }
    }

    // ---------------------------------------------------------------------------------------------- live stream

    private void OnOp(IReadMessage m)
    {
        if (!compatible) { Incompatible(); return; }
        byte[] frame = SubEditorProtocol.ReadBlob(m, SubEditorProtocol.MaxFrameBytes * 2);
        if (frame == null) { return; }
        if (replica.Offer(frame) is OfferResult.Overflow or OfferResult.Failed) { Join(); return; }
        RunAccepted();
        AnnounceApplied(force: true);
    }

    private void AnnounceApplied(bool force = false)
    {
        if (state != LinkState.Live || !force && announcedRevision == replica.Applied) { return; }
        ulong revision = replica.Applied;
        if (INet.Net.Send(SubEditorProtocol.Channel, w => { SubEditorProtocol.WriteClientHeader(w, ClientPacket.Applied, sessionToken); w.WriteUInt64(revision); }))
        { announcedRevision = revision; }
    }

    private void RunAccepted()
    {
        for (int i = afterRevision.Count - 1; i >= 0; i--)
        {
            if (afterRevision[i].Rev > replica.Applied) { continue; }
            Action done = afterRevision[i].Done;
            afterRevision.RemoveAt(i);
            try { done(); } catch (Exception e) { ModLog.Once($"subeditor: acknowledged request cleanup failed: {e.Message}"); }
        }
    }

    private void RunReplacements(ulong revision)
    {
        for (int i = beforeRevision.Count - 1; i >= 0; i--)
        {
            if (beforeRevision[i].Rev > revision) { continue; }
            Action done = beforeRevision[i].Done;
            beforeRevision.RemoveAt(i);
            done();
        }
    }

    private static bool ApplyFrame(OpFrame.View v)
    {
        WireTracker.Enter();
        try
        {
            instance?.RunReplacements(v.Rev);
            Span<ushort> ids = stackalloc ushort[v.Count]; v.ReadIds(ids);
            InventoryReplication.Fence(ids, v.NativeEventId);
            if (!FrameApplier.ApplyTransaction(v)) { ModLog.Error($"subeditor: frame {v.Rev} ({v.Kind}) was not applied here"); return false; }
            InventoryReplication.Reconcile(ids, v.NativeEventId);
            Observe(v.Kind, v.Payload);
            return true;
        }
        catch (Exception e) { ModLog.Error($"subeditor: frame {v.Rev} ({v.Kind}) failed: {e}"); return false; }
        finally { WireTracker.Exit(); }
    }

    /// <summary>What the local trackers must learn from an applied operation, so that it is never mistaken for a local edit.</summary>
    private static void Observe(OpKind kind, ReadOnlySpan<byte> payload)
    {
        switch (kind)
        {
            case OpKind.SubMeta:
                instance?.editor?.RememberMetadata();
                break;
            case OpKind.Wire:
                if (WirePayload.TryRead(payload, out WireState[] wires)) { foreach (WireState w in wires) { WireTracker.Remember(w); } }
                break;
            case OpKind.Property:
                if (new ByteReader(payload).Str(256) == nameof(MapEntity.Layer)) { LayerTracker.NamesChanged = true; }
                break;
            case OpKind.Spawn:
            case OpKind.Delete:
            case OpKind.SpawnSub:
            case OpKind.DeleteSub:
                LayerTracker.NamesChanged = true;
                break;
            case OpKind.Batch:
                if (BatchPayload.TryRead(payload, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] subs))
                {
                    foreach (BatchPayload.Sub sub in subs) { Observe(sub.Kind, sub.Payload); }
                }
                break;
        }
    }

    private void OnRejected(IReadMessage m, bool denied)
    {
        uint id = m.ReadUInt32(); var status = (SubmitStatus)m.ReadByte(); m.ReadUInt64(); int conflict = m.ReadInt32();
        if (!pendingRequests.Remove(id)) { return; }
        accepted.Remove(id);
        replacements.Remove(id);
        LiveHistory.Rejected(id);
        if (conflict >= 0) { LiveView.FlashConflict((ushort)conflict); }
        if (rollbacks.Remove(id, out Action undo)) { try { WireTracker.Quietly(undo); } catch (Exception e) { ModLog.Once($"subeditor: rollback failed: {e.Message}"); } }
        string text = denied ? "Нет прав на правку подлодки."
            : status == SubmitStatus.Conflict ? "Другой редактор уже изменил это. Ваша правка отменена."
            : status == SubmitStatus.NothingToUndo ? "Отменять нечего."
            : status == SubmitStatus.NothingToRedo ? "Повторять нечего."
            : "Сервер отклонил правку.";
        ModLog.Error($"subeditor: request {id} refused by the server: {(denied ? "denied" : status.ToString())}");
        global::Barotrauma.GUI.AddMessage(text, Microsoft.Xna.Framework.Color.Orange);
        if (!denied && status is SubmitStatus.Conflict or SubmitStatus.Malformed) { Join(); }
    }

    private void Incompatible()
    {
        if (editor is { IsOpen: true }) { editor.Close(); }
        OnRoundEnd();
        incompatible = true;
        global::Barotrauma.GUI.AddMessage("Несовместимая версия сетевого редактора. Обновите Primary package на сервере и всех клиентах.", Microsoft.Xna.Framework.Color.Orange);
    }

    // ---------------------------------------------------------------------------------------------- frame update

    public void OnUpdate(double fixedDeltaTime)
    {
        InventoryReplication.Tick();
        if (GameMain.Client == null || GameMain.GameSession == null)
        {
            if (state != LinkState.Off) { OnRoundEnd(); }
            return;
        }
        if (state == LinkState.Off)
        {
            if (incompatible) { return; }
            if (GameMain.GameSession.IsRunning && Screen.Selected == GameMain.GameScreen && Submarine.MainSub != null)
            {
                RoundStart();
            }
            return;
        }
        long now = NowMs;
        if (state == LinkState.Joining && snapshotReady) { FinishSnapshot(); }
        if (state == LinkState.Live)
        {
            if (!replica.Drain()) { Join(); return; }
            RunAccepted();
            AnnounceApplied();
            bool stalled = false;
            foreach (long sentAt in pendingRequests.Values) { if (now - sentAt >= SubEditorProtocol.StallMs) { stalled = true; break; } }
            if (stalled) { Join(); }
        }
        if (state == LinkState.Joining && awaitingFiles.Count == 0 && now - lastActivity > joinSilence)
        {
            // Never give up: the server may have dropped this join (a stalled link, a round that had not started on its side yet). Start over, waiting longer each time.
            joinSilence = Math.Min(joinSilence * 2, SubEditorProtocol.JoinSilenceMaxMs);
            Join(retry: true);
        }
        if (state == LinkState.Live && replica.TryGetGap(now, SubEditorProtocol.GapRetryMs, out ulong from, out ulong to))
        {
            INet.Net.Send(SubEditorProtocol.Channel, w => { SubEditorProtocol.WriteClientHeader(w, ClientPacket.Resend, sessionToken); w.WriteUInt64(from); w.WriteUInt64(to); });
        }
        PollFiles(now);
        LiveView.Tick(state == LinkState.Live && Editing, editor?.Cam, now);
        if (state == LinkState.Live)
        {
            WireTracker.Flush();
            if (Editing) { LayerTracker.Flush(); }
            if (LayerTracker.NamesChanged && editor != null && editor.IsOpen) { LayerTracker.NamesChanged = false; editor.RebuildLayers(); }
        }
        if ((state == LinkState.Live || editor is { IsOpen: true }) && !global::Barotrauma.GUI.InputBlockingMenuOpen && !DebugConsole.IsOpen &&
            PlayerInput.KeyHit(Keys.F4) && global::Barotrauma.GUI.KeyboardDispatcher.Subscriber == null) { Toggle(); }
    }

    private void Toggle()
    {
        editor ??= new LiveEditorScreen();
        if (editor.IsOpen) { editor.Close(); return; }
        if (GameMain.Client.HasPermission(ClientPermissions.ConsoleCommands) || GameMain.Client.IsServerOwner) { editor.Open(); }
        else { global::Barotrauma.GUI.AddMessage("Для редактора нужно право на консольные команды.", Microsoft.Xna.Framework.Color.Orange); }
    }

    // ---------------------------------------------------------------------------------------------- requests

    /// <summary>Sends an edit. rollback restores the local optimistic change if the server refuses it.</summary>
    internal static bool SendRequest(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload, Action rollback, Action onAccepted = null, string description = null, bool replaceLocal = false)
    {
        SubEditorClient c = instance;
        if (c == null || c.state != LinkState.Live || !c.compatible || c.pendingRequests.Count >= SubEditorProtocol.MaxPendingRequests ||
            ids.Length > OpFrame.MaxTouched || !OpFrame.AreStrictlyAscending(ids) || payload.Length > SubEditorProtocol.MaxFrameBytes) { return false; }
        uint id = c.nextRequest++;
        c.pendingRequests.Add(id, NowMs);
        if (rollback != null) { c.rollbacks[id] = rollback; }
        if (onAccepted != null) { c.accepted[id] = onAccepted; }
        if (replaceLocal && onAccepted != null) { c.replacements.Add(id); }
        ulong baseRev = c.replica.Applied;
        ushort[] copy = ids.ToArray(); byte[] body = payload.ToArray();
        bool sent = INet.Net.Send(SubEditorProtocol.Channel, w =>
        {
            SubEditorProtocol.WriteClientHeader(w, ClientPacket.Request, c.sessionToken); w.WriteUInt32(id); w.WriteUInt32(c.joinNumber); w.WriteByte((byte)kind); w.WriteUInt64(baseRev);
            w.WriteVariableUInt32((uint)copy.Length);
            foreach (ushort e in copy) { w.WriteUInt16(e); }
            SubEditorProtocol.WriteBlob(w, body);
        });
        ModLog.Message($"subeditor: sent {kind} request {id} ({copy.Length} ids, {body.Length} bytes) at revision {baseRev}: {(sent ? "ok" : "not sent")}");
        if (sent) { LiveHistory.Push(id, description ?? LiveHistory.Describe(kind)); }
        else { c.pendingRequests.Remove(id); c.rollbacks.Remove(id); c.accepted.Remove(id); c.replacements.Remove(id); }
        return sent;
    }

    internal static void SendHistory(bool undo)
    {
        SubEditorClient c = instance;
        if (c == null || c.state != LinkState.Live || !c.compatible || c.pendingRequests.Count >= SubEditorProtocol.MaxPendingRequests) { return; }
        uint id = c.nextRequest++;
        c.pendingRequests.Add(id, NowMs);
        c.accepted[id] = () => LiveHistory.Moved(undo);
        if (!INet.Net.Send(SubEditorProtocol.Channel, w => { SubEditorProtocol.WriteClientHeader(w, undo ? ClientPacket.Undo : ClientPacket.Redo, c.sessionToken); w.WriteUInt32(id); w.WriteUInt32(c.joinNumber); }))
        { c.pendingRequests.Remove(id); c.accepted.Remove(id); }
    }
}
