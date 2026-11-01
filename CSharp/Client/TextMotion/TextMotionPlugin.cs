using System;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.TextMotion;

public sealed class TextMotionPlugin : IModule, ILoadCompleted {
    private Harmony patches;
    public void Attach(Harmony harmony) {
        
        patches = new Harmony("com.primarypackage.textmotion");
        try {
            ChatPresentation.Attach(patches);
            Speech.Attach(patches);
            patches.Patch(AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound)),
                prefix: ModuleHost.Bind(typeof(TextMotionPlugin), nameof(ResetRound)));
            TypeSettings.Attach();
        } catch {
            Release();
            throw;
        }
    }
    public void RoundStart() => ResetRound();
    public void LoadCompleted() => TypeSound.Warm();
    public static void ResetRound() {
        ChatPresentation.Reset(); Speech.Reset(); TypeSound.Release();
    }
    public void Release() {
        patches?.UnpatchSelf(); patches = null;
        ChatPresentation.Reset(); Speech.Reset(); TypeSound.Release();
        TypeGlyphRenderer.Release(); GameFace.Release(); TypeSettings.Release();
    }
}
