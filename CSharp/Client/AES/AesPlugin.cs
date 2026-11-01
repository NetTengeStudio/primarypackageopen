using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Aes {
    public sealed class AesPlugin : PrimaryPackage.Core.IModule {
        private Harmony harmony;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            harmony.Patch(
                AccessTools.Method(typeof(GUIContextMenu), nameof(GUIContextMenu.CreateContextMenu), new[] { typeof(ContextMenuOption[]) }),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(ContextMenu_Prefix)));
            harmony.Patch(
                AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateSelecting)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(SuppressEditorInput_Prefix)));
            harmony.Patch(
                AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateEditor)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(SuppressEditorInput_Prefix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "Update"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(EditorUpdate_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "Draw"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(EditorDraw_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "CreateUI"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(CreateUI_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.Select), System.Type.EmptyTypes),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(CreateUI_Postfix)));
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(Item), nameof(Item.SelectableInEditor)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(AesPlugin), nameof(SelectableInEditor_Postfix)));
            AesItemReset.Register();
        }


        public void Release() {
            AesItemReset.Unregister();
        }

        private static void ContextMenu_Prefix(ref ContextMenuOption[] options) {
            if (!SubEditorScreen.IsSubEditor() || PlayerInput.IsShiftDown()) { return; }
            Item target = GetContextTarget();
            if (target == null) { return; }
            int actionIndex = AesRegistry.FindAction(target);
            if (actionIndex < 0) { return; }

            var extra = new ContextMenuOption(AesRegistry.Actions[actionIndex].Label, isEnabled: true,
                onSelected: () => AesRegistry.Invoke(actionIndex, target));
            var arr = new ContextMenuOption[options.Length + 1];
            Array.Copy(options, arr, options.Length);
            arr[options.Length] = extra;
            options = arr;
        }

        private static Item GetContextTarget() {
            bool useHighlighted = false;
            foreach (MapEntity me in MapEntity.HighlightedEntities) {
                if (!MapEntity.SelectedList.Contains(me)) { useHighlighted = true; break; }
            }
            MapEntity single = null;
            int count = 0;
            if (useHighlighted) {
                foreach (MapEntity me in MapEntity.HighlightedEntities) { single = me; if (++count > 1) { return null; } }
            } else {
                foreach (MapEntity me in MapEntity.SelectedList) { single = me; if (++count > 1) { return null; } }
            }
            return count == 1 ? single as Item : null;
        }

        private static bool SuppressEditorInput_Prefix() => !AesZoneSelector.Active;

        private static void EditorUpdate_Postfix() => AesZoneSelector.Update();

        private static void EditorDraw_Postfix(SpriteBatch spriteBatch) => AesZoneSelector.Draw(spriteBatch);

        private static void CreateUI_Postfix(SubEditorScreen __instance) => AesAuxSystems.BuildPanel(__instance.showEntitiesPanel);

        private static void SelectableInEditor_Postfix(Item __instance, ref bool __result) {
            if (__result && AesAuxSystems.ShouldHideFromSelection(__instance)) { __result = false; }
        }
    }
}
