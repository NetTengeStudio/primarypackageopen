#nullable enable
using System;
using Barotrauma;
using Barotrauma.Sounds;
using HarmonyLib;

namespace AudioDevices;

public sealed class SoundManagerGainPatchPlugin : PrimaryPackage.Core.IModule
{
    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
        try
        {
            var target = AccessTools.PropertySetter(typeof(SoundManager), nameof(SoundManager.ListenerGain));
            if (target != null)
            {
                harmony.Patch(target,
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(SoundManagerGainPatch), nameof(SoundManagerGainPatch.Finalizer))));
            }
        }
        catch (Exception e)
        {
            DebugConsole.ThrowError("Failed to patch SoundManager.ListenerGain", e);
        }
    }

    public void Release() { }
}

public static class SoundManagerGainPatch
{
    public static Exception? Finalizer(Exception? __exception)
    {
        if (__exception != null && __exception.Message.Contains("listener gain"))
        {
            return null;
        }
        return __exception;
    }
}
