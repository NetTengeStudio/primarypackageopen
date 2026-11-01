using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage;
using PrimaryPackage.Core;

namespace AudioDevices;

public sealed class AudioDevicePlugin : IModule, ILoadCompleted {
    private static string harmonyId;
    private static readonly string[] Labels = {
        "GameMain.Update",
        "SoundManager.InitializeAlcDevice",
        "SoundManager.TryRefreshDevice",
        "VoipCapture.TryRefreshDevice",
        "VoipCapture.ChangeCaptureDevice",
        "VoipCapture.Create",
        "VoipCapture.Dispose",
        "SoundManager.ReleaseResources"
    };

    private DebugConsole.Command console;

    public static MethodBase Target(int index) {
        switch (index) {
            case 0: return AccessTools.Method(typeof(GameMain), "Update", new[] { typeof(GameTime) });
            case 1: return AccessTools.Method(typeof(SoundManager), nameof(SoundManager.InitializeAlcDevice), new[] { typeof(string) });
            case 2: return AccessTools.Method(typeof(SoundManager), nameof(SoundManager.TryRefreshDevice), Type.EmptyTypes);
            case 3: return AccessTools.Method(typeof(VoipCapture), nameof(VoipCapture.TryRefreshDevice), Type.EmptyTypes);
            case 4: return AccessTools.Method(typeof(VoipCapture), nameof(VoipCapture.ChangeCaptureDevice), new[] { typeof(string) });
            case 5: return AccessTools.Method(typeof(VoipCapture), nameof(VoipCapture.Create), new[] { typeof(string), typeof(ushort?) });
            case 6: return AccessTools.Method(typeof(VoipCapture), nameof(VoipCapture.Dispose), Type.EmptyTypes);
            case 7: return ForeignPools.Target();
            default: return null;
        }
    }

    public void Attach(Harmony harmony) {
        harmonyId = harmony.Id;
        ModuleHost.Guard(Labels[0], () => harmony.Patch(Target(0),
            postfix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.TickPostfix))));

        ModuleHost.Guard(Labels[1], () => harmony.Patch(Target(1),
            prefix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.InitializeAlcDevicePrefix)),
            postfix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.InitializeAlcDevicePostfix))));

        ModuleHost.Guard(Labels[2], () => harmony.Patch(Target(2),
            prefix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.RefreshOutputPrefix))));

        ModuleHost.Guard(Labels[3], () => harmony.Patch(Target(3),
            prefix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.RefreshCapturePrefix))));

        ModuleHost.Guard(Labels[4], () => harmony.Patch(Target(4),
            prefix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.ChangeCaptureDevicePrefix))));

        ModuleHost.Guard(Labels[5], () => harmony.Patch(Target(5),
            prefix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.CreateCapturePrefix))));

        ModuleHost.Guard(Labels[6], () => harmony.Patch(Target(6),
            prefix: ModuleHost.Bind(typeof(AudioDevicePatches), nameof(AudioDevicePatches.DisposeCapturePrefix))));

        ModuleHost.Guard(Labels[7], () => harmony.Patch(Target(7),
            postfix: HarmonyPatchOrder.Ordered(
                AccessTools.Method(typeof(ForeignPools), nameof(ForeignPools.ReleasePostfix)),
                HarmonyPatchOrder.ForeignPoolsFirst)));

        console = ModCommands.Register("pp_audiodevices", "audio device state, probe to stimulate the real entry points",
            DeviceProbe.Command, localOnClient: true, relayToServer: false);

        AudioDeviceCoordinator.Mount(new AudioResourceLifecycle(SoundPrefab.Prefabs));
        Audit();
    }

    public void LoadCompleted() => ForeignPools.Resolve();

    public void RoundStart() { }

    public void Release() {
        ForeignPools.Drop();
        AudioDeviceCoordinator.Unmount();
        ModCommands.Drop(console);
        console = null;
        harmonyId = null;
    }

    public static bool Owned(int index) {
        MethodBase target = Target(index);
        if (target == null) { return false; }
        Patches info = Harmony.GetPatchInfo(target);
        string id = harmonyId;
        if (info == null || id == null) { return false; }
        for (int i = 0; i < info.Prefixes.Count; i++) {
            if (info.Prefixes[i].owner == id) { return true; }
        }
        for (int i = 0; i < info.Postfixes.Count; i++) {
            if (info.Postfixes[i].owner == id) { return true; }
        }
        return false;
    }

    private static void Audit() {
        string id = harmonyId;
        for (int i = 0; i < Labels.Length; i++) {
            MethodBase target = Target(i);
            if (target == null) { ModLog.Error($"{Labels[i]} does not resolve"); continue; }
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) { continue; }
            Report(Labels[i], "prefixed", info.Prefixes, id);
            Report(Labels[i], "transpiled", info.Transpilers, id);
            Report(Labels[i], "finalized", info.Finalizers, id);
        }
    }

    private static void Report(string label, string kind, System.Collections.ObjectModel.ReadOnlyCollection<Patch> set, string id) {
        for (int i = 0; i < set.Count; i++) {
            if (set[i].owner == id) { continue; }
            ModLog.Message($"{label} also {kind} by {set[i].owner}");
        }
    }
}
