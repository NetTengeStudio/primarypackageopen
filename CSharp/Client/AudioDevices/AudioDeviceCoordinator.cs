using System;
using System.Threading;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using OpenAL;
using PrimaryPackage.Core;

namespace AudioDevices;

public static class AudioDeviceCoordinator {
    private const string PrefsRelativePath = "Data/pp_audiodevices_client.xml";

    private const int ProbeMs = 100;

    private const int ScanFastMs = 500;

    private const int ScanSlowMs = 2000;

    private const int QuietMs = 500;

    private static DeviceState wish = DeviceState.Idle;

    private static DeviceChannel outputs;

    private static DeviceChannel inputs;

    private static long probeAt;

    private static long scanAt;

    private static bool detectSupported;

    private static bool latched;

    private static VoiceMode latchedMode;

    private static int opens;

    private static IAudioOutputPolicy policy;

    public static int Opens => opens;

    public static IAudioOutputPolicy Policy => policy;

    public static bool DetectSupported => detectSupported;

    public static bool OutputFaulted => outputs.Faulted;

    public static void Mount(IAudioOutputPolicy outputPolicy) {
        policy = outputPolicy;
        policy?.Mount(GameMain.SoundManager);
        outputs.Rearm();
        inputs.Rearm();
        LoadPrefs();
        DeviceRoster.Mount();
        DeviceRoster.Begin();
        SoundManager manager = GameMain.SoundManager;
        string output = DeviceState.Clean(GameSettings.CurrentConfig.Audio.AudioOutputDevice);
        string capture = DeviceState.Clean(GameSettings.CurrentConfig.Audio.VoiceCaptureDevice);
        outputs.Preferred ??= output;
        inputs.Preferred ??= capture;
        outputs.Applied = DeviceRoster.Pick(Alc.OutputDevicesSpecifier, Alc.DefaultDeviceSpecifier, output) ?? output;
        inputs.Applied = VoipCapture.Instance != null
            ? DeviceRoster.Pick(Alc.CaptureDeviceSpecifier, Alc.CaptureDefaultDeviceSpecifier, capture) ?? capture
            : null;
        outputs.Shown = outputs.Applied;
        inputs.Shown = inputs.Applied;
        probeAt = 0;
        scanAt = 0;
        DeviceState.Reset(ref wish);
        if (VoipCapture.Instance != null) { DeviceState.Publish(ref wish, DeviceWish.Capture, inputs.Applied); }
        Adopt(manager);
        DeviceGate.Mount();
    }

    public static void Unmount() {
        Unlatch();
        DeviceGate.Unmount();
        SoundManager manager = GameMain.SoundManager;
        if (manager != null) {
            manager.CanDetectDisconnect = detectSupported;
            if (manager.Disconnected && manager.alcDevice != IntPtr.Zero) { manager.Disconnected = false; }
        }
        DeviceState.Reset(ref wish);
        DeviceRoster.Unmount();
        policy = null;
    }

    public static bool MayOpenOutput(SoundManager manager, string target) =>
        policy == null || policy.MayOpen(manager, target);

    public static void Vanish() {
        outputs.Applied = "pp_absent_device";
        if (VoipCapture.Instance != null) { inputs.Applied = "pp_absent_device"; }
        outputs.Asked = null;
        inputs.Asked = null;
        outputs.Verdict = DeviceChannel.Unknown;
        inputs.Verdict = DeviceChannel.Unknown;
        probeAt = 0;
        scanAt = 0;
    }

    public static void Adopt(SoundManager manager) {
        if (manager == null || outputs.Faulted) { return; }
        detectSupported = manager.CanDetectDisconnect;
        manager.CanDetectDisconnect = false;
    }

    public static void RequestOutput(string name) =>
        DeviceState.Publish(ref wish, DeviceGate.Busy ? DeviceWish.Output : DeviceWish.OutputPick, name);

    public static void RequestCapture(string name) =>
        DeviceState.Publish(ref wish, DeviceGate.Busy ? DeviceWish.Capture : DeviceWish.CapturePick, name);

    public static void RequestCaptureOff() => DeviceState.Publish(ref wish, DeviceWish.CaptureOff, null);

    public static void NotifyOutputLost() => DeviceState.Publish(ref wish, DeviceWish.OutputLost, null);

    public static void NotifyCaptureLost() => DeviceState.Publish(ref wish, DeviceWish.CaptureLost, null);

    public static void Tick() {
        if (!DeviceGate.Live) { return; }
        if (!DeviceGate.Enter()) { return; }
        try {
            Reconcile();
        } catch (Exception error) {
            ModLog.Once($"audio devices don't reconcile {error.Message}");
        } finally {
            DeviceGate.Leave();
        }
    }

    public static string Report() {
        DeviceRoster.Begin();
        long now = Environment.TickCount64;
        DeviceState want = Volatile.Read(ref wish);
        VoipCapture capture = VoipCapture.Instance;
        SoundManager manager = GameMain.SoundManager;
        return $"live {DeviceGate.Live} beats {DeviceGate.Beats} detect {detectSupported} "
            + $"enumerates {DeviceRoster.Enumerates} pools {ForeignPools.Count} opens {opens} "
            + $"scan {(DeviceRoster.Degraded ? "sync" : "async")} "
            + $"hold {outputs.Remain(now)}/{inputs.Remain(now)}ms faulted {outputs.Faulted} latched {latched} flags {want.Flags}\n"
            + $"out applied {outputs.Applied ?? "none"} prefer {outputs.Preferred ?? "none"} want {want.Output ?? "same"} "
            + $"disabled {manager != null && manager.Disabled} lost {manager != null && manager.Disconnected} "
            + $"state {(policy == null ? "unmounted" : policy.State.ToString())} missing {(policy == null ? 0 : policy.Missing)}\n"
            + $"in applied {inputs.Applied ?? "none"} prefer {inputs.Preferred ?? "none"} want {want.Capture ?? "same"} "
            + $"capturing {capture != null && Volatile.Read(ref capture.capturing)} "
            + $"voice {GameSettings.CurrentConfig.Audio.VoiceSetting}\n"
            + $"outputs {DeviceRoster.Names(Alc.OutputDevicesSpecifier)}\n"
            + $"inputs {DeviceRoster.Names(Alc.CaptureDeviceSpecifier)}";
    }

    private static void Reconcile() {
        long now = Environment.TickCount64;
        DeviceRoster.Begin();
        string complaint = DeviceRoster.Drain();
        if (complaint != null) { ModLog.Once(complaint); }
        SoundManager manager = GameMain.SoundManager;
        Detect(manager, now);
        Scan(now);
        DeviceState want = Volatile.Read(ref wish);
        Promote(want);
        DeviceFlags done = DeviceFlags.None;
        bool moved = false;
        if (OutputDirty(manager, want, now)) {
            done |= DeviceFlags.OutputLost;
            moved |= ApplyOutput(manager, want, now);
        }
        if (CaptureDirty(want, now)) {
            done |= DeviceFlags.CaptureLost;
            moved |= ApplyCapture(want, now);
        }
        if (done != DeviceFlags.None) { DeviceState.Clear(ref wish, done); }
        if (moved) { Refresh(); }
    }

    private static void Detect(SoundManager manager, long now) {
        bool probe = now >= probeAt;
        if (probe) { probeAt = now + ProbeMs; }
        if (manager != null && !outputs.Faulted) {
            if (manager.Disabled || manager.Disconnected) {
                NotifyOutputLost();
            } else if (probe && detectSupported && !DeviceRoster.Connected(manager.alcDevice)) {
                manager.Disconnected = true;
                NotifyOutputLost();
            }
        }
        VoipCapture capture = VoipCapture.Instance;
        if (capture == null) { return; }
        if (capture.Disconnected || !Volatile.Read(ref capture.capturing)) { NotifyCaptureLost(); return; }
        if (!probe) { return; }
        Thread worker = capture.captureThread;
        if (worker == null || !worker.IsAlive) { NotifyCaptureLost(); }
    }

    private static void Scan(long now) {
        if (now < scanAt) { return; }
        DeviceRoster.Request();
        SweepOutput();
        SweepCapture();
        bool restless = latched || outputs.Restless(now) || inputs.Restless(now);
        scanAt = now + (restless ? ScanFastMs : ScanSlowMs);
    }

    private static void SweepOutput() {
        if (outputs.Faulted || GameMain.SoundManager == null || outputs.Applied == null) { return; }
        int seen = DeviceRoster.Presence(Alc.OutputDevicesSpecifier, outputs.Applied, outputs.Preferred);
        if ((seen & DeviceRoster.Listed) == 0) { return; }
        if (outputs.Judge(seen, !Same(outputs.Preferred, outputs.Applied), out int verdict)) {
            if (verdict == DeviceChannel.Absent) { ModLog.Message($"output {outputs.Applied} is gone"); }
            else if (verdict == DeviceChannel.Returned) { ModLog.Message($"output {outputs.Preferred} is back"); }
        }
        if (verdict == DeviceChannel.Absent) {
            NotifyOutputLost();
        } else if (verdict == DeviceChannel.Returned) {
            outputs.Asked = null;
            RequestOutput(outputs.Preferred);
        }
    }

    private static void SweepCapture() {
        if (latched) {
            int back = DeviceRoster.Presence(Alc.CaptureDeviceSpecifier, inputs.Preferred, null);
            if ((back & DeviceRoster.Listed) == 0) { return; }
            Unlatch();
            inputs.Asked = null;
            RequestCapture((back & DeviceRoster.HasFirst) != 0 ? inputs.Preferred : null);
            return;
        }
        if (VoipCapture.Instance == null || inputs.Applied == null) { return; }
        int mic = DeviceRoster.Presence(Alc.CaptureDeviceSpecifier, inputs.Applied, inputs.Preferred);
        if ((mic & DeviceRoster.Listed) == 0) { return; }
        if (inputs.Judge(mic, !Same(inputs.Preferred, inputs.Applied), out int verdict)) {
            if (verdict == DeviceChannel.Absent) { ModLog.Message($"input {inputs.Applied} is gone"); }
            else if (verdict == DeviceChannel.Returned) { ModLog.Message($"input {inputs.Preferred} is back"); }
        }
        if (verdict == DeviceChannel.Absent) {
            NotifyCaptureLost();
        } else if (verdict == DeviceChannel.Returned) {
            inputs.Asked = null;
            RequestCapture(inputs.Preferred);
        }
    }

    private static void Promote(DeviceState want) {
        if (!want.Has(DeviceFlags.Chosen)) { return; }
        bool changed = false;
        if (want.Has(DeviceFlags.OutputChosen) && want.Output != null && !Same(want.Output, outputs.Preferred)) {
            outputs.Preferred = want.Output;
            outputs.Chosen();
            changed = true;
        }
        if (want.Has(DeviceFlags.CaptureChosen) && want.Capture != null && !Same(want.Capture, inputs.Preferred)) {
            inputs.Preferred = want.Capture;
            inputs.Chosen();
            changed = true;
        }
        DeviceState.Clear(ref wish, DeviceFlags.Chosen);
        if (changed) { SavePrefs(); }
    }

    private static bool OutputDirty(SoundManager manager, DeviceState want, long now) {
        if (outputs.Faulted) { return false; }
        if (manager == null || GameMain.Instance == null || !GameMain.Instance.HasLoaded) { return false; }
        return (want.Has(DeviceFlags.OutputLost)
                || manager.Disabled
                || manager.Disconnected
                || manager.alcDevice == IntPtr.Zero
                || outputs.Applied == null
                || (want.Output != null && !Same(want.Output, outputs.Applied) && !Same(want.Output, outputs.Asked)))
            && outputs.Ready(now)
            && MayOpenOutput(manager, want.Output ?? outputs.Preferred);
    }

    private static bool CaptureDirty(DeviceState want, long now) {
        VoipCapture existing = VoipCapture.Instance;
        bool wanted = Wanted(want);
        bool dirty = wanted != (existing != null)
            || want.Has(DeviceFlags.CaptureLost)
            || (wanted && existing != null && (existing.Disconnected || !Volatile.Read(ref existing.capturing)))
            || (wanted && want.Capture != null && !Same(want.Capture, inputs.Applied)
                && !Same(want.Capture, inputs.Asked));
        if (!dirty) { return false; }
        return !wanted || inputs.Ready(now);
    }

    private static bool Wanted(DeviceState want) =>
        want.Has(DeviceFlags.CaptureWanted)
        && GameSettings.CurrentConfig.Audio.VoiceSetting != VoiceMode.Disabled;

    private static bool ApplyOutput(SoundManager manager, DeviceState want, long now) {
        string target = DeviceRoster.Pick(Alc.OutputDevicesSpecifier, Alc.DefaultDeviceSpecifier,
            want.Output ?? outputs.Preferred ?? DeviceState.Clean(GameSettings.CurrentConfig.Audio.AudioOutputDevice));
        if (target == null) {
            outputs.Defer(now, QuietMs);
            ModLog.Once("no audio output device");
            return false;
        }
        bool healthy = manager.alcDevice != IntPtr.Zero && !manager.Disabled && !manager.Disconnected;
        outputs.Asked = want.Output;
        if (healthy && !want.Has(DeviceFlags.OutputLost) && Same(target, outputs.Applied)) { return false; }
        IntPtr staleDevice = manager.alcDevice;
        IntPtr staleContext = manager.alcContext;
        DeviceOpen opened = DeviceGate.OpenOutput(manager, target);
        Retire(manager, staleDevice, staleContext);
        if (opened != DeviceOpen.Opened) {
            if (opened == DeviceOpen.Broke) {
                outputs.Faulted = true;
                manager.CanDetectDisconnect = detectSupported;
                if (manager.alcDevice != IntPtr.Zero) { outputs.Applied = target; }
                ModLog.Once("output device switching stops");
            } else {
                outputs.Failed(now);
                ModLog.Once($"{target} doesn't open");
            }
            return false;
        }
        opens++;
        ModLog.Message($"output now {target}");
        outputs.Applied = target;
        outputs.Won();
        if (Same(target, outputs.Preferred)) { WriteOutputConfig(target); }
        return true;
    }

    private static void Retire(SoundManager manager, IntPtr staleDevice, IntPtr staleContext) {
        if (staleContext != IntPtr.Zero && staleContext != manager.alcContext) {
            Alc.DestroyContext(staleContext);
        }
        if (staleDevice != IntPtr.Zero && staleDevice != manager.alcDevice && !Alc.CloseDevice(staleDevice)) {
            ModLog.Once("previous output device doesn't close");
        }
    }

    private static bool ApplyCapture(DeviceState want, long now) {
        VoipCapture existing = VoipCapture.Instance;
        if (!Wanted(want)) {
            if (existing == null) { inputs.Applied = null; return false; }
            DeviceGate.CloseCapture(existing);
            inputs.Applied = null;
            return true;
        }
        string target = DeviceRoster.Pick(Alc.CaptureDeviceSpecifier, Alc.CaptureDefaultDeviceSpecifier,
            want.Capture ?? inputs.Preferred ?? DeviceState.Clean(GameSettings.CurrentConfig.Audio.VoiceCaptureDevice));
        bool broken = existing != null && (existing.Disconnected || !Volatile.Read(ref existing.capturing));
        if (target == null) {
            if (existing != null && !broken) { inputs.Defer(now, QuietMs); return false; }
            if (existing != null) { DeviceGate.CloseCapture(existing); }
            inputs.Applied = null;
            inputs.Verdict = DeviceChannel.Unknown;
            inputs.Defer(now, QuietMs);
            ModLog.Once("no voice capture device");
            return existing != null;
        }
        inputs.Asked = want.Capture;
        if (existing != null && !broken && !want.Has(DeviceFlags.CaptureLost) && Same(target, inputs.Applied)) {
            return false;
        }
        if (existing != null) { DeviceGate.CloseCapture(existing); }
        VoiceMode before = GameSettings.CurrentConfig.Audio.VoiceSetting;
        DeviceGate.OpenCapture(target);
        if (VoipCapture.Instance == null) {
            inputs.Applied = null;
            Latch(before);
            inputs.Failed(now);
            ModLog.Once($"{target} doesn't open");
            return true;
        }
        inputs.Applied = target;
        inputs.Won();
        ModLog.Message($"input now {target}");
        if (Same(target, inputs.Preferred)) { WriteCaptureConfig(target); }
        return true;
    }

    private static void Refresh() {
        if (!GUI.SettingsMenuOpen) {
            outputs.Shown = outputs.Applied;
            inputs.Shown = inputs.Applied;
            return;
        }
        if (Same(outputs.Shown, outputs.Applied) && Same(inputs.Shown, inputs.Applied)) { return; }
        outputs.Shown = outputs.Applied;
        inputs.Shown = inputs.Applied;
        SettingsMenu.Instance?.CreateAudioAndVCTab(true);
    }

    private static void Latch(VoiceMode before) {
        if (before == VoiceMode.Disabled || latched) { return; }
        if (GameSettings.CurrentConfig.Audio.VoiceSetting != VoiceMode.Disabled) { return; }
        latched = true;
        latchedMode = before;
        ModLog.Message("voice waits for an input device");
    }

    private static void Unlatch() {
        if (!latched) { return; }
        latched = false;
        GameSettings.Config config = GameSettings.CurrentConfig;
        if (config.Audio.VoiceSetting != VoiceMode.Disabled) { return; }
        config.Audio.VoiceSetting = latchedMode;
        GameSettings.SetCurrentConfig(config);
        ModLog.Message("voice returns");
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    private static void WriteOutputConfig(string target) {
        GameSettings.Config config = GameSettings.CurrentConfig;
        if (Same(config.Audio.AudioOutputDevice, target)) { return; }
        config.Audio.AudioOutputDevice = target;
        GameSettings.SetCurrentConfig(config);
    }

    private static void WriteCaptureConfig(string target) {
        GameSettings.Config config = GameSettings.CurrentConfig;
        if (Same(config.Audio.VoiceCaptureDevice, target)) { return; }
        config.Audio.VoiceCaptureDevice = target;
        GameSettings.SetCurrentConfig(config);
    }

    private static void LoadPrefs() => ModPrefs.Read(PrefsRelativePath, root => {
        outputs.Preferred = DeviceState.Clean((string)root.Attribute("output"));
        inputs.Preferred = DeviceState.Clean((string)root.Attribute("capture"));
    });

    private static void SavePrefs() => ModPrefs.Write(PrefsRelativePath, new XElement("AudioDevices",
        new XAttribute("output", outputs.Preferred ?? string.Empty),
        new XAttribute("capture", inputs.Preferred ?? string.Empty)));
}
