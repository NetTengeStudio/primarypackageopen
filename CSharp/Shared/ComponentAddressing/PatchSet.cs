using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public sealed class PatchSet {
    private readonly List<MethodBase> targets = new List<MethodBase>();

    public void Patch(Harmony harmony, MethodBase target, HarmonyMethod prefix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null) {
        if (target == null) { throw new MissingMethodException("patch target is missing"); }
        harmony.Patch(target, prefix: prefix, transpiler: transpiler, finalizer: finalizer);
        targets.Add(target);
    }

    public void Revert(Harmony harmony) {
        if (harmony != null) {
            for (int i = targets.Count - 1; i >= 0; i--) {
                try {
                    harmony.Unpatch(targets[i], HarmonyPatchType.All, harmony.Id);
                } catch (Exception) {
                    ModLog.Error($"{targets[i].Name} keeps its patch");
                }
            }
        }
        targets.Clear();
    }
}
