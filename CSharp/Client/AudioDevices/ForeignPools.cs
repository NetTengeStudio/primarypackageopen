using System;
using System.Reflection;
using Barotrauma.Sounds;
using HarmonyLib;
using PrimaryPackage.Core;

namespace AudioDevices;

public static class ForeignPools {
    private static readonly string[] Owners = {
        "SoundproofWalls.ReducedSoundBuffers",
        "SoundproofWalls.ExtendedSoundBuffers"
    };

    private static Action[] sinks = Array.Empty<Action>();

    public static int Count => sinks.Length;

    public static MethodBase Target() =>
        AccessTools.Method(typeof(SoundManager), "ReleaseResources", new[] { typeof(bool) });

    public static void Resolve() {
        Action[] found = new Action[Owners.Length];
        int count = 0;
        for (int i = 0; i < Owners.Length; i++) {
            Type owner = AccessTools.TypeByName(Owners[i]);
            if (owner == null) { continue; }
            MethodInfo clear = AccessTools.Method(owner, "ClearPool", Type.EmptyTypes);
            if (clear == null || !clear.IsStatic) { continue; }
            if (Delegate.CreateDelegate(typeof(Action), clear, false) is not Action sink) { continue; }
            found[count++] = sink;
            ModLog.Message($"{Owners[i]} clears with the device");
        }
        Array.Resize(ref found, count);
        sinks = found;
    }

    public static void Drop() => sinks = Array.Empty<Action>();

    public static void ReleasePostfix() {
        Action[] found = sinks;
        for (int i = 0; i < found.Length; i++) {
            try {
                found[i]();
            } catch (Exception error) {
                ModLog.Once($"foreign sound buffers don't clear {error.Message}");
            }
        }
    }
}
