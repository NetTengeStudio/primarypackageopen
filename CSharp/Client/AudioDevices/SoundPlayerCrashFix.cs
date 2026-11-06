#nullable enable
using System;
using Barotrauma;
using Barotrauma.Sounds;
using HarmonyLib;
using PrimaryPackage.Core;

namespace AudioDevices;

public sealed class SoundPlayerCrashFixPlugin : IModule
{
    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
        var targetMethod = AccessTools.Method(typeof(SoundPlayer), "UpdateMusic");
        if (targetMethod != null)
        {
            harmony.Patch(targetMethod,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SoundPlayerCrashFix), nameof(SoundPlayerCrashFix.Prefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(SoundPlayerCrashFix), nameof(SoundPlayerCrashFix.Finalizer))));
        }
    }

    public void Release() { }
}

public static class SoundPlayerCrashFix
{
    private static readonly AccessTools.FieldRef<BackgroundMusic[]> targetMusicRef =
        AccessTools.StaticFieldRefAccess<BackgroundMusic[]>(AccessTools.Field(typeof(SoundPlayer), "targetMusic"));

    private static readonly AccessTools.FieldRef<BackgroundMusic[]> currentMusicRef =
        AccessTools.StaticFieldRefAccess<BackgroundMusic[]>(AccessTools.Field(typeof(SoundPlayer), "currentMusic"));

    public static void Prefix()
    {
        var target = targetMusicRef();
        if (target != null)
        {
            for (int i = 0; i < target.Length; i++)
            {
                if (target[i] != null && target[i].Sound == null)
                {
                    target[i] = null!;
                }
            }
        }

        var current = currentMusicRef();
        if (current != null)
        {
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != null && current[i].Sound == null)
                {
                    current[i] = null!;
                }
            }
        }
    }

    public static Exception? Finalizer(Exception? __exception)
    {
        if (__exception is NullReferenceException)
        {
            return null;
        }
        return __exception;
    }
}
