using System;
using System.Collections.Generic;

namespace PrimaryPackage.SubEditor;

public delegate bool OperationHandler(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload);

public enum SubmitStatus : byte { Accepted, Conflict, Malformed, NothingToUndo, NothingToRedo }

public readonly record struct SubmitResult(SubmitStatus Status, ulong Rev, int ConflictId);

/// <summary>
/// Server-authoritative revision counter, optimistic per-entity conflict check, replay ring and per-actor Undo/Redo.
/// Payload bytes are opaque here: the core only needs the touched entity ids to order and validate operations,
/// which keeps one pipeline for every operation kind (registry K1) instead of one protocol per feature.
/// Single-threaded: call from the game thread only.
/// </summary>
public sealed class Authority
{
    public const int MaxUndoDepth = 256;
    public const int MaxHistoryBytesPerActor = 4 << 20;

    private sealed class Entry
    {
        public ulong Rev; public OpKind UndoKind, RedoKind; public ushort[] Ids; public byte[] Undo; public byte[] Redo;
        public int Bytes => Ids.Length * 2 + Undo.Length + Redo.Length;
    }

    private sealed class History
    {
        public readonly List<Entry> Undo = new();
        public readonly List<Entry> Redo = new();
        public int Bytes;
    }

    private readonly OpRing ring;
    private readonly EntityVersions versions;
    private readonly History[] histories = new History[256];
    private byte[] scratch = new byte[4096];

    public Authority(OpRing ring, EntityVersions versions) { this.ring = ring; this.versions = versions; }

    public ulong Rev { get; private set; }
    public ushort NativeEventId { get; set; }
    public OpRing Ring => ring;
    public EntityVersions Versions => versions;

    /// <summary>The frame of the most recently accepted op; valid until the next Submit.</summary>
    public bool TryGetFrame(ulong rev, out ReadOnlySpan<byte> frame) => ring.TryGet(rev, out frame);

    /// <param name="baseRev">Newest revision the actor had applied when it composed the op.</param>
    /// <param name="redoPayload">Payload broadcast to everyone (and replayed by Redo).</param>
    /// <param name="undoKind">Kind of the reverting op when it differs (Spawn is undone by Delete and back).</param>
    /// <param name="undoPayload">Payload that reverts it; stays on the server and is never broadcast.</param>
    public SubmitResult Submit(byte actor, OpKind kind, ulong baseRev, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> redoPayload, ReadOnlySpan<byte> undoPayload, bool recordHistory = true, OpKind? undoKind = null)
    {
        SubmitResult r = Commit(actor, kind, baseRev, ids, redoPayload);
        if (r.Status != SubmitStatus.Accepted || !recordHistory) { return r; }
        History h = histories[actor] ??= new History();
        foreach (Entry dropped in h.Redo) { h.Bytes -= dropped.Bytes; }
        h.Redo.Clear();
        Push(h, new Entry { Rev = r.Rev, RedoKind = kind, UndoKind = undoKind ?? kind, Ids = ids.ToArray(), Undo = undoPayload.ToArray(), Redo = redoPayload.ToArray() });
        return r;
    }

    /// <summary>What the next undo (or redo) of this actor would apply, without taking it: lets the caller refuse a step the world can no longer accept.</summary>
    public bool TryPeek(byte actor, bool undo, out OpKind kind, out byte[] payload)
    {
        kind = default; payload = null;
        History h = histories[actor];
        List<Entry> from = h == null ? null : (undo ? h.Undo : h.Redo);
        if (from == null || from.Count == 0) { return false; }
        Entry e = from[^1];
        kind = undo ? e.UndoKind : e.RedoKind;
        payload = undo ? e.Undo : e.Redo;
        return true;
    }

    public SubmitResult Undo(byte actor, OperationHandler apply = null, bool applyIsAtomic = false) => Replay(actor, undo: true, apply, applyIsAtomic);
    public SubmitResult Redo(byte actor, OperationHandler apply = null, bool applyIsAtomic = false) => Replay(actor, undo: false, apply, applyIsAtomic);

    public int UndoDepth(byte actor) => histories[actor]?.Undo.Count ?? 0;
    public int RedoDepth(byte actor) => histories[actor]?.Redo.Count ?? 0;
    public int HistoryBytes(byte actor) => histories[actor]?.Bytes ?? 0;

    private SubmitResult Replay(byte actor, bool undo, OperationHandler apply, bool applyIsAtomic)
    {
        History h = histories[actor];
        List<Entry> from = h == null ? null : (undo ? h.Undo : h.Redo);
        if (from == null || from.Count == 0) { return new SubmitResult(undo ? SubmitStatus.NothingToUndo : SubmitStatus.NothingToRedo, Rev, -1); }
        Entry e = from[^1];
        bool attempted = false;
        SubmitResult r;
        try { r = Commit(actor, undo ? e.UndoKind : e.RedoKind, e.Rev, e.Ids, undo ? e.Undo : e.Redo, apply, out attempted); }
        catch
        {
            if (!applyIsAtomic && attempted && !apply(undo ? e.RedoKind : e.UndoKind, e.Ids, undo ? e.Redo : e.Undo)) { throw new InvalidOperationException("History rollback failed"); }
            throw;
        }
        if (!applyIsAtomic && r.Status != SubmitStatus.Accepted && attempted && !apply(undo ? e.RedoKind : e.UndoKind, e.Ids, undo ? e.Redo : e.Undo))
        { throw new InvalidOperationException("History rollback failed"); }
        if (r.Status != SubmitStatus.Accepted) { return r; }
        from.RemoveAt(from.Count - 1);
        e.Rev = r.Rev;
        (undo ? h.Redo : h.Undo).Add(e);
        return r;
    }

    private void Push(History h, Entry e)
    {
        h.Undo.Add(e); h.Bytes += e.Bytes;
        while (h.Undo.Count > MaxUndoDepth || (h.Bytes > MaxHistoryBytesPerActor && h.Undo.Count > 1))
        { h.Bytes -= h.Undo[0].Bytes; h.Undo.RemoveAt(0); }
    }

    private SubmitResult Commit(byte actor, OpKind kind, ulong baseRev, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload)
        => Commit(actor, kind, baseRev, ids, payload, null, out _);

    private SubmitResult Commit(byte actor, OpKind kind, ulong baseRev, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload, OperationHandler apply, out bool attempted)
    {
        attempted = false;
        if (ids.Length == 0 || ids.Length > OpFrame.MaxTouched || payload.Length > OpFrame.MaxPayload || baseRev > Rev || !OpFrame.AreStrictlyAscending(ids))
        { return new SubmitResult(SubmitStatus.Malformed, Rev, -1); }
        int conflict = versions.FirstConflict(ids, actor, baseRev);
        if (conflict >= 0) { return new SubmitResult(SubmitStatus.Conflict, Rev, conflict); }
        int need = OpFrame.MaxEncodedSize(ids.Length, payload.Length);
        if (scratch.Length < need) { scratch = new byte[Math.Max(need, scratch.Length * 2)]; }
        ulong rev = Rev + 1;
        int n = OpFrame.Write(scratch, (OpKind)kind, actor, rev, baseRev, ids, payload, NativeEventId);
        if (!ring.CanAppend(rev, n)) { return new SubmitResult(SubmitStatus.Malformed, Rev, -1); }
        if (apply != null)
        {
            attempted = true;
            if (!apply(kind, ids, payload)) { return new SubmitResult(SubmitStatus.Malformed, Rev, -1); }
        }
        if (!ring.Append(rev, scratch.AsSpan(0, n))) { return new SubmitResult(SubmitStatus.Malformed, Rev, -1); }
        Rev = rev;
        versions.Stamp(ids, actor, rev);
        return new SubmitResult(SubmitStatus.Accepted, rev, -1);
    }

    /// <summary>Result of a resend request: frames come from the ring or the client must take a snapshot instead.</summary>
    public bool CanResend(ulong fromRev, ulong toRev) => fromRev <= toRev && toRev <= Rev && ring.Count > 0 && fromRev >= ring.OldestRev;
}
