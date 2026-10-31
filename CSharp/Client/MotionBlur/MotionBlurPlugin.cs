using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.MotionBlur;

public sealed class MotionBlurPlugin : PrimaryPackage.Core.IModule {
    private static readonly MotionScan scan = new MotionScan();

    public void Attach(Harmony harmony) {
        harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap)),
            transpiler: PrimaryPackage.Core.ModuleHost.Bind(typeof(MotionBlurPlugin), nameof(DrawMap_Transpiler)));
    }

    public void RoundStart() => scan.Reset();

    public void Release() {
        BlurPass.Release();
        scan.Reset();
    }

    private static IEnumerable<CodeInstruction> DrawMap_Transpiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> code = new List<CodeInstruction>(instructions);
        MethodInfo inserted = AccessTools.Method(typeof(MotionBlurPlugin), nameof(Smear));
        MethodInfo bind = AccessTools.Method(typeof(GraphicsDevice), nameof(GraphicsDevice.SetRenderTarget),
            new[] { typeof(RenderTarget2D) });
        int anchor = -1;
        for (int i = 0; i < code.Count; i++) {
            if (code[i].opcode == OpCodes.Call && inserted.Equals(code[i].operand)) { return code; }
            if (i > 0 && code[i].opcode == OpCodes.Callvirt && ReferenceEquals(code[i].operand, bind)
                && code[i - 1].opcode == OpCodes.Ldnull) { anchor = i - 1; }
        }
        if (anchor < 0) {
            SafeLog();
            return code;
        }
        CodeInstruction head = new CodeInstruction(OpCodes.Ldarg_0);
        head.labels.AddRange(code[anchor].labels);
        code[anchor].labels.Clear();
        code.InsertRange(anchor, new[] {
            head,
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_3),
            new CodeInstruction(OpCodes.Call, inserted)
        });
        return code;
    }

    private static void SafeLog() {
        try { PrimaryPackage.Core.ModLog.Error("motion blur anchor does not apply"); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("MotionBlurPlugin.1", noted); }
    }

    private static void Smear(GameScreen screen, GraphicsDevice graphics, double seconds) {
        if (!MotionBlurPrefs.Enabled) { return; }
        RenderTarget2D scene = PrimaryPackage.Core.SceneTarget.Scene(screen);
        if (scene == null || scene.IsDisposed) { return; }
        if (!scan.Sample(screen.Cam, (float)seconds, out MotionSample motion)) { return; }
        if (scan.FrameShift >= MotionBlurPrefs.CutPixels(scan.ScreenHeight)) { return; }
        if (!MotionField.Shape(motion, MotionBlurPrefs.Shutter, MotionBlurPrefs.MinimumPixels,
            MotionBlurPrefs.SoftPixels, MotionBlurPrefs.MaximumPixels(BlurPass.Taps), out float x, out float y)) {
            return;
        }
        BlurPass.Run(graphics, scene, x, y);
    }
}
