using System;
using Barotrauma;
using PrimaryPackage.Core;
using PrimaryPackage.Core.Fault;

namespace PrimaryPackage.Fault;

internal static class DeviceLossGuard {
    private static readonly DeviceLossTracker Tracker = new DeviceLossTracker(6.0);

    public static Exception PresentFinalizer(Exception __exception) {
        if (__exception == null) { return null; }
        if (!DeviceLoss.IsLoss(__exception)) { return __exception; }
        if (Tracker.MarkLost(Now())) {
            ModLog.Error($"graphics device was lost ({__exception.GetType().Name}, HRESULT 0x{__exception.HResult:X8}); rendering stopped, leaving the game in an orderly way");
        }
        return null;
    }

    public static bool DrawPrefix() {
        if (!Tracker.Lost) { return true; }
        if (Tracker.ShouldExit(Now())) { Leave(); }
        return false;
    }

    private static double Now() => Environment.TickCount64 / 1000.0;

    private static void Leave() {
        try {
            GameMain.Client?.Quit();
        } catch (Exception noted) {
            ModFault.Note("DeviceLossGuard.Quit", noted);
        }
        try {
            GameMain.Instance?.Exit();
        } catch (Exception noted) {
            ModFault.Note("DeviceLossGuard.Exit", noted);
        }
    }
}
