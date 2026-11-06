using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace INet;

public static class PackageParity {
    public static bool Expected(IReadOnlyList<ServerContentPackage> server, out CorePackage core,
        out List<RegularPackage> regular, out List<string> missing) {
        core = null;
        regular = new List<RegularPackage>();
        missing = new List<string>();
        for (int i = 0; i < server.Count; i++) {
            ServerContentPackage package = server[i];
            if (package.CorePackage is CorePackage foundCore) {
                core ??= foundCore;
                continue;
            }
            if (package.RegularPackage is RegularPackage foundRegular) {
                if (!regular.Contains(foundRegular)) { regular.Add(foundRegular); }
                continue;
            }
            if (package.IsMandatory) { missing.Add(package.Name); }
        }
        return core != null && missing.Count == 0;
    }

    public static bool Matches(CorePackage core, List<RegularPackage> expected) {
        if (!ReferenceEquals(ContentPackageManager.EnabledPackages.Core, core)) { return false; }
        var wanted = new HashSet<RegularPackage>(expected);
        int index = 0;
        foreach (RegularPackage enabled in ContentPackageManager.EnabledPackages.Regular) {
            if (!enabled.HasMultiplayerSyncedContent && !wanted.Contains(enabled)) { continue; }
            if (index >= expected.Count || !ReferenceEquals(expected[index], enabled)) { return false; }
            index++;
        }
        return index == expected.Count;
    }

    public static List<string> NotEnabled(List<RegularPackage> expected) {
        var names = new List<string>();
        var enabled = new HashSet<RegularPackage>(ContentPackageManager.EnabledPackages.Regular);
        for (int i = 0; i < expected.Count; i++) {
            if (!enabled.Contains(expected[i])) { names.Add(expected[i].Name); }
        }
        return names;
    }

    public static void Align(CorePackage core, List<RegularPackage> expected) {
        var regular = new List<RegularPackage>(expected);
        foreach (RegularPackage enabled in ContentPackageManager.EnabledPackages.Regular) {
            if (!enabled.HasMultiplayerSyncedContent && !regular.Contains(enabled)) { regular.Add(enabled); }
        }
        if (ContentPackageManager.EnabledPackages.BackupPackages.Core == null &&
            ContentPackageManager.EnabledPackages.BackupPackages.Regular == null) {
            ContentPackageManager.EnabledPackages.BackUp();
        }
        ContentPackageManager.EnabledPackages.SetCore(core);
        ContentPackageManager.EnabledPackages.SetRegular(regular);
    }
}

public sealed class LobbyParity : IEventUpdate {
    private const string AlignedKey = "pp_parity_aligned";

    private static LobbyParity instance;
    private static GameClient checkedClient;

    public static void Attach() {
        checkedClient = null;
        instance = new LobbyParity();
        LuaCsSetup.Instance?.EventService?.Subscribe<IEventUpdate>(instance);
    }

    public static void Release() {
        if (instance != null) { LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(instance); }
        instance = null;
        checkedClient = null;
    }

    public void OnUpdate(double fixedDeltaTime) {
        GameClient client = GameMain.Client;
        if (client == null) {
            checkedClient = null;
            return;
        }
        if (ReferenceEquals(client, checkedClient) || Screen.Selected != GameMain.NetLobbyScreen) { return; }
        ClientPeer peer = client.ClientPeer;
        if (peer == null || !peer.ContentPackageOrderReceived || peer.ServerContentPackages.IsDefaultOrEmpty) { return; }
        checkedClient = client;
        Verify(client, peer.ServerContentPackages);
    }

    private static void Verify(GameClient client, ImmutableArray<ServerContentPackage> server) {
        if (!PackageParity.Expected(server, out CorePackage core, out List<RegularPackage> regular, out List<string> missing)) {
            Refuse(missing);
            return;
        }
        int stamp = RuntimeHelpers.GetHashCode(client);
        if (PackageParity.Matches(core, regular)) {
            AppDomain.CurrentDomain.SetData(AlignedKey, null);
            return;
        }
        if (AppDomain.CurrentDomain.GetData(AlignedKey) is int aligned && aligned == stamp) {
            Refuse(PackageParity.NotEnabled(regular));
            return;
        }
        AppDomain.CurrentDomain.SetData(AlignedKey, stamp);
        ModLog.Message("content packages follow the server list");
        PackageParity.Align(core, regular);
    }

    private static void Refuse(List<string> names) {
        string list = names.Count == 0 ? "load order" : string.Join(", ", names);
        ModLog.Error($"content differs from the server in {list}");
        CoroutineManager.Invoke(() => {
            GameMain.Client?.Quit();
            GameMain.MainMenuScreen.Select();
            new GUIMessageBox(TextManager.Get("Error"), $"Content differs from the server: {list}");
        });
    }
}
