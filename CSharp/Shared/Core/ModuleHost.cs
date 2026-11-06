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

public interface IRoundEnded {
    void RoundEnd();
}

public static class ModuleHost {
    private sealed class ModuleRegistration {
        public readonly IModule Module;
        public readonly Harmony Harmony;

        public ModuleRegistration(IModule module, Harmony harmony) {
            Module = module;
            Harmony = harmony;
        }
    }

    private static readonly List<ModuleRegistration> modules = new List<ModuleRegistration>();
    private static readonly List<string> startupFailures = new List<string>();

    public static IReadOnlyList<string> StartupFailures => startupFailures;

    private static Harmony harmony;

    public static Harmony Instance => harmony;

    public static HarmonyMethod Bind(Type owner, string name) =>
        new HarmonyMethod(AccessTools.Method(owner, name));

    private static void StartupFailed(string label, Exception error = null) {
        string message = error == null ? label : label + ": " + error;
        startupFailures.Add(message);
        ModLog.Error(message);
    }

    public static void Guard(string label, Action patch) {
        try {
            patch();
        } catch (Exception error) {
            StartupFailed($"{label} does not patch", error);
        }
    }

    public static void Attach(string harmonyId, params IModule[] set) {
        if (harmony != null) { return; }
        startupFailures.Clear();
        harmony = new Harmony(harmonyId);
        try {
            harmony.Patch(
                AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound),
                    new[] { typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo) }),
                postfix: Bind(typeof(ModuleHost), nameof(StartRoundPostfix)));
        } catch (Exception error) {
            StartupFailed("round start hook is missing", error);
        }
        try {
            var endRound = AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound),
                new[] { typeof(string), typeof(CampaignMode.TransitionType), typeof(TraitorManager.TraitorResults?), typeof(bool) })
                ?? typeof(GameSession).GetMethod(nameof(GameSession.EndRound), BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                ?? AccessTools.GetDeclaredMethods(typeof(GameSession)).Find(m => m.Name == nameof(GameSession.EndRound));
            if (endRound != null) {
                harmony.Patch(endRound, postfix: Bind(typeof(ModuleHost), nameof(EndRoundPostfix)));
            } else {
                StartupFailed("round end hook target method could not be found");
            }
        } catch (Exception error) {
            StartupFailed("round end hook is missing", error);
        }
#if CLIENT
        Guard("GUI.Draw", () => HudFrame.Attach(harmony));
#endif
        for (int i = 0; i < set.Length; i++) {
            if (set[i] == null) { continue; }
            Harmony moduleHarmony = new Harmony(harmonyId + "." + i + "." + set[i].GetType().FullName);
            try {
                set[i].Attach(moduleHarmony);
                modules.Add(new ModuleRegistration(set[i], moduleHarmony));
            } catch (Exception error) {
                StartupFailed($"{Name(set[i])} does not attach", error);
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
                StartupFailed(Name(module) + " does not finish loading", error);
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

    public static void EndRoundPostfix() {
        for (int i = modules.Count - 1; i >= 0; i--) {
            if (modules[i].Module is not IRoundEnded listener) { continue; }
            try {
                listener.RoundEnd();
            } catch (Exception error) {
                ModLog.Once($"{Name(modules[i].Module)} cannot end round: {error.Message}");
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

    private static void Unpatch(ModuleRegistration registration) =>
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
