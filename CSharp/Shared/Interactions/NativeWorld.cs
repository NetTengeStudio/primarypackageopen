using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using Barotrauma;
using HarmonyLib;
using PrimaryPackage.Networking;

namespace PrimaryPackage.Interactions;

// Capture immutable construction-time graphs, not the simulation state at join time.
// This detects cached/mismatched base geometry before native events can reference it.
internal static class NativeWorld {
    private sealed record Graph(byte[] Digest, HashSet<MapEntity> Entities);
    private static readonly Dictionary<Submarine, Graph> graphs = new();
    internal static void Attach(Harmony harmony) {
        int count = 0;
        foreach (var constructor in AccessTools.GetDeclaredConstructors(typeof(Submarine))) {
            if (constructor.GetParameters().Length == 4) { harmony.Patch(constructor, postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(NativeWorld), nameof(Constructed))); count++; }
        }
        if (count != 1) { throw new InvalidOperationException("Unsupported native submarine construction lifecycle"); }
    }
    internal static void Prune() {
        var removed = new List<Submarine>(); foreach (var sub in graphs.Keys) { if (sub.Removed) { removed.Add(sub); } }
        foreach (var sub in removed) { graphs.Remove(sub); }
    }
    internal static void Clear() => graphs.Clear();
    private static void Constructed(Submarine __instance) {
        var entities = new List<MapEntity>();
        foreach (var entity in MapEntity.MapEntityList) { if (!entity.Removed && entity.Submarine == __instance) { entities.Add(entity); } }
        entities.Sort(static (a, b) => a.ID.CompareTo(b.ID));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[1024];
        foreach (var entity in entities) {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, entity.ID);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[2..], entity.Rect.X - (int)__instance.HiddenSubPosition.X);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[6..], entity.Rect.Y - (int)__instance.HiddenSubPosition.Y);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[10..], entity.Rect.Width); BinaryPrimitives.WriteInt32LittleEndian(buffer[14..], entity.Rect.Height);
            BinaryPrimitives.WriteSingleLittleEndian(buffer[18..], entity.RotationRad); BinaryPrimitives.WriteSingleLittleEndian(buffer[22..], entity.Scale);
            buffer[26] = (byte)((entity.FlippedX ? 1 : 0) | (entity.FlippedY ? 2 : 0));
            hash.AppendData(buffer[..27]); Append(hash, entity.GetType().FullName, buffer); Append(hash, entity.Prefab?.Identifier.Value ?? "", buffer);
            if (entity is Item item) { foreach (var component in item.Components) { Append(hash, component.GetType().FullName, buffer); } }
            buffer[0] = 0; hash.AppendData(buffer[..1]);
        }
        graphs[__instance] = new(hash.GetHashAndReset(), new HashSet<MapEntity>(entities));
    }
    private static void Append(IncrementalHash hash, string value, Span<byte> buffer) {
        int count = WireEncoding.Utf8.GetByteCount(value);
        if (count > buffer.Length - 4) { throw new ArgumentException("Native graph identifier is too long"); }
        BinaryPrimitives.WriteInt32LittleEndian(buffer, count); WireEncoding.Utf8.GetBytes(value.AsSpan(), buffer[4..]); hash.AppendData(buffer[..(count + 4)]);
    }
    internal static bool IsConstructionEntity(MapEntity entity) => entity.Submarine != null && graphs.TryGetValue(entity.Submarine, out var graph) && graph.Entities.Contains(entity);
    internal static BaselineGraph[] Capture() {
        Prune(); var result = new BaselineGraph[graphs.Count]; int index = 0;
        foreach (var pair in graphs) { result[index++] = new(pair.Key.ID, pair.Value.Digest); }
        return result;
    }
    internal static void Validate(BaselineGraph[] expected) {
        foreach (var graph in expected) {
            if (Entity.FindEntityByID(graph.Submarine) is not Submarine sub || !graphs.TryGetValue(sub, out var actual) || !CryptographicOperations.FixedTimeEquals(actual.Digest, graph.Digest)) {
                throw new InvalidOperationException("Базовая геометрия подлодки ID " + graph.Submarine + " отличается от сервера. Нативные события не запущены; проверьте версии файла подлодки и контента");
            }
        }
    }
}
