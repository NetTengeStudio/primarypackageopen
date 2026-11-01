using System;
using System.Threading;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using PrimaryPackage.Core;

namespace AudioDevices;

public enum DeviceOpen {
    Opened,
    Refused,
    Broke
}

public enum DeviceOp {
    None = 0,
    OpenOutput,
    OpenCapture,
    CloseCapture
}

public static class DeviceGate {
    [ThreadStatic]
    private static DeviceOp ticket;

    private static int live;

    private static int owner;

    private static int beats;

    public static int Beats => Volatile.Read(ref beats);

    public static bool Live =>
        Volatile.Read(ref live) != 0 && Volatile.Read(ref beats) != 0 && !GameMain.IsExiting;

    public static bool Busy => Volatile.Read(ref owner) != 0;

    private static bool Held => Volatile.Read(ref owner) != 0 && Thread.CurrentThread == GameMain.MainThread;

    public static void Beat() {
        int next = beats + 1;
        Volatile.Write(ref beats, next == 0 ? 1 : next);
    }

    public static void Mount() {
        Volatile.Write(ref owner, 0);
        Volatile.Write(ref beats, 0);
        Volatile.Write(ref live, 1);
    }

    public static void Unmount() {
        Volatile.Write(ref live, 0);
        Volatile.Write(ref owner, 0);
        Volatile.Write(ref beats, 0);
    }

    public static bool Enter() {
        if (Thread.CurrentThread != GameMain.MainThread || owner != 0) { return false; }
        Volatile.Write(ref owner, 1);
        return true;
    }

    public static void Leave() => Volatile.Write(ref owner, 0);

    public static bool Authorized(DeviceOp op) {
        if (ticket != op) { return false; }
        ticket = DeviceOp.None;
        return true;
    }

    public static DeviceOpen OpenOutput(SoundManager manager, string name) {
        if (!Held || manager == null) { return DeviceOpen.Refused; }
        ticket = DeviceOp.OpenOutput;
        try {
            return manager.InitializeAlcDevice(name) ? DeviceOpen.Opened : DeviceOpen.Refused;
        } catch (Exception error) {
            ModLog.Once($"{name} doesn't open {error.Message}");
            return DeviceOpen.Broke;
        } finally {
            ticket = DeviceOp.None;
        }
    }

    public static void OpenCapture(string name) {
        if (!Held) { return; }
        ticket = DeviceOp.OpenCapture;
        try {
            VoipCapture.Create(name);
        } catch (Exception error) {
            ModLog.Once($"{name} doesn't open {error.Message}");
        } finally {
            ticket = DeviceOp.None;
        }
    }

    public static void CloseCapture(VoipCapture capture) {
        if (!Held || capture == null) { return; }
        ticket = DeviceOp.CloseCapture;
        try {
            capture.Dispose();
        } catch (Exception error) {
            ModLog.Once($"capture doesn't close {error.Message}");
        } finally {
            ticket = DeviceOp.None;
        }
    }
}
