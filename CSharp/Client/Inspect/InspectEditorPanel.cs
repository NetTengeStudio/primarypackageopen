using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Fonts;

namespace PrimaryPackage.Inspect {
    public static class InspectEditorPanel {
        private static MapEntity pendingRebuild;
        private static System.Reflection.MethodInfo positionEditingHud;

        public static void Inject(MapEntity entity, GUIComponent hud) {
            InspectData data = InspectRegistry.Get(entity);
            if (data == null || hud == null) { return; }
            GUIListBox listBox = hud.GetChild<GUIListBox>();
            if (listBox == null) { return; }

            var editor = new SerializableEntityEditor(listBox.Content.RectTransform, data, inGame: false, showName: true,
                titleFont: GUIStyle.SubHeadingFont, dimOutDefaultValues: false) { UserData = data };
            InspectFields.ApplyMode(editor, data.Mode);
            FontFieldUI.Apply(editor, data);

            if (editor.Fields.TryGetValue("Mode".ToIdentifier(), out GUIComponent[] modeFields) &&
                modeFields.Length > 0 && modeFields[0] is GUIDropDown dropDown) {
                GUIDropDown.OnSelectedHandler previous = dropDown.OnSelected;
                dropDown.OnSelected = (component, userData) => {
                    bool result = previous == null || previous(component, userData);
                    pendingRebuild = entity;
                    return result;
                };
            }

            var removeButton = new GUIButton(new RectTransform(new Point(listBox.Content.Rect.Width, (int)(22 * GUI.Scale))),
                TextManager.Get("pp_inspect.remove").Fallback("Убрать осмотр"), style: "GUIButtonSmall");
            removeButton.OnClicked = (button, userData) => {
                InspectRegistry.Detach(entity);
                pendingRebuild = entity;
                return true;
            };
            editor.AddCustomContent(removeButton, editor.ContentCount);
            Reflow(listBox, editor);
        }

        private static void Reflow(GUIListBox listBox, SerializableEntityEditor editor) {
            editor.Recalculate();
            listBox.RecalculateChildren();
            positionEditingHud ??= AccessTools.Method(typeof(MapEntity), "PositionEditingHUD");
            positionEditingHud?.Invoke(null, null);
            listBox.UpdateScrollBarSize();
        }

        public static void Update() {
            if (pendingRebuild == null) { return; }
            MapEntity entity = pendingRebuild;
            pendingRebuild = null;
            if (entity.Removed || !entity.IsSelected) { return; }
            (entity as Item)?.CreateEditingHUD();
            (entity as Structure)?.CreateEditingHUD();
        }
    }

    public sealed class InspectEditorPlugin : PrimaryPackage.Core.IModule {
        private Harmony harmony;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            harmony.Patch(
                AccessTools.Method(typeof(Item), nameof(Item.CreateEditingHUD)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(InspectEditorPlugin), nameof(EditingHud_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(Structure), nameof(Structure.CreateEditingHUD)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(InspectEditorPlugin), nameof(EditingHud_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "Update"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(InspectEditorPlugin), nameof(EditorUpdate_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(SubEditorScreen), "Draw"),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(InspectEditorPlugin), nameof(EditorDraw_Postfix)));
            harmony.Patch(
                AccessTools.Method(typeof(PlayerInput), nameof(PlayerInput.KeyHit), new[] { typeof(Keys) }),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(InspectEditorPlugin), nameof(KeyHit_Prefix)));
        }


        public void Release() {
            InspectRing.Dispose();
        }

        public static void EditingHud_Postfix(MapEntity __instance, GUIComponent __result) =>
            InspectEditorPanel.Inject(__instance, __result);

        public static void EditorUpdate_Postfix() {
            InspectHold.Update();
            InspectEditorPanel.Update();
        }

        public static void EditorDraw_Postfix(SpriteBatch spriteBatch) => InspectHold.Draw(spriteBatch);

        public static bool KeyHit_Prefix(Keys button, ref bool __result) {
            if (button != Keys.F || Screen.Selected != GameMain.SubEditorScreen) { return true; }
            __result = false;
            return false;
        }
    }
}
