using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>Applies one decoded operation frame to the live world. Used verbatim by the server and by every client.</summary>
public static class FrameApplier
{
    public const int MaxTouchedForApply = OpFrame.MaxTouched;

    /// <summary>False when the kind is not implemented or the payload does not match its header.</summary>
    public static bool Apply(OpFrame.View v)
    {
        if (v.Count > MaxTouchedForApply) { return false; }
        Span<ushort> ids = stackalloc ushort[v.Count];
        v.ReadIds(ids);
        return Apply(v.Kind, ids, v.Payload);
    }

    public static bool ApplyTransaction(OpFrame.View view)
    {
        if (view.Count > MaxTouchedForApply) { return false; }
        Span<ushort> ids = stackalloc ushort[view.Count]; view.ReadIds(ids);
        return ApplyTransaction(view.Kind, ids, view.Payload);
    }

    public static bool ApplyTransaction(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload)
    {
        if (kind == OpKind.Batch) { return ApplyBatch(payload); }
        if (kind is OpKind.SpawnSub or OpKind.DeleteSub or OpKind.Circuit) { return Apply(kind, ids, payload); }
        return ApplyTransaction(kind, ids, payload, out _);
    }

    private static bool ApplyTransaction(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload, out BatchPayload.Sub inverse)
    {
        using var geometry = LiveGeometry.Enter();
        using var world = WorldContext.Enter();
        if (!CaptureInverse(kind, ids, payload, out inverse)) { return false; }
        bool committed = false;
        try { committed = Apply(kind, ids, payload); return committed; }
        finally
        {
            if (!committed) { Restore(inverse); }
        }
    }

    internal static void Restore(BatchPayload.Sub inverse)
    {
        if (inverse.Kind == OpKind.Spawn) { EntityOps.Delete(inverse.Ids); }
        if (!Apply(inverse.Kind, inverse.Ids, inverse.Payload)) { ModLog.Error("subeditor: world transaction rollback failed"); }
    }

    internal static bool CaptureInverse(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload, out BatchPayload.Sub inverse)
    {
        inverse = default;
        var writer = new ByteWriter(128);
        ushort[] touched = ids.ToArray();
        OpKind undoKind = kind;
        try
        {
            switch (kind)
            {
                case OpKind.Transform:
                    var transforms = new TransformRecord[ids.Length];
                    if (!EntityOps.CaptureTransform(ids, transforms)) { return false; }
                    TransformPayload.Write(ref writer, transforms);
                    break;
                case OpKind.Property:
                    var components = new short[ids.Length]; var values = new string[ids.Length];
                    if (!PropertyPayload.TryRead(payload, ids.Length, out string name, components, values) || !EntityOps.CaptureProperty(ids, components, name, values)) { return false; }
                    PropertyPayload.Write(ref writer, name, components, values);
                    break;
                case OpKind.State:
                    var states = new byte[ids.Length][];
                    if (!EntityOps.CaptureStates(ids, states)) { return false; }
                    StatePayload.Write(ref writer, states);
                    break;
                case OpKind.Inventory:
                    var placements = new InventoryPlacement[ids.Length];
                    for (int i = 0; i < ids.Length; i++) { if (EntityOps.Find(ids[i]) is not Item item || !InventoryOps.Capture(item, out placements[i])) { return false; } }
                    InventoryPayload.Write(ref writer, placements);
                    break;
                case OpKind.Flip:
                    var flips = new FlipRecord[ids.Length];
                    if (!EntityOps.CaptureFlip(ids, flips)) { return false; }
                    FlipPayload.Write(ref writer, flips);
                    break;
                case OpKind.Wire:
                    if (!WirePayload.TryRead(payload, out WireState[] wires)) { return false; }
                    for (int i = 0; i < wires.Length; i++) { if (!EntityOps.CaptureWire(wires[i].WireId, out wires[i])) { return false; } }
                    WirePayload.Write(ref writer, wires);
                    break;
                case OpKind.Link:
                    if (!LinkPayload.TryRead(payload, Math.Max(ids.Length * (ids.Length - 1) / 2, ids.Length * 4), out var links)) { return false; }
                    for (int i = 0; i < links.Length; i++)
                    {
                        var link = links[i];
                        if (EntityOps.Find(link.A) is not MapEntity a || EntityOps.Find(link.B) is not MapEntity b) { return false; }
                        links[i] = (link.A, link.B, a.linkedTo.Contains(b));
                    }
                    LinkPayload.Write(ref writer, links);
                    break;
                case OpKind.SubMeta:
                    if (ids.Length != 1 || SubRegistry.Find(ids[0]) is not Submarine submarine || !MetaPayload.TryRead(payload, out MetaPayload.Pair[] pairs)) { return false; }
                    MetaPayload.Write(ref writer, SubMeta.CaptureKeysFrom(SubMeta.Capture(submarine.Info), pairs));
                    break;
                case OpKind.Delete:
                    List<MapEntity> all = EntityOps.WithContents(ids);
                    if (all.Count == 0)
                    {
                        touched = Array.Empty<ushort>(); undoKind = OpKind.State;
                        StatePayload.Write(ref writer, Array.Empty<byte[]>());
                        break;
                    }
                    touched = all.Select(entity => entity.ID).ToArray();
                    SpawnPayload.Write(ref writer, SubRegistry.WireId(all[0].Submarine), EntityOps.SaveXml(all));
                    undoKind = OpKind.Spawn;
                    break;
                case OpKind.Spawn:
                    if (!SpawnPayload.TryRead(payload, out _, out byte[] xml) || EntityOps.ElementIds(xml) is not int[] created || created.Length == 0 ||
                        created.Any(id => id <= 0 || id >= ushort.MaxValue || Entity.FindEntityByID((ushort)id) != null)) { return false; }
                    touched = created.Select(id => (ushort)id).OrderBy(id => id).ToArray();
                    undoKind = OpKind.Delete;
                    break;
                default: return false;
            }
            inverse = new BatchPayload.Sub(undoKind, touched, writer.Written.ToArray());
            return true;
        }
        finally { writer.Dispose(); }
    }

    /// <summary>Direct apply path: applies kind, touched ids and payload without re-encoding to an OpFrame.</summary>
    public static bool Apply(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload)
    {
        using var geometry = LiveGeometry.Enter();
        using var world = WorldContext.Enter();
        switch (kind)
        {
            case OpKind.Transform:
            {
                var records = new TransformRecord[ids.Length];
                if (!TransformPayload.TryRead(payload, ids.Length, records)) { return false; }
                return EntityOps.ApplyTransform(ids, records);
            }
            case OpKind.Property:
            {
                var comps = new short[ids.Length]; var values = new string[ids.Length];
                if (!PropertyPayload.TryRead(payload, ids.Length, out string name, comps, values)) { return false; }
                return EntityOps.ApplyProperty(ids, comps, name, values);
            }
            case OpKind.Delete:
                if (payload.Length != 0) { return false; }
                EntityOps.Delete(ids);
                return true;
            case OpKind.Spawn:
                return ApplySpawn(payload);
            case OpKind.Link:
                return ApplyLink(payload, ids);
            case OpKind.State:
            {
                if (!EntityOps.AllExist(ids) || !StatePayload.TryRead(payload, ids.Length, out byte[][] blobs)) { return false; }
                return EntityOps.RestoreStates(ids, blobs);
            }
            case OpKind.Inventory:
            {
                if (!InventoryPayload.TryRead(payload, ids.Length, out InventoryPlacement[] placements)) { return false; }
                return InventoryOps.Apply(ids, placements);
            }
            case OpKind.Wire:
            {
                if (!WirePayload.TryRead(payload, out WireState[] wires)) { return false; }
                return EntityOps.ApplyWires(wires);
            }
            case OpKind.Batch:
                return ApplyBatch(payload);
            case OpKind.Flip:
            {
                var records = new FlipRecord[ids.Length];
                if (!FlipPayload.TryRead(payload, ids.Length, records)) { return false; }
                return EntityOps.ApplyFlip(ids, records);
            }
            case OpKind.SubMeta:
            {
                if (ids.Length != 1 || !MetaPayload.TryRead(payload, out MetaPayload.Pair[] pairs) || SubRegistry.Find(ids[0]) is not Submarine sub) { return false; }
                SubMeta.Apply(sub.Info, pairs);
                return true;
            }
            case OpKind.SpawnSub:
                return SubSpawnPayload.TryRead(payload, out SubSpawn spawn) && SubRegistry.Load(spawn) != null;
            case OpKind.DeleteSub:
                return ids.Length == 1 && payload.Length == 0 && SubRegistry.Unload(ids[0]);
            case OpKind.Circuit:
                return ids.Length == 1 && CircuitOps.Apply(ids[0], payload);
            default:
                return false;
        }
    }

    /// <summary>Sub-operations are applied directly without re-encoding to an OpFrame.</summary>
    private static bool ApplyBatch(ReadOnlySpan<byte> payload)
    {
        if (!BatchPayload.TryRead(payload, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] subs)) { return false; }
        var undo = new List<BatchPayload.Sub>(subs.Length);
        bool committed = false;
        try
        {
            foreach (BatchPayload.Sub sub in subs)
            {
                if (!ApplyTransaction(sub.Kind, sub.Ids, sub.Payload, out BatchPayload.Sub inverse)) { return false; }
                undo.Add(inverse);
            }
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    BatchPayload.Sub inverse = undo[i];
                    Restore(inverse);
                }
            }
        }
    }

    private static bool ApplySpawn(ReadOnlySpan<byte> payload)
    {
        if (!SpawnPayload.TryRead(payload, out ushort subId, out byte[] xml)) { return false; }
        Submarine sub = SubRegistry.Find(subId);
        if (sub == null && subId != SubRegistry.WorldId) { return false; }
        return EntityOps.LoadExact(xml, sub) != null;
    }

    private static bool ApplyLink(ReadOnlySpan<byte> payload, ReadOnlySpan<ushort> touched)
    {
        int maxPairs = Math.Max(touched.Length * (touched.Length - 1) / 2, touched.Length * 4);
        if (!EntityOps.AllExist(touched) || !LinkPayload.TryRead(payload, maxPairs, out var pairs)) { return false; }
        foreach (var (a, b, linked) in pairs) { EntityOps.SetLink(a, b, linked); }
        return true;
    }
}
