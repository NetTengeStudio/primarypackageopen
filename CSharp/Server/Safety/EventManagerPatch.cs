using System;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.Server.Safety;

public sealed class EventManagerSafetyPlugin : PrimaryPackage.Core.IModule
{
    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(EventManager), "IsCrewAway"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(EventManagerPatch), nameof(EventManagerPatch.Prefix))));
        }
        catch (Exception e)
        {
            DebugConsole.ThrowError("Failed to patch EventManager.IsCrewAway", e);
        }
    }

    public void Release() { }
}

internal static class EventManagerPatch
{
    public static bool Prefix(ref bool __result)
    {
        if (GameMain.Server?.ConnectedClients == null)
        {
            __result = false;
            return false;
        }
        return true;
    }
}
