using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Client;
using SubSeal.Server;

// These adapters replace only engine/file access. Decisions run the linked production sources.
namespace Barotrauma {
    internal sealed class SubmarineInfo {
        internal string FilePath;
        internal string Name { get; set; }
        internal XElement SubmarineElement;
        internal bool IsFileCorrupted;
        internal Action<SubmarineInfo> OnReload;
        internal void Reload() => OnReload?.Invoke(this);
    }
    internal sealed class Submarine {
        internal static readonly List<Submarine> Loaded = new();
        internal bool Removed;
        internal SubmarineInfo Info;
    }
    internal sealed class GameSession {
        internal object Campaign { get; set; }
        internal SubmarineInfo SubmarineInfo { get; set; }
        internal readonly List<SubmarineInfo> OwnedSubmarines = new();
    }
    internal static class GameModePreset { internal static readonly object MultiPlayerCampaign = new(); }
    internal sealed class NetLobbyScreen {
        internal SubmarineInfo SelectedSub { get; set; }
        internal SubmarineInfo SelectedShuttle { get; set; }
        internal SubmarineInfo SelectedEnemySub { get; set; }
        internal object SelectedMode { get; set; }
        internal IReadOnlyList<SubmarineInfo> GetSubList() => Array.Empty<SubmarineInfo>();
    }
    internal sealed class ServerSettings { internal HashSet<string> HiddenSubs { get; set; } }
    internal sealed class GameServer { internal bool GameStarted; internal ServerSettings ServerSettings = new(); }
    internal static class GameMain {
        internal static NetLobbyScreen NetLobbyScreen { get; set; }
        internal static GameSession GameSession;
        internal static GameServer Server = new();
    }
}
namespace Barotrauma.Networking {
    internal enum FileTransferType { Submarine, CampaignSave, Other }
    internal sealed class NetworkConnection { }
}
namespace SubSeal.Server {
    internal sealed class Settings {
        internal bool PreviewProtection = true, RestrictToActiveSubmarines = true;
        internal bool IsPublicSubmarine(string path) => path == "public.sub";
    }
    internal sealed class Budget { internal bool TryTake(string identity, string path) => true; }
    internal static class Identity { internal static string Of(NetworkConnection connection) => "test"; }
}
namespace SubSeal.Client {
    internal enum SealProbe { Present, Absent, Unreadable }
    internal static class SealedFile { internal static SealProbe State = SealProbe.Present; internal static SealProbe Probe(string path) => State; }
    internal static class EngineSymbols { internal static void MarkFileCorrupted(SubmarineInfo info) => info.IsFileCorrupted = true; }
    internal static class PreviewLock {
        internal static bool PreviewUiReady => true;
        internal static bool IsCreating;
        internal static bool ShowLocked(SubmarineInfo info) => true;
        internal static void BeginCreate(bool locked) => IsCreating = true;
        internal static void EndCreate() => IsCreating = false;
        internal static void Release() => IsCreating = false;
    }
}
internal static class Program {
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) { throw new Exception(message); } }
    private static XElement Blueprint() => XElement.Parse("<Submarine><Hull ID='1' rect='0,0,100,100'/><Item ID='2' identifier='door'><Door isopen='false'/></Item></Submarine>");
    private static SubmarineInfo Protected() => new() { FilePath = "protected.sub", SubmarineElement = Blueprint() };
    private static void Main() {
        var settings = new Settings(); var entitlement = new Entitlement(settings, new Budget()); var connection = new NetworkConnection();
        GameMain.GameSession = new GameSession(); GameMain.Server.GameStarted = true;
        Check(!entitlement.Allows(connection, FileTransferType.Submarine, "dynamic.sub", out var denied) && denied == Claim.Protected, "unknown submarine remains protected");
        var instance = new Submarine { Info = new SubmarineInfo { FilePath = "dynamic.sub" } }; Submarine.Loaded.Add(instance);
        Check(entitlement.Allows(connection, FileTransferType.Submarine, "DYNAMIC.SUB", out var active) && active == Claim.Active, "loaded dynamic file has an active claim without campaign ownership");
        Check(entitlement.Allows(connection, FileTransferType.Submarine, "folder/../dynamic.sub", out _), "equivalent file paths share the same exact active claim");
        instance.Removed = true;
        Check(!entitlement.Allows(connection, FileTransferType.Submarine, "dynamic.sub", out _), "removed dynamic instance cannot grant content access");
        Check(!entitlement.Allows(connection, FileTransferType.Submarine, "other.sub", out _), "active claim does not open unrelated files");
        Check(entitlement.Allows(connection, FileTransferType.Submarine, "public.sub", out var publicClaim) && publicClaim == Claim.Public, "explicit public policy survives");
        var info = Protected(); ResidentAsset.Evict(info);
        Check(SubmarineSkeleton.IsReduced(info.SubmarineElement), "eviction retains only structural identity");
        info.OnReload = value => value.SubmarineElement = Blueprint(); LoadPatches.BeforeSubmarineConstructed(info);
        Check(!SubmarineSkeleton.IsReduced(info.SubmarineElement), "rehydration precedes world construction");
        info = Protected(); ResidentAsset.Evict(info); SealedFile.State = SealProbe.Unreadable; bool enteredConstructor = false;
        try { LoadPatches.BeforeSubmarineConstructed(info); enteredConstructor = true; } catch (InvalidDataException) { }
        Check(!enteredConstructor && info.IsFileCorrupted, "failed protected restore stops before native constructor and ID registration");
        SealedFile.State = SealProbe.Present;
        info = Protected(); ResidentAsset.Evict(info); info.OnReload = value => { };
        bool rejectedSkeleton = false;
        try { LoadPatches.BeforeSubmarineConstructed(info); } catch (InvalidDataException) { rejectedSkeleton = true; }
        Check(rejectedSkeleton, "unchanged skeleton cannot masquerade as rehydrated content");
        info = Protected(); ResidentAsset.Evict(info); info.FilePath = "other.sub";
        bool rejectedOrigin = false;
        try { LoadPatches.BeforeSubmarineConstructed(info); } catch (InvalidDataException) { rejectedOrigin = true; }
        Check(rejectedOrigin, "a reduced XML node cannot be reused under a different file identity");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { status = "PASS", checks, scope = "linked production entitlement and rehydration decisions, engine and file adapters; no actual transfer, decryption or network connection" }));
    }
}
