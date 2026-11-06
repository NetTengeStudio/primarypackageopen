using System;
using System.Diagnostics;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using PrimaryPackage.Core;

namespace PrimaryPackage.GUI;

public sealed class GUIRuntime : IDisposable {
    public static GUIRuntime Instance { get; private set; }
    public GUIWindowManager Windows { get; } = new();
    public GUILayout Layout { get; } = new();
    public GUIRenderer Renderer { get; } = new();
    public GUIComponentRegistry Components { get; } = new();
    public GUIInput Input { get; }
    public GUIProfiler Profiler { get; } = new();
    private LanguageIdentifier language;
    private bool suspended;
    private Screen screen;
    private readonly List<GUIOverlay> overlays = new();
    private readonly List<GUINativeSurface> nativeSurfaces = new();
    internal bool CapturesAim {
        get {
            if (OverlayCapture) { return true; }
            var windows = Windows.Windows;
            for (int i = 0; i < windows.Count; i++) { if (!windows[i].Disposed && windows[i].Visible && !windows[i].InputTransparent) { return true; } }
            return false;
        }
    }
    internal bool OverlayCapture {
        get { for (int i = 0; i < overlays.Count; i++) { if (overlays[i].BlocksWorld && overlays[i].Visible()) { return true; } } return false; }
    }
    internal bool MovementCapture {
        get { for (int i = 0; i < overlays.Count; i++) { if (overlays[i].CapturesMovement && overlays[i].Visible()) { return true; } } return false; }
    }
    internal bool ViewportInputAvailable {
        get {
            if (Input.BlocksWorld || Barotrauma.GUI.MouseOn != null || Barotrauma.GUI.KeyboardDispatcher.Subscriber != null || Barotrauma.GUI.InputBlockingMenuOpen || DebugConsole.IsOpen) { return false; }
            var windows = Windows.Windows;
            for (int i = 0; i < windows.Count; i++) { var window = windows[i]; if (!window.Disposed && window.Visible && !window.InputTransparent && !window.AllowsViewportInputOutside) { return false; } }
            return true;
        }
    }
    internal bool MoveOverlayCamera(Camera camera, float deltaTime) {
        for (int i = overlays.Count - 1; i >= 0; i--) { var overlay = overlays[i]; if (overlay.MoveCamera != null && overlay.Visible()) { overlay.MoveCamera(camera, deltaTime); return true; } }
        return false;
    }
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public event Action Updating;

    internal GUIRuntime() {
        if (Instance != null) { throw new InvalidOperationException("GUIRuntime already mounted"); }
        Instance = this;
        screen = Screen.Selected;
        Input = new GUIInput(this);
    }
    public GUIOverlay RegisterOverlay(Func<bool> visible, Action<SpriteBatch> draw, Action cancel = null, bool blocksWorld = false, bool hidesCursor = false, bool cancelOnWindowOpen = true, bool capturesMovement = false, Action<Camera, float> moveCamera = null) {
        if ((capturesMovement || moveCamera != null) && !blocksWorld) { throw new ArgumentException("Viewport ownership requires world-input capture"); }
        var overlay = new GUIOverlay(overlays, visible, draw, cancel, blocksWorld, hidesCursor, cancelOnWindowOpen, capturesMovement, moveCamera);
        overlays.Add(overlay);
        return overlay;
    }
    public GUINativeSurface RegisterNativeSurface(Action addToUpdateList, Action close) {
        var surface = new GUINativeSurface(nativeSurfaces, addToUpdateList, close);
        nativeSurfaces.Add(surface);
        return surface;
    }
    internal void AddNativeSurfaces() { for (int i = 0; i < nativeSurfaces.Count; i++) { nativeSurfaces[i].AddToUpdateList(); } }
    internal void CancelBlockingOverlays() {
        for (int i = 0; i < overlays.Count; i++) { if (overlays[i].BlocksWorld && overlays[i].CancelOnWindowOpen && overlays[i].Visible()) { overlays[i].Cancel?.Invoke(); } }
    }
    internal void DrawOverlays(SpriteBatch batch) {
        bool opened = false;
        try {
            for (int i = 0; i < overlays.Count; i++) {
                var overlay = overlays[i];
                if (!overlay.Visible()) { continue; }
                if (!opened) { batch.End(); opened = true; }
                if (overlay.HidesCursor) { Barotrauma.GUI.HideCursor = true; }
                overlay.Draw(batch);
            }
        } finally {
            if (opened) { batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, Barotrauma.GUI.SamplerState, null, GameMain.ScissorTestEnable); }
        }
    }

    internal void Update() {
        bool active = Screen.Selected != null && GameMain.WindowActive && !Barotrauma.GUI.InputBlockingMenuOpen && !DebugConsole.IsOpen;
        if (!active) {
            if (!suspended) { Reset(); suspended = true; }
            return;
        }
        suspended = false;
        if (screen != Screen.Selected) {
            Input.Cancel();
            for (int i = 0; i < overlays.Count; i++) { overlays[i].Cancel?.Invoke(); }
            Windows.CloseForeignScreen(Screen.Selected);
            screen = Screen.Selected;
        }
        long started = Profiler.Begin();
        GUIMotion.Shared.Advance(Now);
        Windows.Collect();
        Profiler.End(GUIPhase.Motion, started);
        started = Profiler.Begin();
        bool resized = Layout.Resize(GameMain.GraphicsWidth, GameMain.GraphicsHeight, Barotrauma.GUI.Scale);
        if (Windows.Windows.Count > 0 && Renderer.Prepare(Layout.Scale)) { resized = true; }
        var currentLanguage = GameSettings.CurrentConfig.Language;
        if (language != currentLanguage) { language = currentLanguage; Renderer.InvalidateText(); resized = true; }
        var windows = Windows.Windows;
        for (int i = 0; i < windows.Count; i++) {
            var window = windows[i];
            if (resized) { window.InvalidateWindow(GUIDirty.All); }
            if ((window.WindowDirty & GUIDirty.Layout) != 0) { Layout.Arrange(window); }
        }
        Profiler.End(GUIPhase.Layout, started);
        started = Profiler.Begin();
        Input.Update();
        Updating?.Invoke();
        Profiler.End(GUIPhase.Input, started);
    }

    internal void Draw(Camera camera, SpriteBatch batch) {
        if (suspended || screen != Screen.Selected) { return; }
        long started = Profiler.Begin();
        var windows = Windows.Windows;
        for (int i = 0; i < windows.Count; i++) {
            var window = windows[i];
            if ((window.WindowDirty & GUIDirty.Layout) != 0) { Layout.Arrange(window); }
            if (window.WindowDirty != GUIDirty.None) { Renderer.Build(window, Layout.Scale); }
        }
        Profiler.End(GUIPhase.Build, started);
        started = Profiler.Begin();
        Renderer.Draw(batch, windows);
        Profiler.End(GUIPhase.Render, started);
    }

    public void Reset(bool closeNative = false) {
        for (int i = 0; i < overlays.Count; i++) { overlays[i].Cancel?.Invoke(); }
        if (closeNative) { for (int i = 0; i < nativeSurfaces.Count; i++) { nativeSurfaces[i].Close(); } }
        Input.Cancel();
        Windows.CloseAll();
        GUIMotion.Shared.FinishAll();
        Renderer.InvalidateText();
    }
    public void Dispose() {
        Reset(closeNative: true);
        Windows.Dispose();
        Input.Release();
        Components.Clear();
        Updating = null;
        overlays.Clear();
        nativeSurfaces.Clear();
        Instance = null;
    }
}

public sealed class GUIBridge : IModule {
    private GUIRuntime runtime;
    public static bool BlocksWorld => GUIRuntime.Instance?.Input.BlocksWorld == true || GUIRuntime.Instance?.OverlayCapture == true;
    internal static bool CapturesCustomViewInput => BlocksWorld || GUIRuntime.Instance?.CapturesAim == true;
    internal static bool CapturesCharacterInput(Character character) {
        if (character != Character.Controlled) { return false; }
        return Screen.Selected is SubEditorScreen editor && editor.dummyCharacter == character ? CapturesCustomViewInput : CapturesViewInput;
    }
    // Native widgets retain their raw input; only world consumers use this capture.
    internal static bool CapturesViewInput {
        get {
            if (CapturesCustomViewInput) { return true; }
            if (Barotrauma.GUI.MouseOn != null) { return true; }
            if (Barotrauma.GUI.InputBlockingMenuOpen || Barotrauma.GUI.PauseMenuOpen || Barotrauma.GUI.SettingsMenuOpen) { return true; }
            if (Barotrauma.GUI.KeyboardDispatcher.Subscriber != null) { return true; }
            if (DebugConsole.IsOpen) { return true; }
            if (CharacterInventory.DraggingItemToWorld) { return false; }
            if (Inventory.IsMouseOnInventory) { return true; }
            if (Character.IsMouseOnUI) { return true; }
            if (CharacterHealth.OpenHealthWindow != null) { return true; }
            if (CrewManager.IsCommandInterfaceOpen) { return true; }
            if (ConversationAction.IsDialogOpen) { return true; }
            if (GameSession.IsTabMenuOpen) { return true; }
            if (GameMain.GameSession?.Campaign != null && (GameMain.GameSession.Campaign.ShowCampaignUI || GameMain.GameSession.Campaign.ForceMapUI)) { return true; }
            if (Screen.Selected is SubEditorScreen) { return true; }

            Character controlled = Character.Controlled;
            if (controlled != null) {
                if (controlled.SelectedCharacter != null) { return true; }
                if (controlled.SelectedItem != null && controlled.SelectedItem.Components.Any(ic => ic?.GuiFrame != null && ic.ShouldDrawHUD(controlled))) { return true; }
            }
            return false;
        }
    }


    public void Attach(Harmony harmony) {
        runtime = new GUIRuntime();
        harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update)),
            prefix: ModuleHost.Bind(typeof(GUIBridge), nameof(UpdatePrefix)));
        harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.AddToGUIUpdateList)),
            postfix: ModuleHost.Bind(typeof(GUIBridge), nameof(UpdateListPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Barotrauma.GUI), nameof(Barotrauma.GUI.Update)),
            prefix: ModuleHost.Bind(typeof(GUIBridge), nameof(ValidatePrefix)));
        harmony.CreateClassProcessor(typeof(GUIInventoryInput)).Patch();
        harmony.CreateClassProcessor(typeof(GUICharacterInteraction)).Patch();
        harmony.CreateClassProcessor(typeof(GUICharacterCursor)).Patch();
        harmony.CreateClassProcessor(typeof(GUICameraInput)).Patch();
        harmony.CreateClassProcessor(typeof(GUICharacterUI)).Patch();
        harmony.CreateClassProcessor(typeof(GUIWorldMouse)).Patch();
        harmony.CreateClassProcessor(typeof(GUIWorldScroll)).Patch();
        harmony.CreateClassProcessor(typeof(GUIWorldInputScope)).Patch();
        harmony.CreateClassProcessor(typeof(GUICharacterKeys)).Patch();
        harmony.Patch(AccessTools.Method(typeof(Barotrauma.GUI), "DrawCursor"), prefix: ModuleHost.Bind(typeof(GUIBridge), nameof(CursorPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Barotrauma.GUI), "UpdateMouseCursorState"), prefix: ModuleHost.Bind(typeof(GUIBridge), nameof(ResolveCursor)));
        harmony.Patch(AccessTools.Method(typeof(Barotrauma.GUI), nameof(Barotrauma.GUI.UpdateMouseOn)), prefix: ModuleHost.Bind(typeof(GUIBridge), nameof(ValidateMouseCapture)));
    }
    public static void UpdatePrefix() {
        if (Screen.Selected != GameMain.GameScreen) { return; }
        UpdateEditorInput();
    }
    internal static void UpdateEditorInput() {
        // Persistent menus are appended inside GUI.Update, after the world's update.
        // Resolving hover against the earlier world-only list steals their pressed
        // button capture before GUI.Update can see the menu again.
        if (!GameMain.WindowActive || Barotrauma.GUI.InputBlockingMenuOpen || DebugConsole.IsOpen) {
            GUIRuntime.Instance?.Update();
            return;
        }
        Barotrauma.GUI.RefreshUpdateList();
        Barotrauma.GUI.UpdateMouseOn();
        GUIRuntime.Instance?.Update();
    }
    public static void UpdateListPostfix() => GUIRuntime.Instance?.AddNativeSurfaces();
    public static void ValidatePrefix() {
        if (Screen.Selected != GameMain.GameScreen && Screen.Selected is not PrimaryPackage.SubEditor.LiveEditorScreen) { UpdateEditorInput(); }
        else if (!GameMain.WindowActive || Barotrauma.GUI.InputBlockingMenuOpen || DebugConsole.IsOpen) { GUIRuntime.Instance?.Reset(); }
    }
    private static bool ResolveCursor(Barotrauma.GUIComponent c, ref CursorState __result) {
        if (Barotrauma.GUI.MouseCursor == CursorState.Waiting || GUIScrollBar.DraggingBar != null) { return true; }
        if (GUIRuntime.Instance?.Input.TryGetCursor(out __result) == true) { return false; }
        if (Screen.Selected is PrimaryPackage.SubEditor.LiveEditorScreen && SubEditorScreen.DraggedItemPrefab == null && (c == null || c is GUICustomComponent) && Inventory.IsMouseOnInventory) {
            __result = Inventory.GetInventoryMouseCursor();
            return false;
        }
        return true;
    }
    private static void ValidateMouseCapture() {
        lock (Barotrauma.GUI.mutex) {
            Barotrauma.GUIComponent previous = Barotrauma.GUI.MouseOn;
            if (previous != null && (!Barotrauma.GUI.updateListSet.Contains(previous) || !previous.Visible)) { Barotrauma.GUI.ForceMouseOn(null); }
        }
    }
    public static void CursorPrefix(SpriteBatch spriteBatch) {
        if (!GameMain.WindowActive || GUIRuntime.Instance == null) { return; }
        GUIRuntime.Instance.DrawOverlays(spriteBatch);
        GUIRuntime.Instance.Draw(Screen.Selected?.Cam, spriteBatch);
    }
    public void RoundStart() => runtime?.Reset(closeNative: true);
    public void Release() { if (runtime != null) { runtime.Dispose(); runtime = null; } }
}

public sealed class GUIOverlay : IDisposable {
    private readonly List<GUIOverlay> owner;
    internal readonly Func<bool> Visible;
    internal readonly Action<SpriteBatch> Draw;
    internal readonly Action Cancel;
    internal readonly bool BlocksWorld, HidesCursor, CancelOnWindowOpen, CapturesMovement;
    internal readonly Action<Camera, float> MoveCamera;
    internal GUIOverlay(List<GUIOverlay> owner, Func<bool> visible, Action<SpriteBatch> draw, Action cancel, bool blocksWorld, bool hidesCursor, bool cancelOnWindowOpen, bool capturesMovement, Action<Camera, float> moveCamera) {
        this.owner = owner;
        Visible = visible ?? throw new ArgumentNullException(nameof(visible));
        Draw = draw ?? throw new ArgumentNullException(nameof(draw));
        Cancel = cancel; BlocksWorld = blocksWorld; HidesCursor = hidesCursor; CancelOnWindowOpen = cancelOnWindowOpen; CapturesMovement = capturesMovement; MoveCamera = moveCamera;
    }
    public void Dispose() { if (owner.Remove(this)) { Cancel?.Invoke(); } }
}

public sealed class GUINativeSurface : IDisposable {
    private readonly List<GUINativeSurface> owner;
    internal readonly Action AddToUpdateList, Close;
    internal GUINativeSurface(List<GUINativeSurface> owner, Action addToUpdateList, Action close) {
        this.owner = owner;
        AddToUpdateList = addToUpdateList ?? throw new ArgumentNullException(nameof(addToUpdateList));
        Close = close ?? throw new ArgumentNullException(nameof(close));
    }
    public void Dispose() { if (owner.Remove(this)) { Close(); } }
}

public enum GUIPhase : byte { Motion, Layout, Input, Build, Render }

public sealed class GUIProfiler {
    private const int Capacity = 2048;
    private readonly long[,] ticks = new long[5, Capacity];
    private readonly long[,] bytes = new long[5, Capacity];
    private readonly int[] counts = new int[5];
    private readonly int[] cursors = new int[5];
    private long allocated;
    public bool Enabled { get; set; }
    internal long Begin() {
        if (!Enabled) { return 0; }
        allocated = GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }
    internal void End(GUIPhase phase, long start) {
        if (!Enabled || start == 0) { return; }
        int p = (int)phase, index = cursors[p];
        cursors[p] = (index + 1) & (Capacity - 1);
        counts[p] = Math.Min(Capacity, counts[p] + 1);
        ticks[p, index] = Stopwatch.GetTimestamp() - start;
        bytes[p, index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
    }
    public string Snapshot(GUIPhase phase) {
        int p = (int)phase, count = Math.Min(counts[p], Capacity);
        if (count == 0) { return phase + ": no samples"; }
        var samples = new long[count];
        long allocatedBytes = 0;
        for (int i = 0; i < count; i++) { samples[i] = ticks[p, i]; allocatedBytes += bytes[p, i]; }
        Array.Sort(samples);
        double factor = 1000000.0 / Stopwatch.Frequency;
        return $"{phase}: samples={count} p95={samples[(int)((count - 1) * .95)] * factor:F3}us p99={samples[(int)((count - 1) * .99)] * factor:F3}us bytes/sample={allocatedBytes / (double)count:F1}";
    }
}
