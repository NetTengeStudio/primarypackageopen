using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PrimaryPackage.Core;



public sealed class PatchSet {
    private readonly List<(MethodBase Target, MethodInfo Patch)> registrations = new List<(MethodBase, MethodInfo)>();

    public void Patch(Harmony harmony, MethodBase target, HarmonyMethod prefix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null) {
        if (target == null) { throw new MissingMethodException("patch target is missing"); }
        if (harmony == null) { throw new ArgumentNullException(nameof(harmony)); }

        harmony.Patch(target, prefix: prefix, transpiler: transpiler, finalizer: finalizer);
        Track(target, prefix);
        Track(target, transpiler);
        Track(target, finalizer);
    }

    public void Revert(Harmony harmony) => RevertFrom(harmony, 0);

    private void Track(MethodBase target, HarmonyMethod patch) {
        if (patch?.method is MethodInfo method) { registrations.Add((target, method)); }
    }

    private void RevertFrom(Harmony harmony, int firstRegistration) {
        for (int i = registrations.Count - 1; i >= firstRegistration; i--) {
            (MethodBase target, MethodInfo patch) = registrations[i];
            if (harmony == null) { continue; }
            try {
                harmony.Unpatch(target, patch);
            } catch (Exception error) {
                ModLog.Error($"{target.Name} keeps patch {patch.DeclaringType?.Name}.{patch.Name}: {error.Message}");
            }
        }
        if (firstRegistration < registrations.Count) {
            registrations.RemoveRange(firstRegistration, registrations.Count - firstRegistration);
        }
    }
}
