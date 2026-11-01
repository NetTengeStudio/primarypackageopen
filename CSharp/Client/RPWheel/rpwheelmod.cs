#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Barotrauma.LuaCs.Events;
using Barotrauma.LuaCs;

namespace PrimaryPackage.RPWheel
{
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public class RPWheelMod : PrimaryPackage.Core.IModule, IEventUpdate
    {

        public static RPWheelMod?      Instance     { get; private set; }
        internal static EmoteDisplay?  EmoteDisplay { get; private set; }
        internal static string ModDir   { get; private set; } = "";

        private WheelOverlay?  _overlay;
        private WheelModel?    _model;
        private Character?     _owner;
        private bool           _wasHotkeyDown;
        private bool           _wasEscDown;
        private PrimaryPackage.GUI.GUIOverlay? wheelLayer, emoteLayer;
        private readonly List<DebugConsole.Command> _consoleCommands = new();


        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void RoundStart() => ResetWheel();

        public void Attach(Harmony harmony)
        {
            Instance     = this;
            EmoteDisplay = new EmoteDisplay();
            ModDir       = PrimaryPackage.Core.ModAssets.Resolve("Content/Systems/RPWheel");
            Theme.Initialize();
            LoadCatalog();
            RebuildWheel();
            wheelLayer = PrimaryPackage.GUI.GUIRuntime.Instance.RegisterOverlay(() => IsWheelOpen, DrawOverlay, ResetWheel, blocksWorld: true, hidesCursor: true);
            emoteLayer = PrimaryPackage.GUI.GUIRuntime.Instance.RegisterOverlay(() => PrimaryPackage.Core.PlayView.Running && EmoteDisplay?.HasEntries == true, batch => EmoteDisplay?.Draw(batch));
            RpUtil.PatchNamespace(harmony);
            RPWheelNet.Register();
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventUpdate>(this);
            RegisterConsoleCommands();
        }


        public void Release()
        {
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
            ResetWheel();
            wheelLayer?.Dispose();
            emoteLayer?.Dispose();
            _overlay?.Dispose();
            _model?.Dispose();
            wheelLayer = emoteLayer = null;
            _overlay = null;
            _model = null;
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

        public static bool IsWheelOpen => Instance?._model is { Visible: true };

        internal void DrawOverlay(SpriteBatch spriteBatch) {
            double now = Now;
            Vector2 mouse = PlayerInput.LatestMousePosition;
            _model?.Pointer(now, mouse.X - GameMain.GraphicsWidth * 0.5f,
                mouse.Y - GameMain.GraphicsHeight * 0.5f, GUI.Scale);
            _overlay?.Draw(spriteBatch, now, mouse);
        }

        public void OnUpdate(double fixedDeltaTime)
        {
            try { Tick((float)fixedDeltaTime); }
            catch (Exception e) { ResetWheel(); LogError($"Tick: {e.GetType().Name}: {e.Message}"); }
        }

        private void Tick(float dt)
        {
            double now = Now;
            bool hotkey = PrimaryPackage.Core.ModSettings.RPWheelKey.DownUnfiltered();
            bool esc = PlayerInput.GetKeyboardState.IsKeyDown(Keys.Escape);
            var controlled = Character.Controlled;
            bool available = Screen.Selected == GameMain.GameScreen && GameMain.WindowActive &&
                controlled != null && !controlled.Removed && !controlled.IsDead && !controlled.IsUnconscious &&
                PrimaryPackage.GUI.GUIRuntime.Instance?.Input.BlocksWorld != true &&
                !PrimaryPackage.Core.PlayView.Typing && EmoteCatalog.Count > 0;
            if ((!available || _owner != controlled) && (_owner != null || IsWheelOpen)) { ResetWheel(); }
            _owner = available ? controlled : null;
            if (available && _model != null) {
                if (hotkey && !_wasHotkeyDown) { _model.Open(now); }
                Vector2 mouse = PlayerInput.MousePosition;
                _model.Pointer(now, mouse.X - GameMain.GraphicsWidth * 0.5f,
                    mouse.Y - GameMain.GraphicsHeight * 0.5f, GUI.Scale);
                int selected = _model.Input(now, hotkey, esc && !_wasEscDown);
                if (selected >= 0) { EmoteNet.Send((byte)selected); }
            }
            if (Screen.Selected == GameMain.GameScreen) { EmoteDisplay?.Tick(dt); }
            _wasHotkeyDown = hotkey;
            _wasEscDown    = esc;
        }

        public void CloseWheel() => _model?.Close(Now);

        private void ResetWheel() {
            _model?.Reset();
            _owner = null;
            CharacterControlPatch.Reset();
        }

        private void RebuildWheel() {
            ResetWheel();
            _overlay?.Dispose();
            _model?.Dispose();
            _overlay = null;
            _model = new WheelModel(EmoteCatalog.Count);
            _overlay = new WheelOverlay(EmoteCatalog.Defs, _model);
        }

        internal static void ReloadCatalog()
        {
            LoadCatalog();
            Instance?.RebuildWheel();
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
    [HarmonyPriority(Priority.First)]
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










}
