using System;

namespace PrimaryPackage.Interactions;

[Flags]
internal enum WorldCapabilities : uint { None = 0, Properties = 1, StaticTranslation = 2, Announcements = 4, Phantoms = 8 }
internal static class WorldContract {
    internal const byte SchemaVersion = 1;
    // Topology and submarines are deliberately not advertised by this implementation.
    internal const WorldCapabilities Supported = WorldCapabilities.Properties | WorldCapabilities.StaticTranslation | WorldCapabilities.Announcements | WorldCapabilities.Phantoms;
}
