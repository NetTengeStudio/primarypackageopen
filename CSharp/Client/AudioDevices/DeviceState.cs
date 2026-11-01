using System;
using System.Threading;

namespace AudioDevices;

[Flags]
public enum DeviceFlags : uint {
    None = 0,
    OutputLost = 1u << 0,
    CaptureLost = 1u << 1,
    CaptureWanted = 1u << 2,
    OutputChosen = 1u << 3,
    CaptureChosen = 1u << 4,
    Chosen = OutputChosen | CaptureChosen
}

public enum DeviceWish {
    Output,
    OutputPick,
    Capture,
    CapturePick,
    CaptureOff,
    OutputLost,
    CaptureLost
}

public sealed class DeviceState {
    public static readonly DeviceState Idle = new DeviceState(null, null, DeviceFlags.None);

    public readonly string Output;

    public readonly string Capture;

    public readonly DeviceFlags Flags;

    private DeviceState(string output, string capture, DeviceFlags flags) {
        Output = output;
        Capture = capture;
        Flags = flags;
    }

    public bool Has(DeviceFlags flag) => (Flags & flag) != DeviceFlags.None;

    public static void Publish(ref DeviceState cell, DeviceWish wish, string name) {
        DeviceState current, next;
        do {
            current = Volatile.Read(ref cell);
            next = current.Edit(wish, Clean(name));
            if (ReferenceEquals(next, current)) { return; }
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref cell, next, current), current));
    }

    public static void Clear(ref DeviceState cell, DeviceFlags drop) {
        DeviceState current, next;
        do {
            current = Volatile.Read(ref cell);
            if ((current.Flags & drop) == DeviceFlags.None) { return; }
            next = new DeviceState(current.Output, current.Capture, current.Flags & ~drop);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref cell, next, current), current));
    }

    public static void Reset(ref DeviceState cell) => Volatile.Write(ref cell, Idle);

    public static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? null : name;

    private DeviceState Edit(DeviceWish wish, string name) {
        switch (wish) {
            case DeviceWish.Output:
                return name == null || string.Equals(Output, name, StringComparison.Ordinal)
                    ? this
                    : new DeviceState(name, Capture, Flags);
            case DeviceWish.OutputPick: {
                if (name == null) { return this; }
                DeviceFlags flags = Flags | DeviceFlags.OutputChosen;
                return flags == Flags && string.Equals(Output, name, StringComparison.Ordinal)
                    ? this
                    : new DeviceState(name, Capture, flags);
            }
            case DeviceWish.Capture: {
                DeviceFlags flags = Flags | DeviceFlags.CaptureWanted;
                string capture = name ?? Capture;
                return flags == Flags && string.Equals(Capture, capture, StringComparison.Ordinal)
                    ? this
                    : new DeviceState(Output, capture, flags);
            }
            case DeviceWish.CapturePick: {
                DeviceFlags flags = Flags | DeviceFlags.CaptureWanted | DeviceFlags.CaptureChosen;
                string capture = name ?? Capture;
                return flags == Flags && string.Equals(Capture, capture, StringComparison.Ordinal)
                    ? this
                    : new DeviceState(Output, capture, flags);
            }
            case DeviceWish.CaptureOff:
                return Has(DeviceFlags.CaptureWanted)
                    ? new DeviceState(Output, Capture, Flags & ~DeviceFlags.CaptureWanted)
                    : this;
            case DeviceWish.OutputLost:
                return Has(DeviceFlags.OutputLost)
                    ? this
                    : new DeviceState(Output, Capture, Flags | DeviceFlags.OutputLost);
            case DeviceWish.CaptureLost:
                return Has(DeviceFlags.CaptureLost)
                    ? this
                    : new DeviceState(Output, Capture, Flags | DeviceFlags.CaptureLost);
            default:
                return this;
        }
    }
}
