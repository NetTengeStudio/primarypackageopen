// Offline test-only engine stubs. No Barotrauma DLLs or game boot are required.
namespace Barotrauma
{
    internal static class Timing
    {
        internal static double TotalTime { get; set; }
    }
}
namespace Barotrauma.Networking
{
    internal class NetworkConnection { }
    internal enum FileTransferType { Submarine = 1, CampaignSave = 2, Mod = 3 }
}
namespace SubSeal.Server
{
    internal static class Diagnostics
    {
        internal static void Note(string message) { }
    }
}
