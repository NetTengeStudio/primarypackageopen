using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Combat;

public sealed class DamageGuardPlugin : PrimaryPackage.Core.IModule
{
    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(Limb), nameof(Limb.AddDamage),
                new[] { typeof(Vector2), typeof(IEnumerable<Affliction>), typeof(bool), typeof(float), typeof(float), typeof(Character) });
            if (target != null)
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DamageGuard), nameof(DamageGuard.Prefix))));
            }
        }
        catch (Exception e)
        {
            DebugConsole.ThrowError("Failed to patch Limb.AddDamage", e);
        }
    }

    public void Release() { }
}

public static class DamageGuard
{
    public static bool Prefix(Limb __instance, ref IEnumerable<Affliction> afflictions, ref AttackResult __result)
    {
        if (__instance == null || __instance.character == null || __instance.character.Removed)
        {
            __result = default;
            return false;
        }

        if (afflictions == null)
        {
            afflictions = Array.Empty<Affliction>();
        }

        return true;
    }
}
