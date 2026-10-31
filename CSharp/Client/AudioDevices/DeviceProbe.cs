using System;
using System.Diagnostics;
using System.Threading;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using PrimaryPackage.Core;

namespace AudioDevices;

public static class DeviceProbe {
    private const int JoinMs = 2000;

    public static void Command(string[] args) {
        if (args.Length == 0) { ModLog.Message(AudioDeviceCoordinator.Report()); return; }
        if (!args[0].Equals("probe", StringComparison.OrdinalIgnoreCase)) {
            ModLog.Message(AudioDeviceCoordinator.Report());
            return;
        }
        string which = args.Length > 1 ? args[1].ToLowerInvariant() : "all";
        switch (which) {
            case "thread": CaptureThread(); break;
            case "race": Race(); break;
            case "churn": Churn(Count(args, 2, 8)); break;
            case "lost": Lost(); break;
            case "gone": Gone(); break;
            case "disable": Disable(); break;
            case "reload": Reload(); break;
            default:
                CaptureThread();
                Race();
                Churn(8);
                break;
        }
        ModLog.Message(AudioDeviceCoordinator.Report());
    }

    private static void CaptureThread() {
        if (!Ready(3)) { ModLog.Message("thread probe unavailable"); return; }
        Stopwatch clock = Stopwatch.StartNew();
        Thread worker = Spawn(static () => VoipCapture.TryRefreshDevice());
        bool joined = worker.Join(JoinMs);
        clock.Stop();
        ModLog.Message(joined
            ? $"capture thread path returns in {clock.ElapsedMilliseconds}ms"
            : "capture thread path does not return");
    }

    private static void Race() {
        if (!Ready(2) || !Ready(3) || !Ready(6)) { ModLog.Message("race probe unavailable"); return; }
        using ManualResetEventSlim gate = new ManualResetEventSlim(false);
        Thread worker = Spawn(() => {
            gate.Wait(JoinMs);
            VoipCapture.TryRefreshDevice();
            VoipCapture.Instance?.Dispose();
        });
        gate.Set();
        SoundManager.TryRefreshDevice();
        ModLog.Message(worker.Join(JoinMs) ? "race arms both sides" : "race worker does not return");
    }

    private static void Churn(int times) {
        SoundManager manager = GameMain.SoundManager;
        if (manager == null || !Ready(1)) { ModLog.Message("churn probe unavailable"); return; }
        string name = GameSettings.CurrentConfig.Audio.AudioOutputDevice;
        int before = AudioDeviceCoordinator.Opens;
        for (int i = 0; i < times; i++) { manager.InitializeAlcDevice(name); }
        ModLog.Message($"churn {times} collapses to {AudioDeviceCoordinator.Opens - before} opens");
    }

    private static void Lost() {
        SoundManager manager = GameMain.SoundManager;
        if (manager == null || !DeviceGate.Live) { ModLog.Message("lost probe unavailable"); return; }
        manager.Disconnected = true;
        ModLog.Message("output marked lost, recovery runs next frame");
    }

    private static void Gone() {
        if (!DeviceGate.Live) { ModLog.Message("gone probe unavailable"); return; }
        AudioDeviceCoordinator.Vanish();
        ModLog.Message("applied devices marked absent, the poll picks replacements within half a second");
    }

    private static void Disable() {
        SoundManager manager = GameMain.SoundManager;
        if (manager == null || !DeviceGate.Live) { ModLog.Message("disable probe unavailable"); return; }
        manager.Disabled = true;
        ModLog.Message("output marked disabled, recovery runs next frame");
    }

    private static void Reload() {
        SoundManager manager = GameMain.SoundManager;
        bool supported = AudioDeviceCoordinator.DetectSupported;
        IAudioOutputPolicy kept = AudioDeviceCoordinator.Policy;
        AudioDeviceCoordinator.Unmount();
        bool restored = manager == null || manager.CanDetectDisconnect == supported;
        bool inert = !DeviceGate.Live;
        AudioDeviceCoordinator.Mount(kept ?? new AudioResourceLifecycle(SoundPrefab.Prefabs));
        ModLog.Message($"reload restores vanilla detection {restored} and stays inert while down {inert}");
    }

    private static bool Ready(int target) => DeviceGate.Live && AudioDevicePlugin.Owned(target);

    private static Thread Spawn(Action body) {
        Thread worker = new Thread(() => {
            try {
                body();
            } catch (Exception error) {
                ModLog.Once($"probe worker {error.Message}");
            }
        }) {
            IsBackground = true,
            Name = "VoipCapture"
        };
        worker.Start();
        return worker;
    }

    private static int Count(string[] args, int index, int fallback) =>
        args.Length > index && int.TryParse(args[index], out int value) && value > 0 && value < 1000
            ? value
            : fallback;
}
