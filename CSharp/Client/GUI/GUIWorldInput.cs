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
    public static bool Prefix(Character __instance) => !GUIBridge.BlocksWorld || __instance != Character.Controlled;
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
    public static bool Prefix(ref bool __result) {
        if (!GUIBridge.BlocksWorld) { return true; }
        __result = false;
        return false;
    }
}
