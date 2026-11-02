using System;

namespace PrimaryPackage.Interactions;

[Flags]
internal enum WorldCapabilities : uint { None = 0, Properties = 1, StaticTranslation = 2, Announcements = 4, Phantoms = 8, BatchTranslation = 16, Baseline = 32 }
internal static class WorldContract {
    internal const byte SchemaVersion = 4;
    internal const string Channel = "pp.world.v1";
    // LuaCs fallback header: packet byte, kind byte, one-byte length + ASCII channel;
    // then NativeWire's UInt16 length. The registered-id header is smaller.
    internal static readonly int MaxEnvelopeBytes = PrimaryPackage.Networking.ChunkWire.MaxPacketBytes + 2 + 1 + Channel.Length + 2;
    // Topology and submarines are deliberately not advertised by this implementation.
    internal const WorldCapabilities Supported = WorldCapabilities.Properties | WorldCapabilities.StaticTranslation | WorldCapabilities.Announcements | WorldCapabilities.Phantoms | WorldCapabilities.BatchTranslation | WorldCapabilities.Baseline;
}
