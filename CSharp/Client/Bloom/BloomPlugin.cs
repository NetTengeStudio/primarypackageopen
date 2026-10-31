using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Bloom {
    public sealed class BloomPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BloomPlugin), nameof(DrawMap_Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BloomPlugin), nameof(DrawMap_Postfix))),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(BloomPlugin), nameof(DrawMap_Transpiler))));
        }

        private static IEnumerable<CodeInstruction> DrawMap_Transpiler(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            var target = AccessTools.Method(typeof(GraphicsDevice), nameof(GraphicsDevice.SetRenderTarget),
                new[] { typeof(RenderTarget2D) });
            int anchor = -1;
            for (int i = 0; i < code.Count; i++) {
                if (i > 0 && code[i].opcode == OpCodes.Callvirt && ReferenceEquals(code[i].operand, target)
                    && code[i - 1].opcode == OpCodes.Ldnull) { anchor = i; }
            }
            if (anchor < 0) {
                PrimaryPackage.Core.ModLog.Error("bloom render target hook does not apply");
                return code;
            }
            code.InsertRange(anchor + 1, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BloomPlugin), nameof(BuildNow)))
            });
            BloomChain.SameFrameSource = true;
            return code;
        }

        private static void BuildNow(GameScreen screen, GraphicsDevice graphics, SpriteBatch spriteBatch) {
            BloomChain.BuildChain(screen, graphics, spriteBatch);
            try { graphics.SetRenderTarget(null); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("BloomPlugin.1", noted); }
        }

        private static void DrawMap_Prefix(GameScreen __instance, GraphicsDevice graphics, SpriteBatch spriteBatch) {
            try { ExplosionFlash.Update(); } catch (Exception) { ExplosionFlash.Disable(); }
            if (!BloomChain.SameFrameSource) { BloomChain.BuildChain(__instance, graphics, spriteBatch); }
        }

        private static void DrawMap_Postfix(GameScreen __instance, GraphicsDevice graphics, SpriteBatch spriteBatch) {
            ShockWave.Draw(graphics, BloomChain.Source(__instance));
            BloomChain.Composite(__instance, spriteBatch);
        }



        public void Release() {
            BloomChain.Release();
        }
    }
}
