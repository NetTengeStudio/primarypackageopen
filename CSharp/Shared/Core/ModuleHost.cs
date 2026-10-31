using System;
using System.Collections.Generic;
using HarmonyLib;

namespace PrimaryPackage.Core;

public interface IModule {
    void Attach(Harmony harmony);

    void RoundStart();

    void Release();
}

// Modules requiring a frame tick use a single engine hook owned by ModuleHost.
// This avoids each module installing its own GameSession.Update patch.
public interface IFrameModule {
    void FrameUpdate(float deltaTime);
}

public interface ILoadCompleted {
    void LoadCompleted();
}

public static class ModuleHost {
    private static readonly List<IModule> modules = new List<IModule>();

    private static Harmony harmony;
    private static readonly List<IFrameModule> frameModules = new List<IFrameModule>();

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
        Guard("shared GameSession.Update", () => {
            var update = AccessTools.Method(typeof(GameSession), nameof(GameSession.Update));
            if (update == null) { throw new MissingMethodException("GameSession.Update"); }
            harmony.Patch(update, postfix: Bind(typeof(ModuleHost), nameof(FrameUpdatePostfix)));
        });
#if CLIENT
        Guard("GUI.Draw", () => HudFrame.Attach(harmony));
#endif
        for (int i = 0; i < set.Length; i++) {
            if (set[i] == null) { continue; }
            try {
                set[i].Attach(harmony);
                modules.Add(set[i]);
                if (set[i] is IFrameModule frameModule) { frameModules.Add(frameModule); }
            } catch (Exception error) {
                ModLog.Error($"{Name(set[i])} does not attach: {error.Message}");
                Retreat(set[i]);
            }
        }
    }

    public static void Release() {
        harmony?.UnpatchSelf();
        harmony = null;
        for (int i = modules.Count - 1; i >= 0; i--) { Retreat(modules[i]); }
        modules.Clear();
        frameModules.Clear();
#if CLIENT
        HudFrame.Release();
#endif
    }

    public static void LoadCompleted() {
        for (int i = 0; i < modules.Count; i++) {
            if (modules[i] is not ILoadCompleted ready) { continue; }
            try {
                ready.LoadCompleted();
            } catch (Exception error) {
                ModLog.Error($"{Name(modules[i])} does not finish loading: {error.Message}");
            }
        }
    }

    public static void FrameUpdatePostfix(float deltaTime) {
        for (int i = 0; i < frameModules.Count; i++) {
            IFrameModule module = frameModules[i];
            try {
                module.FrameUpdate(deltaTime);
            } catch (Exception error) {
                ModLog.Once($"{module.GetType().Name} frame update failed: {error.Message}");
            }
        }
    }

    public static void StartRoundPostfix() {
        for (int i = 0; i < modules.Count; i++) {
            try {
                modules[i].RoundStart();
            } catch (Exception error) {
                ModLog.Once($"{Name(modules[i])} does not start: {error.Message}");
            }
        }
    }

    private static void Retreat(IModule module) {
        try {
            module.Release();
        } catch (Exception error) {
            ModLog.Error($"{Name(module)} does not release: {error.Message}");
        }
    }

    private static string Name(IModule module) => module.GetType().Name;
}
