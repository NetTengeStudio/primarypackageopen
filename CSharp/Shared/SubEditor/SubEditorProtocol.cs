using System;
using System.Buffers;
using Barotrauma.Networking;

namespace PrimaryPackage.SubEditor;

public enum ClientPacket : byte { Hello = 1, Request = 2, SnapAck = 3, Resend = 4, Undo = 5, Redo = 6, Bye = 7, Missing = 9, Cursor = 10, SnapshotReady = 11, Applied = 12 }

public enum ServerPacket : byte { SnapChunk = 1, Op = 2, Ack = 3, Reject = 4, Denied = 5, Restart = 6, Notice = 7, Presence = 8, Welcome = 9, Incompatible = 10 }

/// <summary>Flags of a cursor report: the editor was closed (the cursor disappears for the others).</summary>
public static class PresenceFlags
{
    public const byte Closed = 1;
    public const long SendIntervalMs = 100, ExpireMs = 3000;
}

/// <summary>Chunk kinds inside a ServerPacket.SnapChunk. The completion marker travels through the same window as data.</summary>
public enum ChunkKind : byte { Data = 0, DataBrotli = 1, Done = 2, Subs = 3, Circuit = 4 }

public enum EntityState : byte { Gone = 0, Full = 1, InPlace = 2, Meta = 3 }

public static class SubEditorProtocol
{
    public const string Channel = "pp.subeditor.v5";
    public const ushort Version = 7;
    public const byte ServerMarker = 230;
    public const int MaxPendingRequests = 128;
    public const int MaxFrameBytes = 48 * 1024;
    public const int MaxChunkBytes = 24 * 1024;
    public const int BatchEntities = 64;
    public const int WindowChunks = 16;
    /// <summary>The transport underneath is reliable, so a retransmission here only guards against a lost peer state; the timeout follows the measured round trip up to MaxRtoMs.</summary>
    public const long BaseRtoMs = 1000, MaxRtoMs = 15000, StallMs = 90000;
    /// <summary>A joiner that hears nothing for this long starts the join again, doubling the wait up to the maximum; any chunk resets it.</summary>
    public const long JoinSilenceMs = 12000, JoinSilenceMaxMs = 90000;
    public const long GapRetryMs = 500;
    public const int MaxSnapshotBytes = 64 << 20;
    public const long AutosaveMs = 5 * 60 * 1000;

    public static void WriteServerHeader(IWriteMessage message, ulong session)
    {
        message.WriteByte(ServerMarker); message.WriteUInt64(session);
    }

    public static bool ReadServerHeader(IReadMessage message, out ulong session, out ServerPacket packet)
    {
        session = 0; packet = default;
        if (message.BitPosition < 0 || (long)message.LengthBits > (long)message.Buffer.Length * 8 ||
            message.LengthBits - message.BitPosition < 80 || message.ReadByte() != ServerMarker) { return false; }
        session = message.ReadUInt64(); packet = (ServerPacket)message.ReadByte();
        return packet is >= ServerPacket.SnapChunk and <= ServerPacket.Incompatible;
    }

    public static void WriteClientHeader(IWriteMessage message, ClientPacket packet, ulong session)
    {
        message.WriteByte((byte)packet);
        if (packet != ClientPacket.Hello) { message.WriteUInt64(session); }
    }

    public static void WriteBlob(IWriteMessage msg, ReadOnlySpan<byte> data)
    {
        msg.WriteVariableUInt32((uint)data.Length);
        if (data.Length == 0) { return; }
        byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
        try { data.CopyTo(rented); msg.WriteBytes(rented, 0, data.Length); }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>Returns null on any inconsistency; never throws for hostile lengths.</summary>
    public static byte[] ReadBlob(IReadMessage msg, int max)
    {
        if (max < 0 || msg.BitPosition < 0 || msg.LengthBits < msg.BitPosition || (long)msg.LengthBits > (long)msg.Buffer.Length * 8) { return null; }
        uint len = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (msg.LengthBits - msg.BitPosition < 8) { return null; }
            byte part = msg.ReadByte();
            if (shift == 28 && part > 15) { return null; }
            len |= (uint)(part & 127) << shift;
            if ((part & 128) == 0) { break; }
        }
        if (len > (uint)max || (msg.LengthBits - msg.BitPosition) < (long)len * 8) { return null; }
        return len == 0 ? Array.Empty<byte>() : msg.ReadBytes((int)len);
    }
}
