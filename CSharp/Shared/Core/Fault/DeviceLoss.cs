using System;

namespace PrimaryPackage.Core.Fault;

public static class DeviceLoss {
    private const int Removed = unchecked((int)0x887A0005);
    private const int Hung = unchecked((int)0x887A0006);
    private const int Reset = unchecked((int)0x887A0007);
    private const int DriverInternalError = unchecked((int)0x887A0020);

    public static bool IsLoss(Exception error) {
        int depth = 0;
        while (error != null && depth++ < 8) {
            int code = error.HResult;
            if (code == Removed || code == Hung || code == Reset || code == DriverInternalError) { return true; }
            error = error.InnerException;
        }
        return false;
    }
}

public sealed class DeviceLossTracker {
    private readonly double graceSeconds;
    private bool exitRequested;

    public bool Lost { get; private set; }
    public double Since { get; private set; }

    public DeviceLossTracker(double graceSeconds) {
        this.graceSeconds = graceSeconds;
    }

    public bool MarkLost(double now) {
        if (Lost) { return false; }
        Lost = true;
        Since = now;
        return true;
    }

    public bool ShouldExit(double now) {
        if (!Lost || exitRequested || now - Since < graceSeconds) { return false; }
        exitRequested = true;
        return true;
    }
}
