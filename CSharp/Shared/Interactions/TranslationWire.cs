using System;
using System.Collections.Generic;
using System.Buffers;
using Microsoft.Xna.Framework;
using PrimaryPackage.Networking;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

// Only submarine-local translation. Geometry, links and properties have other contracts.
internal static class TranslationWire {
    internal readonly record struct IntentRow(Guid Token, ulong Revision, string Before, string After);
    internal readonly record struct StateRow(Guid Token, ushort NativeFrame, string Position);
    internal static void Validate(Guid request, ReadOnlySpan<IntentRow> rows) {
        if (request == Guid.Empty || rows.Length is < 1 or > WorldEditJournal.MaxBatch) { throw new ArgumentException("Недопустимое выделение для перемещения"); }
        var tokens = new HashSet<Guid>(rows.Length);
        long dx = 0, dy = 0;
        for (int i = 0; i < rows.Length; i++) {
            var row = rows[i];
            if (row.Token == Guid.Empty || !tokens.Add(row.Token)) { throw new ArgumentException("Повторная или пустая цель перемещения"); }
            var before = LiveItemTransform.Decode(row.Before); var after = LiveItemTransform.Decode(row.After);
            long x = (long)after.Position.X - before.Position.X, y = (long)after.Position.Y - before.Position.Y;
            if (before.Frame != after.Frame || x == 0 && y == 0) { throw new ArgumentException("Изменена система координат или отсутствует перемещение"); }
            if (i == 0) { dx = x; dy = y; }
            else if (dx != x || dy != y) { throw new ArgumentException("Группа должна перемещаться одним вектором"); }
        }
    }
    internal static void WriteIntent(IWriteMessage output, Guid request, IntentRow[] rows) {
        Validate(request, rows);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(NativeWire.MaxFrameBytes);
        try {
            var writer = new BitWriter(buffer.AsSpan(0, NativeWire.MaxFrameBytes));
            writer.Guid(request); writer.Bits((ulong)(rows.Length - 1), 6);
            var first = LiveItemTransform.Decode(rows[0].Before); var last = LiveItemTransform.Decode(rows[0].After);
            writer.Int(last.Position.X - first.Position.X); writer.Int(last.Position.Y - first.Position.Y);
            foreach (var row in rows) {
                var before = LiveItemTransform.Decode(row.Before);
                writer.Guid(row.Token); writer.UInt(row.Revision); writer.Guid(before.Frame); writer.Int(before.Position.X); writer.Int(before.Position.Y);
            }
            writer.Align(); NativeWire.Write(output, buffer, writer.LengthBytes);
        } finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    internal static IntentRow[] ReadIntent(IReadMessage input, out Guid request) {
        var reader = new BitReader(NativeWire.Read(input));
        request = reader.Guid(); int count = (int)reader.Bits(6) + 1;
        int dx = reader.Int(), dy = reader.Int();
        var rows = new IntentRow[count];
        for (int i = 0; i < count; i++) {
            Guid token = reader.Guid(); ulong revision = reader.UInt(); Guid frame = reader.Guid(); int x = reader.Int(), y = reader.Int();
            long afterX = (long)x + dx, afterY = (long)y + dy;
            if (Math.Abs((long)x) > 10000000 || Math.Abs((long)y) > 10000000 || Math.Abs(afterX) > 10000000 || Math.Abs(afterY) > 10000000) { throw new ArgumentException("Wire position outside world limits"); }
            rows[i] = new(token, revision, LiveItemTransform.Encode(frame, new Point(x, y)), LiveItemTransform.Encode(frame, new Point((int)afterX, (int)afterY)));
        }
        reader.Finish(); Validate(request, rows); return rows;
    }
    internal static void WriteState(IWriteMessage output, Guid request, ulong revision, StateRow[] rows) {
        ValidateState(request, revision, rows);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(NativeWire.MaxFrameBytes);
        try {
            var writer = new BitWriter(buffer.AsSpan(0, NativeWire.MaxFrameBytes));
            writer.Guid(request); writer.UInt(revision); writer.Bits((ulong)(rows.Length - 1), 6); writer.UInt(rows[0].NativeFrame);
            foreach (var row in rows) {
                var state = LiveItemTransform.Decode(row.Position);
                writer.Guid(row.Token); writer.Guid(state.Frame); writer.Int(state.Position.X); writer.Int(state.Position.Y);
            }
            writer.Align(); NativeWire.Write(output, buffer, writer.LengthBytes);
        } finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    internal static StateRow[] ReadState(IReadMessage input, out Guid request, out ulong revision) {
        var reader = new BitReader(NativeWire.Read(input));
        request = reader.Guid(); revision = reader.UInt(); int count = (int)reader.Bits(6) + 1;
        uint nativeFrame = reader.UInt32(); if (nativeFrame is 0 or > ushort.MaxValue) { throw new ArgumentException("Invalid world frame ID"); }
        var rows = new StateRow[count];
        for (int i = 0; i < count; i++) {
            Guid token = reader.Guid(), frame = reader.Guid(); int x = reader.Int(), y = reader.Int();
            rows[i] = new(token, (ushort)nativeFrame, LiveItemTransform.Encode(frame, new Point(x, y)));
        }
        reader.Finish(); ValidateState(request, revision, rows); return rows;
    }
    private static void ValidateState(Guid request, ulong revision, StateRow[] rows) {
        if (request == Guid.Empty || revision == 0 || rows.Length is < 1 or > WorldEditJournal.MaxBatch) { throw new ArgumentException("Некорректное подтверждение перемещения"); }
        var tokens = new HashSet<Guid>(rows.Length);
        foreach (var row in rows) {
            if (row.Token == Guid.Empty || row.NativeFrame == 0 || row.NativeFrame != rows[0].NativeFrame || !tokens.Add(row.Token)) { throw new ArgumentException("Некорректная цель подтверждения"); }
            LiveItemTransform.Decode(row.Position);
        }
    }
}
