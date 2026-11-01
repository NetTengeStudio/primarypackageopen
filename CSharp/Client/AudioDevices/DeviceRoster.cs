using System;
using System.Collections.Generic;
using System.Threading;
using OpenAL;

namespace AudioDevices;

public sealed class DeviceLists {
    public readonly IReadOnlyList<string> Outputs;

    public readonly IReadOnlyList<string> Inputs;

    public DeviceLists(IReadOnlyList<string> outputs, IReadOnlyList<string> inputs) {
        Outputs = outputs;
        Inputs = inputs;
    }
}

public static class DeviceRoster {
    public const int Listed = 1;

    public const int HasFirst = 2;

    public const int HasSecond = 4;

    private static DeviceLists published;

    private static DeviceLists current;

    private static long generation;

    private static int scanning;

    private static int degraded;

    private static string complaint;

    private static int enumeration;

    public static bool Degraded => Volatile.Read(ref degraded) != 0;

    public static bool Scanning => Volatile.Read(ref scanning) != 0;

    public static unsafe bool Connected(IntPtr device) {
        if (device == IntPtr.Zero) { return false; }
        int state = 1;
        Alc.GetIntegerv(device, Alc.EnumConnected, 1, (IntPtr)(&state));
        return Alc.GetError(device) != Alc.NoError || state != 0;
    }

    public static void Mount() {
        Interlocked.Increment(ref generation);
        Volatile.Write(ref degraded, 0);
        Volatile.Write(ref scanning, 0);
        complaint = null;
        Volatile.Write(ref published, Collect());
        current = null;
    }

    public static void Unmount() {
        Interlocked.Increment(ref generation);
        Volatile.Write(ref published, null);
        current = null;
        complaint = null;
    }

    public static void Begin() {
        current = Volatile.Read(ref published);
        if (current != null) { return; }
        current = Collect();
        Volatile.Write(ref published, current);
    }

    public static string Drain() {
        string held = Interlocked.Exchange(ref complaint, null);
        return held;
    }

    public static void Request() {
        if (Volatile.Read(ref degraded) != 0) {
            Volatile.Write(ref published, Collect());
            return;
        }
        if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0) { return; }
        long era = Volatile.Read(ref generation);
        try {
            new Thread(Work) { IsBackground = true, Name = "pp_devicescan" }.Start(era);
        } catch (Exception error) {
            Volatile.Write(ref scanning, 0);
            Volatile.Write(ref degraded, 1);
            Interlocked.Exchange(ref complaint, $"device scan doesn't start {error.Message}");
        }
    }

    private static void Work(object state) => Work((long)state);

    private static void Work(long era) {
        try {
            DeviceLists fresh = Collect();
            if (Volatile.Read(ref generation) == era) { Volatile.Write(ref published, fresh); }
        } catch (Exception error) {
            Volatile.Write(ref degraded, 1);
            Interlocked.Exchange(ref complaint, $"device list doesn't enumerate {error.Message}");
        } finally {
            Volatile.Write(ref scanning, 0);
        }
    }

    private static DeviceLists Collect() =>
        new DeviceLists(Read(Alc.OutputDevicesSpecifier), Read(Alc.CaptureDeviceSpecifier));

    private static IReadOnlyList<string> Read(int listSpecifier) {
        IReadOnlyList<string> names = Alc.GetStringList(IntPtr.Zero, listSpecifier);
        Alc.GetError(IntPtr.Zero);
        return names == null || names.Count == 0 ? null : names;
    }

    public static bool Enumerates {
        get {
            if (enumeration == 0) {
                bool ok = Alc.IsExtensionPresent(IntPtr.Zero, "ALC_ENUMERATION_EXT")
                    || Alc.IsExtensionPresent(IntPtr.Zero, "ALC_ENUMERATE_ALL_EXT");
                Alc.GetError(IntPtr.Zero);
                enumeration = ok ? 1 : 2;
            }
            return enumeration == 1;
        }
    }

    public static string Pick(int listSpecifier, int defaultSpecifier, string preferred) {
        IReadOnlyList<string> names = List(listSpecifier);
        if (names == null) { return null; }
        int found = IndexOf(names, preferred);
        if (found >= 0) { return names[found]; }
        found = IndexOf(names, Alc.GetString(IntPtr.Zero, defaultSpecifier));
        return found >= 0 ? names[found] : names[0];
    }

    public static int Presence(int listSpecifier, string first, string second) {
        IReadOnlyList<string> names = List(listSpecifier);
        if (names == null) { return Enumerates ? Listed : 0; }
        int mask = Listed;
        if (IndexOf(names, first) >= 0) { mask |= HasFirst; }
        if (IndexOf(names, second) >= 0) { mask |= HasSecond; }
        return mask;
    }

    public static string Names(int listSpecifier) {
        IReadOnlyList<string> names = List(listSpecifier);
        return names == null ? "none" : string.Join(", ", names);
    }

    private static IReadOnlyList<string> List(int listSpecifier) {
        DeviceLists lists = current;
        if (lists == null) { return null; }
        return listSpecifier == Alc.CaptureDeviceSpecifier ? lists.Inputs : lists.Outputs;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name) {
        if (string.IsNullOrEmpty(name)) { return -1; }
        for (int i = 0; i < names.Count; i++) {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) { return i; }
        }
        return -1;
    }
}
