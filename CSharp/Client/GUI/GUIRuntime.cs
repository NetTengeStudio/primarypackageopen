using System;
using System.Diagnostics;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public event Action Updating;

    internal GUIRuntime() {
        if (Instance != null) { throw new InvalidOperationException("GUIRuntime already mounted"); }
        Instance = this;
        screen = Screen.Selected;
        Input = new GUIInput(this);
    }
    public GUIOverlay RegisterOverlay(Func<bool> visible, Action<SpriteBatch> draw, Action cancel = null, bool blocksWorld = false, bool hidesCursor = false, bool cancelOnWindowOpen = true) {
        var overlay = new GUIOverlay(overlays, visible, draw, cancel, blocksWorld, hidesCursor, cancelOnWindowOpen);
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
        harmony.CreateClassProcessor(typeof(GUIWorldMouse)).Patch();
        harmony.CreateClassProcessor(typeof(GUIWorldScroll)).Patch();
        harmony.CreateClassProcessor(typeof(GUIWorldKeyboard)).Patch();
        harmony.CreateClassProcessor(typeof(GUIWorldKeys)).Patch();
        harmony.Patch(AccessTools.Method(typeof(Barotrauma.GUI), "DrawCursor"), prefix: ModuleHost.Bind(typeof(GUIBridge), nameof(CursorPrefix)));
    }
    public static void UpdatePrefix() {
        Barotrauma.GUI.UpdateMouseOn();
        GUIRuntime.Instance?.Update();
    }
    public static void UpdateListPostfix() => GUIRuntime.Instance?.AddNativeSurfaces();
    public static void ValidatePrefix() {
        if (Screen.Selected != GameMain.GameScreen) { Barotrauma.GUI.UpdateMouseOn(); GUIRuntime.Instance?.Update(); }
        else if (!GameMain.WindowActive || Barotrauma.GUI.InputBlockingMenuOpen || DebugConsole.IsOpen) { GUIRuntime.Instance?.Reset(); }
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
    internal readonly bool BlocksWorld, HidesCursor, CancelOnWindowOpen;
    internal GUIOverlay(List<GUIOverlay> owner, Func<bool> visible, Action<SpriteBatch> draw, Action cancel, bool blocksWorld, bool hidesCursor, bool cancelOnWindowOpen) {
        this.owner = owner;
        Visible = visible ?? throw new ArgumentNullException(nameof(visible));
        Draw = draw ?? throw new ArgumentNullException(nameof(draw));
        Cancel = cancel; BlocksWorld = blocksWorld; HidesCursor = hidesCursor; CancelOnWindowOpen = cancelOnWindowOpen;
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
