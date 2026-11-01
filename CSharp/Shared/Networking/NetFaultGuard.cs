using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public sealed class NetFaultGuardPlugin : IModule {
    private readonly PatchSet patches = new PatchSet();
    private Harmony harmony;

    public void RoundStart() { }

    public void Attach(Harmony harmony) {
        this.harmony = harmony;
        try {
#if CLIENT
            patches.Patch(harmony,
                AccessTools.Method(typeof(GameClient), "ReadDataMessage", new[] { typeof(IReadMessage) }),
                finalizer: ModuleHost.Bind(typeof(NetFaultGuard), nameof(NetFaultGuard.ReadFinalizer)));
#else
            patches.Patch(harmony,
                AccessTools.Method(typeof(GameServer), "HandleClientError", new[] { typeof(IReadMessage), typeof(Client) }),
                prefix: ModuleHost.Bind(typeof(NetFaultGuard), nameof(NetFaultGuard.ErrorReportPrefix)));
#endif
        } catch (Exception e) {
            ModLog.Error($"netfault guard does not patch: {e.Message}");
        }
    }

    public void Release() {
        patches.Revert(harmony);
        harmony = null;
    }
}

internal static class NetFaultGuard {
    private const int BurstLimit = 8;
    private const double BurstSeconds = 10.0;
    private const int ResyncLimit = 3;
    private const double ResyncSeconds = 60.0;

#if CLIENT
    private static int burst;
    private static double burstStart;

    public static Exception ReadFinalizer(Exception __exception) {
        if (__exception == null) { return null; }
        if (GameMain.Client == null || !GameMain.Client.GameStarted || Screen.Selected != GameMain.GameScreen) { return __exception; }
        double now = Timing.TotalTime;
        if (now - burstStart > BurstSeconds) {
            burstStart = now;
            burst = 0;
        }
        if (++burst > BurstLimit) { return __exception; }
        ModLog.Warning($"server message could not be read, skipped it and waiting for the resend: {__exception.GetType().Name}: {__exception.Message}");
        return null;
    }
#else
    private sealed class ResyncState {
        public int Count;
        public double Start;
    }

    private static readonly ConditionalWeakTable<Client, ResyncState> resyncs = new();

    public static bool ErrorReportPrefix(Client c) {
        GameServer server = GameMain.Server;
        if (server == null || c == null || !server.GameStarted || !c.InGame) { return true; }
        double now = Timing.TotalTime;
        ResyncState state = resyncs.GetOrCreateValue(c);
        if (now - state.Start > ResyncSeconds) {
            state.Start = now;
            state.Count = 0;
        }
        if (++state.Count > ResyncLimit) { return true; }
        try {
            server.EntityEventManager.InitClientMidRoundSync(c);
        } catch (Exception e) {
            ModLog.Error($"resync of {c.Name} failed: {e.Message}");
            return true;
        }
        ModLog.Warning($"{c.Name} reported a network error, resyncing it instead of kicking");
        return false;
    }
#endif

}
