using System;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Fault;

namespace PrimaryPackage.Fault;

internal static class RenderRepair {
    private static readonly FieldInfo BatchField = AccessTools.Field(typeof(GameMain), "spriteBatch");
    private static readonly FieldInfo BeginCalled = AccessTools.Field(typeof(SpriteBatch), "_beginCalled");

    public static Exception DrawFinalizer(Exception __exception, System.Reflection.MethodBase __originalMethod) {
        if (__exception == null) { return null; }
        Exception outcome = FaultHooks.ContainBoundary(__originalMethod, __exception);
        if (outcome == null) { Recover(); }
        return outcome;
    }

    private static void Recover() {
        try {
            if (BatchField?.GetValue(null) is SpriteBatch batch && BeginCalled?.GetValue(batch) is true) {
                batch.End();
            }
        } catch (Exception noted) {
            ModFault.Note("RenderRepair.End", noted);
        }
        try {
            GameMain.Instance?.GraphicsDevice?.SetRenderTarget(null);
        } catch (Exception noted) {
            ModFault.Note("RenderRepair.Target", noted);
        }
    }
}
