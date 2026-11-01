using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public static class AdvertisedParity {
    private const string TrimmedKey = "pp_parity_trimmed";

    public static void Attach() => CoroutineManager.Invoke(Trim);

    private static void Trim() {
        ServerPeer peer = GameMain.Server?.ServerPeer;
        if (peer == null) { return; }
        if (AccessTools.Field(peer.GetType(), "contentPackages")?.GetValue(peer) is not ImmutableArray<ContentPackage> advertised ||
            advertised.IsDefault) {
            ModLog.Once("advertised package list is unreadable");
            return;
        }
        var hidden = new List<RegularPackage>();
        foreach (RegularPackage package in ContentPackageManager.EnabledPackages.Regular) {
            if (!package.Files.Any()) { continue; }
            if (!package.HasMultiplayerSyncedContent && !package.Files.All(f => f is SubmarineFile)) { continue; }
            if (!advertised.Contains(package)) { hidden.Add(package); }
        }
        if (hidden.Count == 0) { return; }
        if (AppDomain.CurrentDomain.GetData(TrimmedKey) is true) {
            ModLog.Once("advertised packages still differ from loaded ones");
            return;
        }
        AppDomain.CurrentDomain.SetData(TrimmedKey, true);
        ModLog.Message($"{string.Join(", ", hidden.Select(p => p.Name))} unload for this session because clients never receive them");
        ContentPackageManager.EnabledPackages.SetRegular(
            ContentPackageManager.EnabledPackages.Regular.Where(p => !hidden.Contains(p)).ToArray());
    }
}
