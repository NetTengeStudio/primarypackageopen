using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Core;

public static class SceneTarget {
    private static readonly AccessTools.FieldRef<GameScreen, RenderTarget2D> composed =
        AccessTools.FieldRefAccess<GameScreen, RenderTarget2D>("renderTargetFinal");

    public static RenderTarget2D Scene(GameScreen screen) => screen == null ? null : composed(screen);
}
