using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace IEngine.Execution;

static class PatchCensus
{
    public readonly record struct Foreign(string[] Prefixes, string[] Postfixes, string[] Transpilers, string[] Finalizers)
    {
        public bool Any => Prefixes.Length + Postfixes.Length + Transpilers.Length + Finalizers.Length > 0;
        public bool Observers => Prefixes.Length + Postfixes.Length + Finalizers.Length > 0;
        public override string ToString() =>
            $"prefix[{string.Join(",", Prefixes)}] postfix[{string.Join(",", Postfixes)}] transpiler[{string.Join(",", Transpilers)}] finalizer[{string.Join(",", Finalizers)}]";
    }

    public static Foreign Of(MethodBase method) => Of(method, null);

    public static Foreign Of(MethodBase method, Func<MethodInfo, bool> compatibleObserver)
    {
        Patches info = Harmony.GetPatchInfo(method);
        if (info == null) { return new Foreign(new string[0], new string[0], new string[0], new string[0]); }
        return new Foreign(Owners(info.Prefixes, compatibleObserver), Owners(info.Postfixes, compatibleObserver), Owners(info.Transpilers, null), Owners(info.Finalizers, compatibleObserver));
    }

    static string[] Owners(IEnumerable<Patch> patches, Func<MethodInfo, bool> compatibleObserver) =>
        patches.Where(p => p.owner != EngineHost.HarmonyId && !(compatibleObserver?.Invoke(p.PatchMethod) ?? false)).Select(p => $"{p.owner}:{p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}").Distinct().ToArray();
}
