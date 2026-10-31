#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Barotrauma.LuaCs.Events;
using Barotrauma.LuaCs;

namespace PrimaryPackage.RPWheel
{
    public class RPWheelMod : PrimaryPackage.Core.IModule, IEventUpdate
    {

        public static RPWheelMod?      Instance     { get; private set; }
        internal static EmoteDisplay?  EmoteDisplay { get; private set; }
        internal static string ModDir   { get; private set; } = "";

        private WheelOverlay?  _overlay;
        private bool           _wasHotkeyDown;
        private bool           _wasEscDown;
        private readonly List<DebugConsole.Command> _consoleCommands = new();


        public void RoundStart() { }

        public void Attach(Harmony harmony)
        {
            Instance     = this;
            EmoteDisplay = new EmoteDisplay();
            ModDir       = PrimaryPackage.Core.ModAssets.Resolve("Content/Systems/RPWheel");
            Theme.Initialize();
            LoadCatalog();
            RpUtil.PatchNamespace(harmony);
            RPWheelNet.Register();
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventUpdate>(this);
            RegisterConsoleCommands();
        }


        public void Release()
        {
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
            CharacterControlPatch.Reset();
            _overlay?.Cleanup();
            _overlay = null;
            EmoteDisplay = null;
            EmoteAudio.DisposeAll();
            Theme.Dispose();
            _consoleCommands.ForEach(PrimaryPackage.Core.ModCommands.Drop);
            _consoleCommands.Clear();
            Instance = null;
        }

        internal static void LoadCatalog()
        {
            EmoteCatalog.Load();
            EmoteAudio.LoadCatalog(PrimaryPackage.Core.ModAssets.Root);
        }

        public static bool IsWheelOpen => Instance?._overlay is { IsOpen: true };

        internal void DrawOverlay(SpriteBatch spriteBatch) => _overlay?.Draw(spriteBatch);

        public void OnUpdate(double fixedDeltaTime)
        {
            try { Tick((float)fixedDeltaTime); }
            catch (Exception e) { LogError($"Tick: {e.GetType().Name}: {e.Message}"); }
        }

        private void Tick(float dt)
        {
            if (Screen.Selected != GameMain.GameScreen) return;
            var controlled = Character.Controlled;
            if (controlled == null || controlled.IsDead || controlled.IsUnconscious || EmoteCatalog.Count == 0)
            {
                if (IsWheelOpen) CloseWheel();
                EmoteDisplay?.Tick(dt);
                return;
            }
            bool hotkey = !PrimaryPackage.Core.PlayView.Typing && PrimaryPackage.Core.ModSettings.RPWheelKey.DownUnfiltered();
            bool esc    = PlayerInput.GetKeyboardState.IsKeyDown(Keys.Escape);
            if (hotkey && !_wasHotkeyDown && _overlay is not { IsOpen: true }) OpenWheel();
            _overlay?.Tick(dt, hotkey, esc && !_wasEscDown);
            EmoteDisplay?.Tick(dt);
            _wasHotkeyDown = hotkey;
            _wasEscDown    = esc;
        }

        private void OpenWheel()
        {
            try { (_overlay ??= new WheelOverlay(EmoteCatalog.Defs, CloseWheel)).Open(); }
            catch (Exception ex) { LogError($"OpenWheel: {ex.GetType().Name}: {ex.Message}"); }
        }

        public void CloseWheel() => _overlay?.Close();

        internal static void ReloadCatalog()
        {
            LoadCatalog();
            if (Instance?._overlay != null) { Instance._overlay.Cleanup(); Instance._overlay = null; }
        }

        private void RegisterConsoleCommands()
        {
            var reloadCmd = new DebugConsole.Command("rpwheel_reload|rpr", "Reload RPWheel emote catalog", _ =>
            {
                ReloadCatalog();
                DebugConsole.NewMessage($"rpwheel - перезагружено эмоций {EmoteCatalog.Count}", Color.Cyan);
            });
            _consoleCommands.Add(reloadCmd);
            PrimaryPackage.Core.ModCommands.Adopt(reloadCmd);

        }

        internal static void Log(string msg, Color? color = null)
            => LuaCsLogger.LogMessage($"[RPWheel] {msg}", color ?? Color.Cyan, color ?? Color.Cyan);

        internal static void LogError(string msg)
            => LuaCsLogger.LogError($"[RPWheel] {msg}");
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ShowSpeechBubble), new[] { typeof(Color), typeof(string) })]
    internal static class SpeechBubbleEmotePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Character __instance, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string trimmed = text.Trim();
            if (trimmed.Length < 3 || trimmed[0] != '*' || trimmed[^1] != '*') return true;
            RPWheelMod.EmoteDisplay?.Show(__instance, trimmed[1..^1]);
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), "DetermineMouseOnInventory")]
    internal static class InventoryPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (RPWheelMod.IsWheelOpen) { __result = false; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(Character), "DoInteractionUpdate")]
    internal static class CharacterInteractionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Character __instance)
        {
            if (RPWheelMod.IsWheelOpen && __instance == Character.Controlled)
            {
                __instance.FocusedCharacter = null;
                __instance.FocusedItem      = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GUI), "DrawCursor")]
    internal static class DrawCursorPatch
    {
        [HarmonyPrefix]
        public static void Prefix(SpriteBatch spriteBatch)
        {
            bool hasEmotes  = RPWheelMod.EmoteDisplay?.HasEntries ?? false;
            if (!RPWheelMod.IsWheelOpen && !hasEmotes) return;

            spriteBatch.End();
            try
            {
                if (hasEmotes)  RPWheelMod.EmoteDisplay?.Draw(spriteBatch);
                if (RPWheelMod.IsWheelOpen)
                {
                    GUI.HideCursor = true;
                    RPWheelMod.Instance?.DrawOverlay(spriteBatch);
                }
            }
            finally
            {
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                    GUI.SamplerState, null, GameMain.ScissorTestEnable);
            }
        }
    }

    [HarmonyPatch(typeof(Character), "Control")]
    internal static class CharacterControlPatch
    {
        private static Direction? _frozenDir;

        [HarmonyPrefix]
        public static void Prefix(Character __instance)
        {
            if (__instance != Character.Controlled) return;

            if (RPWheelMod.IsWheelOpen && __instance.AnimController != null)
            {
                _frozenDir ??= __instance.AnimController.TargetDir;
                __instance.AnimController.TargetDir = _frozenDir.Value;
            }
            else
            {
                _frozenDir = null;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(Character __instance)
        {
            if (__instance != Character.Controlled) return;
            if (RPWheelMod.IsWheelOpen && __instance.AnimController != null && _frozenDir.HasValue)
                __instance.AnimController.TargetDir = _frozenDir.Value;
        }

        internal static void Reset() => _frozenDir = null;
    }

    [HarmonyPatch(typeof(Character), "UpdateLocalCursor")]
    internal static class CharacterUpdateLocalCursorPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Character __instance)
        {
            if (!RPWheelMod.IsWheelOpen || __instance != Character.Controlled) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput))]
    internal static class PlayerInputMousePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("PrimaryMouseButtonHeld")]      [HarmonyPatch("PrimaryMouseButtonDown")]
        [HarmonyPatch("PrimaryMouseButtonReleased")]  [HarmonyPatch("PrimaryMouseButtonClicked")]
        [HarmonyPatch("SecondaryMouseButtonHeld")]    [HarmonyPatch("SecondaryMouseButtonDown")]
        [HarmonyPatch("SecondaryMouseButtonReleased")][HarmonyPatch("SecondaryMouseButtonClicked")]
        [HarmonyPatch("MidButtonClicked")]            [HarmonyPatch("MidButtonHeld")]
        [HarmonyPatch("Mouse4ButtonClicked")]         [HarmonyPatch("Mouse4ButtonHeld")]
        [HarmonyPatch("Mouse5ButtonClicked")]         [HarmonyPatch("Mouse5ButtonHeld")]
        [HarmonyPatch("MouseWheelUpClicked")]         [HarmonyPatch("MouseWheelDownClicked")]
        [HarmonyPatch("DoubleClicked")]               [HarmonyPatch("SecondaryDoubleClicked")]
        public static bool Prefix(ref bool __result)
        {
            if (!RPWheelMod.IsWheelOpen) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), "ScrollWheelSpeed", MethodType.Getter)]
    internal static class ScrollWheelPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref int __result)
        {
            if (RPWheelMod.IsWheelOpen) { __result = 0; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerInput))]
    internal static class PlayerInputKeyPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("KeyHit",  new[] { typeof(InputType) })]
        [HarmonyPatch("KeyDown", new[] { typeof(InputType) })]
        [HarmonyPatch("KeyUp",   new[] { typeof(InputType) })]
        public static bool Prefix(InputType inputType, ref bool __result)
        {
            if (!RPWheelMod.IsWheelOpen) return true;
            bool isMovement = inputType is InputType.Up or InputType.Down or InputType.Left or InputType.Right
                                       or InputType.Run or InputType.ToggleRun or InputType.Crouch;
            if (isMovement) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), "InventoryKeyHit")]
    internal static class InventoryKeyHitPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (RPWheelMod.IsWheelOpen) { __result = false; return false; }
            return true;
        }
    }
}
