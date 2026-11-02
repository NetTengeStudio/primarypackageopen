using System;
using System.Buffers.Binary;

namespace PrimaryPackage.Networking;

internal enum ChunkPacket : byte { Offer = 1, Accept, Data, Ack, Complete, Cancel }

internal static class ChunkWire {
    internal const int MaxHeaderBytes = 64, MaxPacketBytes = WorldBlob.ChunkBytes + MaxHeaderBytes;
    private const ushort Magic = 0x5750;
    internal static BitWriter Start(Span<byte> buffer, ChunkPacket kind, Guid transfer) {
        if (kind is < ChunkPacket.Offer or > ChunkPacket.Cancel || transfer == Guid.Empty) { throw new ArgumentException("Invalid snapshot packet header"); }
        var writer = new BitWriter(buffer); writer.Bits(Magic, 16); writer.Bits(1, 3); writer.Bits((ulong)kind, 3); writer.Bits(0, 2); writer.Guid(transfer); return writer;
    }
    internal static int Finish(ref BitWriter writer, Span<byte> buffer) {
        writer.Align(); int size = writer.LengthBytes;
        if (size > buffer.Length - 4) { throw new ArgumentException("Snapshot packet exceeds buffer"); }
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[size..], Crc32C.Compute(buffer[..size])); return size + 4;
    }
    internal static BitReader Open(ReadOnlySpan<byte> packet, out ChunkPacket kind, out Guid transfer) {
        if (!TryOpen(packet, out var reader, out kind, out transfer)) { throw new ArgumentException("Invalid snapshot packet checksum/header"); }
        return reader;
    }
    internal static bool TryOpen(ReadOnlySpan<byte> packet, out BitReader reader, out ChunkPacket kind, out Guid transfer) {
        reader = default; kind = default; transfer = default;
        if (packet.Length is < 23 or > MaxPacketBytes || BinaryPrimitives.ReadUInt16LittleEndian(packet) != Magic ||
            (packet[2] & 7) != 1 || (packet[2] >> 6) != 0 || Crc32C.Compute(packet[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(packet[^4..])) { return false; }
        kind = (ChunkPacket)((packet[2] >> 3) & 7);
        if (kind is < ChunkPacket.Offer or > ChunkPacket.Cancel) { return false; }
        transfer = new Guid(packet.Slice(3, 16)); if (transfer == Guid.Empty) { return false; }
        reader = new BitReader(packet[..^4]); reader.Bytes(19); return true;
    }
}

internal static class Crc32C {
    private static readonly uint[] table = CreateTable();
    private static uint[] CreateTable() {
        var result = new uint[256];
        for (uint i = 0; i < result.Length; i++) {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) { value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0x82F63B78u); }
            result[i] = value;
        }
        return result;
    }
    internal static uint Compute(ReadOnlySpan<byte> bytes) {
        uint value = uint.MaxValue;
        foreach (byte b in bytes) { value = table[(byte)(value ^ b)] ^ (value >> 8); }
        return ~value;
    }
}
