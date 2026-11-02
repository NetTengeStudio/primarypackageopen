using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;

namespace PrimaryPackage.GUI;

[HarmonyPatch(typeof(Inventory), "DetermineMouseOnInventory")]
internal static class GUIInventoryInput {
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result) {
        if (!GUIBridge.BlocksWorld) { return true; }
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Character), "DoInteractionUpdate")]
internal static class GUICharacterInteraction {
    [HarmonyPrefix]
    public static bool Prefix(Character __instance) {
        if (!GUIBridge.BlocksWorld || __instance != Character.Controlled) { return true; }
        __instance.FocusedCharacter = null;
        __instance.FocusedItem = null;
        return false;
    }
}

[HarmonyPatch(typeof(Character), "UpdateLocalCursor")]
internal static class GUICharacterCursor {
    [HarmonyPrefix]
    public static bool Prefix(Character __instance) => !GUIBridge.CapturesViewInput || __instance != Character.Controlled;
}

// Camera reads raw pointer coordinates in both follow and spectator modes. Masking
// ScrollWheelSpeed alone does not stop mouse-look zoom or the follow-camera offset.
[HarmonyPatch(typeof(Camera), nameof(Camera.MoveCamera))]
internal static class GUICameraInput {
    [HarmonyPrefix]
    public static void Prefix(Camera __instance, ref bool allowMove, ref bool allowZoom, ref bool allowInput) {
        if (Screen.Selected != GameMain.GameScreen || !GUIBridge.CapturesViewInput) { return; }
        allowZoom = false;
        allowInput = false;
        if (__instance.TargetPos == Microsoft.Xna.Framework.Vector2.Zero) { allowMove = false; }
        else { __instance.Freeze = true; }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.IsMouseOnUI), MethodType.Getter)]
internal static class GUICharacterUI {
    [HarmonyPostfix]
    public static void Postfix(ref bool __result) => __result |= GUIRuntime.Instance?.CapturesAim == true;
}

[HarmonyPatch(typeof(PlayerInput))]
internal static class GUIWorldMouse {
    [HarmonyPrefix]
    [HarmonyPatch("PrimaryMouseButtonHeld")]
    [HarmonyPatch("PrimaryMouseButtonDown")]
    [HarmonyPatch("PrimaryMouseButtonReleased")]
    [HarmonyPatch("PrimaryMouseButtonClicked")]
    [HarmonyPatch("SecondaryMouseButtonHeld")]
    [HarmonyPatch("SecondaryMouseButtonDown")]
    [HarmonyPatch("SecondaryMouseButtonReleased")]
    [HarmonyPatch("SecondaryMouseButtonClicked")]
    [HarmonyPatch("MidButtonClicked")]
    [HarmonyPatch("MidButtonHeld")]
    [HarmonyPatch("Mouse4ButtonClicked")]
    [HarmonyPatch("Mouse4ButtonHeld")]
    [HarmonyPatch("Mouse5ButtonClicked")]
    [HarmonyPatch("Mouse5ButtonHeld")]
    [HarmonyPatch("MouseWheelUpClicked")]
    [HarmonyPatch("MouseWheelDownClicked")]
    [HarmonyPatch("DoubleClicked")]
    [HarmonyPatch("SecondaryDoubleClicked")]
    [HarmonyPatch("InventoryKeyHit")]
    public static bool Prefix(ref bool __result) {
        if (!GUIBridge.BlocksWorld) { return true; }
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerInput), "ScrollWheelSpeed", MethodType.Getter)]
internal static class GUIWorldScroll {
    [HarmonyPrefix]
    public static bool Prefix(ref int __result) {
        if (!GUIBridge.BlocksWorld) { return true; }
        __result = 0;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerInput))]
internal static class GUIWorldKeyboard {
    [HarmonyPrefix]
    [HarmonyPatch("KeyHit", new[] { typeof(InputType) })]
    [HarmonyPatch("KeyDown", new[] { typeof(InputType) })]
    [HarmonyPatch("KeyUp", new[] { typeof(InputType) })]
    public static bool Prefix(InputType inputType, ref bool __result) {
        if (!GUIBridge.BlocksWorld) { return true; }
        if (GUIVoiceInput.Allows(inputType)) { return true; }
        bool movement = inputType is InputType.Up or InputType.Down or InputType.Left or InputType.Right or InputType.Run or InputType.ToggleRun or InputType.Crouch;
        if (movement && GUIRuntime.Instance?.Input.BlocksWorld != true) { return true; }
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerInput))]
internal static class GUIWorldKeys {
    [HarmonyPrefix]
    [HarmonyPatch("KeyHit", new[] { typeof(Microsoft.Xna.Framework.Input.Keys) })]
    [HarmonyPatch("KeyDown", new[] { typeof(Microsoft.Xna.Framework.Input.Keys) })]
    [HarmonyPatch("KeyUp", new[] { typeof(Microsoft.Xna.Framework.Input.Keys) })]
    public static bool Prefix(Microsoft.Xna.Framework.Input.Keys __0, ref bool __result) {
        if (!GUIBridge.BlocksWorld || GUIVoiceInput.Allows(__0)) { return true; }
        __result = false;
        return false;
    }
}

internal static class GUIVoiceInput {
    private static bool TextFocused => Barotrauma.GUI.KeyboardDispatcher.Subscriber != null;
    internal static bool Allows(InputType type) => !TextFocused && type is InputType.Voice or InputType.RadioVoice or InputType.LocalVoice;
    internal static bool Allows(Microsoft.Xna.Framework.Input.Keys key) {
        if (TextFocused || key == Microsoft.Xna.Framework.Input.Keys.None) { return false; }
        var bindings = GameSettings.CurrentConfig.KeyMap.Bindings;
        return bindings[InputType.Voice].Key == key || bindings[InputType.RadioVoice].Key == key || bindings[InputType.LocalVoice].Key == key;
    }
}
