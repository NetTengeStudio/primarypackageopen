using System;
using System.Buffers;
using System.Collections.Generic;
using PrimaryPackage.Networking;

namespace PrimaryPackage.Interactions;

internal readonly record struct BaselineGraph(ushort Submarine, byte[] Digest);
internal readonly record struct BaselineProperty(ushort Component, string Type, string Name, string Value, ulong Revision);
internal sealed record BaselineItem(Guid Incarnation, ushort NativeId, string Prefab, ushort Submarine,
    bool HasPosition, Guid Frame, int X, int Y, ulong PositionRevision, BaselineProperty[] Properties);
internal sealed record WorldBaseline(Guid Epoch, ulong Cut, BaselineGraph[] Graphs, BaselineItem[] Items) {
    internal const int MaxProperties = 4096, MaxItems = 1024, MaxEncodedBytes = 4 * 1024 * 1024;
    internal byte[] Encode() {
        int estimate = 128 + Graphs.Length * 40, fields = 0;
        foreach (var item in Items) {
            estimate = checked(estimate + 128 + 3 * item.Prefab.Length);
            foreach (var property in item.Properties) { estimate = checked(estimate + 40 + 3 * (property.Type.Length + property.Name.Length + property.Value.Length)); fields++; }
        }
        if (Epoch == Guid.Empty || Graphs.Length > 255 || Items.Length > MaxItems || fields > MaxProperties || estimate > MaxEncodedBytes) { throw new ArgumentException("World baseline exceeds bounded schema"); }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(estimate);
        try {
            var writer = new BitWriter(buffer.AsSpan(0, estimate)); writer.UInt(1); writer.Guid(Epoch); writer.UInt(Cut); writer.UInt((uint)Graphs.Length);
            foreach (var graph in Graphs) { if (graph.Submarine == 0 || graph.Digest.Length != 32) { throw new ArgumentException("Invalid baseline graph digest"); } writer.UInt(graph.Submarine); writer.Bytes(graph.Digest); }
            writer.UInt((uint)Items.Length);
            foreach (var item in Items) {
                writer.Guid(item.Incarnation); writer.UInt(item.NativeId); writer.Text(item.Prefab, 512); writer.UInt(item.Submarine); writer.Bits(item.HasPosition ? 1UL : 0, 1);
                if (item.HasPosition) { writer.Guid(item.Frame); writer.Int(item.X); writer.Int(item.Y); writer.UInt(item.PositionRevision); }
                writer.UInt((uint)item.Properties.Length);
                foreach (var property in item.Properties) { writer.UInt(property.Component); writer.Text(property.Type, 640); writer.Text(property.Name, 512); writer.Text(property.Value, 2048); writer.UInt(property.Revision); }
            }
            writer.Align(); return buffer.AsSpan(0, writer.LengthBytes).ToArray();
        } finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    internal static WorldBaseline Decode(ReadOnlySpan<byte> data) {
        if (data.Length > MaxEncodedBytes) { throw new ArgumentException("World baseline is too large"); }
        var reader = new BitReader(data);
        if (reader.UInt() != 1) { throw new ArgumentException("Unknown baseline schema"); }
        Guid epoch = reader.Guid(); if (epoch == Guid.Empty) { throw new ArgumentException("Empty world epoch"); }
        ulong cut = reader.UInt(); uint graphCount = reader.UInt32(); if (graphCount > 255) { throw new ArgumentException("Too many base submarines"); }
        var graphs = new BaselineGraph[(int)graphCount]; var nativeSubs = new HashSet<ushort>();
        for (int i = 0; i < graphs.Length; i++) { ushort id = UShort(ref reader); if (id == 0 || !nativeSubs.Add(id)) { throw new ArgumentException("Duplicate baseline submarine"); } graphs[i] = new(id, reader.Bytes(32).ToArray()); }
        uint itemCount = reader.UInt32(); if (itemCount > MaxItems) { throw new ArgumentException("Too many baseline items"); }
        var items = new BaselineItem[(int)itemCount]; var ids = new HashSet<ushort>(); var incarnations = new HashSet<Guid>(); int total = 0;
        for (int i = 0; i < items.Length; i++) {
            Guid incarnation = reader.Guid(); ushort id = UShort(ref reader); string prefab = reader.Text(512); ushort sub = UShort(ref reader); bool position = reader.Bits(1) != 0;
            if (id == 0 || incarnation == Guid.Empty || !ids.Add(id) || !incarnations.Add(incarnation) || prefab.Length is < 1 or > 128) { throw new ArgumentException("Invalid baseline item identity"); }
            Guid frame = position ? reader.Guid() : Guid.Empty; int x = 0, y = 0; ulong revision = 0;
            if (position) { x = reader.Int(); y = reader.Int(); revision = reader.UInt(); if (frame == Guid.Empty || sub == 0 || Math.Abs((long)x) > 10000000 || Math.Abs((long)y) > 10000000 || revision > cut) { throw new ArgumentException("Invalid baseline position"); } }
            uint count = reader.UInt32(); if (count > 512 || count > MaxProperties - total) { throw new ArgumentException("Too many baseline properties"); } total += (int)count;
            var properties = new BaselineProperty[(int)count]; var keys = new HashSet<(ushort, string)>(properties.Length);
            for (int p = 0; p < properties.Length; p++) {
                ushort component = UShort(ref reader); string type = reader.Text(640), name = reader.Text(512), value = reader.Text(2048); ulong propertyRevision = reader.UInt();
                if (component == ushort.MaxValue || type.Length is < 1 or > 160 || name.Length is < 1 or > 128 || value.Length > 512 || propertyRevision > cut || !keys.Add((component, name))) { throw new ArgumentException("Invalid baseline property contract"); }
                properties[p] = new(component, type, name, value, propertyRevision);
            }
            items[i] = new(incarnation, id, prefab, sub, position, frame, x, y, revision, properties);
        }
        reader.Finish(); return new(epoch, cut, graphs, items);
    }
    private static ushort UShort(ref BitReader reader) { uint value = reader.UInt32(); return value <= ushort.MaxValue ? (ushort)value : throw new ArgumentException("Native identity overflow"); }
}
