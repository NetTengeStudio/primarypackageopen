using HarmonyLib;
using IEngine;
using IEngine.Execution;
using Barotrauma.LuaCs;
using Barotrauma.Networking;

namespace INet;

sealed class NetFeature : Feature
{
    System.Reflection.MethodInfo start, receive;
    public override string Name => "inet";
    protected override string Install(Harmony harmony)
    {
        Net.CompactState = Net.CachedChannels = false;
        start = AccessTools.Method(typeof(NetworkingService), nameof(NetworkingService.Start), new[] { typeof(string) });
        receive = AccessTools.Method(typeof(NetworkingService), nameof(NetworkingService.Receive), new[] { typeof(string), typeof(NetMessageReceived) });
        if (start == null || receive == null) { return "LuaCs networking endpoint contracts differ in this build"; }
        Net.CompactState = Enabled;
        return null;
    }
    protected override void OnToggled() => Refresh();
    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    void Refresh()
    {
        Net.CompactState = Active;
        Net.CachedChannels = Active &&
            !PatchCensus.Of(start).Any && !PatchCensus.Of(receive).Any;
    }
    internal override void Uninstall() { Net.CompactState = Net.CachedChannels = false; }
    public override string Report() => Net.Report();
}
