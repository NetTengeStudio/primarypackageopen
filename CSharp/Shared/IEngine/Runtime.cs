using System;
using System.Collections.Generic;
using System.Text;
using Barotrauma;
using HarmonyLib;

namespace IEngine;

public abstract class Feature
{
    public abstract string Name { get; }
    bool enabled = true;

    public bool Enabled
    {
        get => enabled;
        internal set
        {
            if (enabled == value) { return; }
            enabled = value;
            if (Installed) { OnToggled(); }
        }
    }
    public bool Installed { get; private set; }
    public string Fault { get; private set; }

    internal void TryInstall(Harmony harmony)
    {
        try
        {
            string blocker = Install(harmony);
            Installed = blocker == null;
            Fault = blocker;
        }
        catch (Exception e)
        {
            Installed = false;
            Fault = e.Message;
            DebugConsole.ThrowError($"IEngine: {Name} not installed", e);
        }
    }

    protected abstract string Install(Harmony harmony);
    internal virtual void OnLoadCompleted() { }
    internal virtual void RecheckObservers() { }
    protected virtual void OnToggled() { }
    internal virtual void Uninstall() { }
    public virtual string Report() => "";
    public virtual void SetVerify(bool on) { }
    public bool Active => Enabled && Installed;
}

public static class Runtime
{
    static readonly List<Feature> features = new();

    public static IReadOnlyList<Feature> Features => features;

    internal static void Install(Harmony harmony)
    {
        features.Clear();
        features.AddRange(FeatureCatalog.Create());
        foreach (Feature f in features) { f.TryInstall(harmony); }
    }

    internal static void WatchForeignPatches(Harmony harmony)
    {
        var post = new HarmonyMethod(typeof(Runtime), nameof(ForeignPatched));
        harmony.Patch(AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.Patch)), postfix: post);
        harmony.Patch(AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.Unpatch), new[] { typeof(HarmonyPatchType), typeof(string) }), postfix: post);
        harmony.Patch(AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.Unpatch), new[] { typeof(System.Reflection.MethodInfo) }), postfix: post);
    }

    static void ForeignPatched(Harmony ___instance)
    {
        if (___instance?.Id == EngineHost.HarmonyId) { return; }
        foreach (Feature f in features) { if (f.Installed) { f.RecheckObservers(); } }
    }

    internal static void OnLoadCompleted()
    {
        foreach (Feature f in features) { if (f.Installed) { f.OnLoadCompleted(); } }
    }

    internal static void Uninstall()
    {
        foreach (Feature f in features) { if (f.Installed) { f.Uninstall(); } }
        features.Clear();
    }

    public static string SetMode(string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        string selected = mode.Trim();
        bool all = selected.Equals("on", StringComparison.OrdinalIgnoreCase) || selected.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool none = selected.Equals("off", StringComparison.OrdinalIgnoreCase) || selected.Equals("none", StringComparison.OrdinalIgnoreCase) || selected.Equals("vanilla", StringComparison.OrdinalIgnoreCase);
        HashSet<string> names = all || none ? null : new HashSet<string>(selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        foreach (Feature f in features) { f.Enabled = all || (!none && names.Contains(f.Name)); }
        return Describe();
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        foreach (Feature f in features)
        {
            if (sb.Length > 0) { sb.Append(", "); }
            sb.Append(f.Name).Append(f.Installed ? (f.Enabled ? "=on" : "=off") : $"=unavailable({f.Fault})");
        }
        return sb.Length == 0 ? "no features" : sb.ToString();
    }
}
