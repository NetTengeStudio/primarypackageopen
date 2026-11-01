using System;
using System.Collections.Generic;
using HarmonyLib;

namespace PrimaryPackage.Core;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

public interface IModule {
    void Attach(Harmony harmony);

    void RoundStart();

    void Release();
}

public interface ILoadCompleted {
    void LoadCompleted();
}

public static class ModuleHost {
    private sealed class Registration {
        public readonly IModule Module;
        public readonly Harmony Harmony;

        public Registration(IModule module, Harmony harmony) {
            Module = module;
            Harmony = harmony;
        }
    }

    private static readonly List<Registration> modules = new List<Registration>();

    private static Harmony harmony;

    public static Harmony Instance => harmony;

    public static HarmonyMethod Bind(Type owner, string name) =>
        new HarmonyMethod(AccessTools.Method(owner, name));

    public static void Guard(string label, Action patch) {
        try {
            patch();
        } catch (Exception error) {
            ModLog.Error($"{label} does not patch: {error.Message}");
        }
    }

    public static void Attach(string harmonyId, params IModule[] set) {
        if (harmony != null) { return; }
        harmony = new Harmony(harmonyId);
        try {
            harmony.Patch(
                AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound),
                    new[] { typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo) }),
                postfix: Bind(typeof(ModuleHost), nameof(StartRoundPostfix)));
        } catch (Exception error) {
            ModLog.Error($"round start hook is missing: {error.Message}");
        }
#if CLIENT
        Guard("GUI.Draw", () => HudFrame.Attach(harmony));
#endif
        for (int i = 0; i < set.Length; i++) {
            if (set[i] == null) { continue; }
            Harmony moduleHarmony = new Harmony(harmonyId + "." + i + "." + set[i].GetType().FullName);
            try {
                set[i].Attach(moduleHarmony);
                modules.Add(new Registration(set[i], moduleHarmony));
            } catch (Exception error) {
                ModLog.Error($"{Name(set[i])} does not attach: {error.Message}");
                Retreat(set[i], moduleHarmony);
            }
        }
    }

    public static void Release() {
        for (int i = modules.Count - 1; i >= 0; i--) {
            Unpatch(modules[i]);
        }
        for (int i = modules.Count - 1; i >= 0; i--) {
            Retreat(modules[i].Module);
        }
        modules.Clear();
        harmony?.UnpatchSelf();
        harmony = null;
#if CLIENT
        HudFrame.Release();
#endif
    }

    public static void LoadCompleted() {
        for (int i = 0; i < modules.Count; i++) {
            IModule module = modules[i].Module;
            if (module is not ILoadCompleted ready) { continue; }
            try {
                ready.LoadCompleted();
            } catch (Exception error) {
                ModLog.Error("" + Name(module) + " does not finish loading: " + error.Message);
            }
        }
    }

    public static void StartRoundPostfix() {
        for (int i = 0; i < modules.Count; i++) {
            try {
                modules[i].Module.RoundStart();
            } catch (Exception error) {
                ModLog.Once("" + Name(modules[i].Module) + " does not start: " + error.Message);
            }
        }
    }

    private static void Retreat(IModule module, Harmony owner) {
        Unpatch(module, owner);
        Retreat(module);
    }

    private static void Retreat(IModule module) {
        try {
            module.Release();
        } catch (Exception error) {
            ModLog.Error($"{Name(module)} does not release: {error.Message}");
        }
    }

    private static void Unpatch(Registration registration) =>
        Unpatch(registration.Module, registration.Harmony);

    private static void Unpatch(IModule module, Harmony owner) {
        try {
            owner?.UnpatchSelf();
        } catch (Exception error) {
            ModLog.Error("" + Name(module) + " patches do not roll back: " + error.Message);
        }
    }

    private static string Name(IModule module) => module.GetType().Name;
}
