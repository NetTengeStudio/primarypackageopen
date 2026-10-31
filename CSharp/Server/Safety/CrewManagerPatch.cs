using System;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.Server.Safety;

public sealed class CrewManagerSafetyPlugin : PrimaryPackage.Core.IModule
{
    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(CrewManager), nameof(CrewManager.AddCharacter), new[] { typeof(Character) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CrewManagerPatch), nameof(CrewManagerPatch.Prefix))));
        }
        catch (Exception e)
        {
            DebugConsole.ThrowError("Failed to patch CrewManager.AddCharacter", e);
        }
    }

    public void Release() { }
}

internal static class CrewManagerPatch
{
    public static bool Prefix(Character character)
    {
        return character?.Info != null;
    }
}
