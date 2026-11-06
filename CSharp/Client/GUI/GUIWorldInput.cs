using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
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
        // Native hover only suppresses focusing in the engine. Skipping the whole
        // interaction update also skips ladder traversal and deselection.
        if (__instance != Character.Controlled || !GUIBridge.CapturesCustomViewInput) { return true; }
        __instance.FocusedCharacter = null;
        __instance.FocusedItem = null;
        return false;
    }
}

[HarmonyPatch(typeof(Character), "UpdateLocalCursor")]
internal static class GUICharacterCursor {
    [HarmonyPrefix]
    public static bool Prefix(Character __instance) => !GUIBridge.CapturesCharacterInput(__instance);
}

// The engine owns the retained follow offset. UI capture freezes that offset,
// while target following, smoothing, shake and interpolation continue normally.
[HarmonyPatch(typeof(Camera), nameof(Camera.MoveCamera))]
internal static class GUICameraInput {
    [HarmonyPrefix]
    public static bool Prefix(Camera __instance, float deltaTime, ref bool allowMove, ref bool allowZoom, ref bool allowInput) {
        if (Screen.Selected != GameMain.GameScreen) { return true; }
        if (__instance == GameMain.GameScreen.Cam && GUIRuntime.Instance?.MoveOverlayCamera(__instance, deltaTime) == true) { return false; }
        if (!GUIBridge.CapturesViewInput) { return true; }
        allowZoom = false;
        allowInput = false;
        __instance.Freeze = true;
        if (__instance.TargetPos == Vector2.Zero) { allowMove = false; }

        return true;
    }

    // Native Freeze only restores previousOffset when the newly calculated
    // pointer offset is nonzero. At the screen centre (or OffsetAmount == 0)
    // that still recentres the view. Select the retained offset at its source.
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo amount = AccessTools.PropertyGetter(typeof(Camera), nameof(Camera.OffsetAmount));
        MethodInfo multiply = AccessTools.Method(typeof(Vector2), "op_Multiply", new[] { typeof(Vector2), typeof(float) });
        MethodInfo followOffset = AccessTools.Method(typeof(GUICameraInput), nameof(FollowOffset));
        int matches = 0;
        for (int i = 1; i < code.Count; i++) {
            if (!code[i - 1].Calls(amount) || !code[i].Calls(multiply)) { continue; }
            CodeInstruction operation = code[i];
            code.Insert(i, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(operation).MoveBlocksFrom(operation));
            operation.operand = followOffset;
            matches++;
            i++;
        }
        if (matches != 1) { throw new InvalidOperationException($"Expected one Camera follow-offset calculation, found {matches}"); }
        return code;
    }

    private static Vector2 FollowOffset(Vector2 direction, float amount, Camera camera) =>
        camera.Freeze ? camera.previousOffset : direction * amount;
}

[HarmonyPatch(typeof(Character), nameof(Character.IsMouseOnUI), MethodType.Getter)]
internal static class GUICharacterUI {
    [HarmonyPostfix]
    public static void Postfix(ref bool __result) => __result |= GUIBridge.CapturesCustomViewInput;
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
        if (!GUIWorldInputScope.Active || !GUIBridge.BlocksWorld) { return true; }
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerInput), "ScrollWheelSpeed", MethodType.Getter)]
internal static class GUIWorldScroll {
    [HarmonyPrefix]
    public static bool Prefix(ref int __result) {
        if (!GUIWorldInputScope.Active || !GUIBridge.BlocksWorld) { return true; }
        __result = 0;
        return false;
    }
}

// PlayerInput is shared by native widgets and the world. Only world update
// consumers may mask its pointer; GUI.Update and menu hotkeys keep raw input.
[HarmonyPatch]
internal static class GUIWorldInputScope {
    [ThreadStatic] private static int depth;
    internal static bool Active => depth != 0;

    private static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer));
        yield return AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.Update));
        yield return AccessTools.Method(typeof(Inventory), nameof(Inventory.Update));
    }

    [HarmonyPrefix]
    internal static void Prefix(out int __state) { __state = depth; depth++; }

    internal static int Suspend() { int previous = depth; depth = 0; return previous; }

    // Finalizers run even when another patch skips the original or it throws.
    [HarmonyFinalizer]
    internal static void Finalizer(int __state) { depth = __state; }
}

[HarmonyPatch(typeof(Key), nameof(Key.SetState), new Type[0])]
internal static class GUICharacterKeys {
    [HarmonyPrefix]
    public static bool Prefix(Key __instance, InputType ___inputType, out int __state) {
        // Once classified as an allowed action, its binding must read raw input.
        // Movement and voice can be rebound to mouse buttons as well as keys.
        __state = GUIWorldInputScope.Suspend();
        if (!GUIBridge.BlocksWorld) { return true; }
        if (GUIVoiceInput.Allows(___inputType)) { return true; }
        bool movement = ___inputType is InputType.Up or InputType.Down or InputType.Left or InputType.Right or InputType.Run or InputType.ToggleRun or InputType.Crouch;
        if (movement && GUIRuntime.Instance?.Input.BlocksWorld != true && GUIRuntime.Instance?.MovementCapture != true) { return true; }
        __instance.Reset();
        return false;
    }

    [HarmonyFinalizer]
    internal static void Finalizer(int __state) => GUIWorldInputScope.Finalizer(__state);
}

internal static class GUIVoiceInput {
    private static bool TextFocused => Barotrauma.GUI.KeyboardDispatcher.Subscriber != null;
    internal static bool Allows(InputType type) => !TextFocused && type is InputType.Voice or InputType.RadioVoice or InputType.LocalVoice;
}
