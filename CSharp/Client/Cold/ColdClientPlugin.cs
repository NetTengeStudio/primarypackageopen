using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Cold {
    public sealed class ColdClientPlugin : PrimaryPackage.Core.IModule {
        private Harmony harmony;

        private static int coldDebugRevision = -1;
        private static int coldVisualRevision = -1;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            ColdClientNet.Hook();
            PrimaryPackage.Core.HudFrame.Under(PrimaryPackage.Core.HudFrame.Chill, GuiDrawPrefix);
            harmony.Patch(
                AccessTools.Method(typeof(GUI), "DrawCursor"),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdClientPlugin), nameof(DrawCursorPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateSelecting)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdClientPlugin), nameof(SuppressEditorInput)));
            harmony.Patch(
                AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateEditor)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdClientPlugin), nameof(SuppressEditorInput)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "Update"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdClientPlugin), nameof(EditorUpdatePostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "Draw"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdClientPlugin), nameof(EditorDrawPostfix)));
        }



        public void Release() {
            ColdDebug.Disable();
            ColdDebugMap.Release();
            ColdVisuals.Dispose();
        }

        public static void GuiDrawPrefix(Camera cam, SpriteBatch spriteBatch) => ColdVisuals.Draw(spriteBatch);

        public static void DrawCursorPrefix(SpriteBatch spriteBatch) {
            PollConfig();
            if (!ColdDebug.Enabled) { return; }
            if (GameMain.GameSession == null || Screen.Selected != GameMain.GameScreen) {
                ColdDebug.Reset();
                return;
            }
            ColdDebugMap.Draw(spriteBatch);
            ColdDebug.DrawPanel(spriteBatch);
        }

        private static void PollConfig() {
            int debugRevision = PrimaryPackage.Core.ModSettings.ColdDebug.Revision;
            if (debugRevision != coldDebugRevision) {
                coldDebugRevision = debugRevision;
                if (ColdDebug.Enabled) {
                    ColdDebug.Reset();
                } else {
                    ColdDebug.Disable();
                    ColdDebugMap.Release();
                }
            }
            int visualRevision = PrimaryPackage.Core.ModSettings.ColdEffects.Revision;
            if (visualRevision == coldVisualRevision) { return; }
            coldVisualRevision = visualRevision;
            if (!ColdVisuals.Enabled) { ColdVisuals.Reset(); }
        }

        public static bool SuppressEditorInput() => !ColdHullPicker.Active;

        public static void EditorUpdatePostfix() => ColdHullPicker.Update();

        public static void EditorDrawPostfix(SpriteBatch spriteBatch) => ColdHullPicker.Draw(spriteBatch);

    }
}
