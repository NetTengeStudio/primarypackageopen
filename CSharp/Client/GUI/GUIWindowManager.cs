using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.GUI;

public class GUIWindow : ScrollView {
    internal readonly List<GUIDrawCommand> Commands = new();
    internal readonly List<GUIComponent> DirtyNodes = new();
    internal bool RebuildCommands = true;
    internal readonly MotionValue Openness;
    internal Vector2 Anchor, Origin;
    internal GUIDirty WindowDirty = GUIDirty.All;
    internal bool Closing;
    public bool Modal { get; }
    public GUIWindow OwnerWindow { get; internal set; }
    internal readonly Screen OwnerScreen;
    public bool DismissOnOutside { get; set; }
    public bool InputTransparent { get; internal set; }
    public string Title { get; }
    public event Action<GUIWindow> Closed;
    public event Action<GUIWindow> ClosingRequested;

    internal GUIWindow(string title, Vector2 position, float width, float height, bool modal) {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) { throw new ArgumentOutOfRangeException(nameof(position)); }
        Title = title ?? "";
        OwnerScreen = Screen.Selected;
        Anchor = Origin = position;
        Width = width;
        Height = height;
        Modal = modal;
        Window = this;
        Openness = GUIMotion.Shared.Create(0, MotionSettings.Open);
        Openness.AnimateTo(1, .22, MotionCurve.EaseOut);
    }

    public void Close() {
        if (Disposed || Closing) { return; }
        Closing = true;
        ClosingRequested?.Invoke(this);
        FinishAnimations();
        GUIRuntime.Instance?.Input.Detach(this);
        Openness.AnimateTo(0, .16, MotionCurve.Smooth);
    }

    public void Suspend() {
        if (Disposed) { throw new ObjectDisposedException(nameof(GUIWindow)); }
        Visible = false;
        GUIRuntime.Instance?.Input.Detach(this);
    }
    public void Resume() {
        if (Disposed) { return; }
        Visible = true;
        GUIRuntime.Instance?.Windows.BringToFront(this);
    }
    public void Move(Vector2 position) {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) { throw new ArgumentOutOfRangeException(nameof(position)); }
        Anchor = position;
        Invalidate(GUIDirty.Layout);
    }
    public void Reopen() {
        if (Disposed) { throw new ObjectDisposedException(nameof(GUIWindow)); }
        Closing = false;
        Openness.AnimateTo(1, .22, MotionCurve.EaseOut);
    }

    internal void InvalidateWindow(GUIDirty dirty) => WindowDirty |= dirty | GUIDirty.Render;
    internal void Enqueue(GUIComponent component, GUIDirty dirty) {
        InvalidateWindow(dirty);
        if (!component.Queued) { component.Queued = true; DirtyNodes.Add(component); }
    }

    public override void Dispose() {
        if (Disposed) { return; }
        if (!Closing) { Closing = true; ClosingRequested?.Invoke(this); }
        Openness.Dispose();
        base.Dispose();
        Commands.Clear();
        DirtyNodes.Clear();
        Closed?.Invoke(this);
        Closed = null;
        ClosingRequested = null;
    }
}

public sealed class GUIWindowManager : IDisposable {
    private readonly List<GUIWindow> windows = new();
    private bool disposed;
    private bool clearing;
    public IReadOnlyList<GUIWindow> Windows => windows;
    public GUIWindow Open(string title, Vector2 anchor, float width = 320, float height = 0, bool modal = false) {
        RequireOpen();
        var window = new Window(title, anchor, width, height, modal);
        Mount(window);
        return window;
    }
    public void BringToFront(GUIWindow window) {
        if (windows.Remove(window)) { Mount(window); }
    }
    private void Mount(GUIWindow window) {
        if (window is not (Notification or Tooltip)) { GUIRuntime.Instance?.CancelBlockingOverlays(); }
        int at = windows.Count;
        if (!window.Modal) {
            for (int i = windows.Count - 1; i >= 0; i--) {
                if (!windows[i].Modal || windows[i].Disposed) { continue; }
                bool child = false;
                for (GUIWindow owner = window.OwnerWindow; owner != null; owner = owner.OwnerWindow) { if (owner == windows[i]) { child = true; break; } }
                if (!child) { at = i; }
                break;
            }
        }
        windows.Insert(at, window);
        if (window.Modal && GUIRuntime.Instance != null) { GUIRuntime.Instance.Input.Cancel(); GUIRuntime.Instance.Input.ConsumePointer(); }
    }
    public GUIWindow OpenChild(GUIWindow owner, string title, Vector2 anchor, float width = 300) {
        RequireOpen();
        if (owner == null || owner.Disposed || owner.Closing || !windows.Contains(owner)) { throw new ArgumentException(nameof(owner)); }
        var window = new Window(title, anchor, width, 0, false) { OwnerWindow = owner };
        Mount(window);
        return window;
    }
    internal bool AllowsInput(GUIWindow window) {
        for (int i = windows.Count - 1; i >= 0; i--) {
            if (windows[i] == window) { return !window.Closing && !window.Disposed; }
            if (windows[i].Modal && !windows[i].Disposed) { return false; }
        }
        return false;
    }
    internal void CloseForeignScreen(Screen screen) {
        for (int i = windows.Count - 1; i >= 0; i--) {
            if (windows[i].OwnerScreen == screen) { continue; }
            var window = windows[i]; windows.RemoveAt(i); window.Dispose();
        }
    }
    public ContextMenu OpenContext(string title, Vector2 anchor, float width = 300) {
        RequireOpen();
        var window = new ContextMenu(title, anchor, width);
        Mount(window);
        return window;
    }
    public Popup OpenPopup(string title, Vector2 anchor, float width = 380) {
        RequireOpen();
        var window = new Popup(title, anchor, width);
        Mount(window);
        return window;
    }
    public Notification Notify(string title, Vector2 anchor, double duration = 8) {
        RequireOpen();
        int count = 0, oldest = -1;
        for (int i = 0; i < windows.Count; i++) { if (windows[i] is Notification) { count++; if (oldest < 0) { oldest = i; } } }
        if (count >= 4 && oldest >= 0) { var previous = windows[oldest]; windows.RemoveAt(oldest); previous.Dispose(); }
        var window = new Notification(title, anchor, duration);
        Mount(window);
        return window;
    }
    public Tooltip ShowTooltip(string text, Vector2 anchor) {
        RequireOpen();
        for (int i = windows.Count - 1; i >= 0; i--) { if (windows[i] is Tooltip previous) { windows.RemoveAt(i); previous.Dispose(); } }
        var window = new Tooltip(text, anchor);
        Mount(window);
        return window;
    }
    internal void Collect() {
        for (int i = windows.Count - 1; i >= 0; i--) {
            GUIWindow window = windows[i];
            if (window.OwnerWindow is { } owner && (owner.Closing || owner.Disposed)) { window.Close(); }
            if (window is Notification notification && GUIRuntime.Now >= notification.ExpiresAt) { window.Close(); }
            if (window.Disposed || window.Closing && !window.Openness.Moving) { windows.RemoveAt(i); window.Dispose(); }
        }
    }
    public void CloseAll() {
        clearing = true;
        try {
            while (windows.Count > 0) { int index = windows.Count - 1; var window = windows[index]; windows.RemoveAt(index); window.Dispose(); }
        } finally { clearing = false; }
    }
    private void RequireOpen() {
        if (disposed) { throw new ObjectDisposedException(nameof(GUIWindowManager)); }
        if (clearing || windows.Count >= 64) { throw new InvalidOperationException("GUI window capacity reached or screen is closing"); }
    }
    public void Dispose() { if (disposed) { return; } disposed = true; CloseAll(); }
}

public sealed class Window : GUIWindow {
    internal Window(string title, Vector2 anchor, float width, float height, bool modal) : base(title, anchor, width, height, modal) { }
}

public sealed class ContextMenu : GUIWindow {
    internal ContextMenu(string title, Vector2 anchor, float width) : base(title, anchor, width, 0, false) { DismissOnOutside = true; }
}

public sealed class Popup : GUIWindow {
    internal Popup(string title, Vector2 anchor, float width) : base(title, anchor, width, 0, true) { }
}

public sealed class Notification : GUIWindow {
    internal double ExpiresAt { get; }
    internal Notification(string title, Vector2 anchor, double duration) : base(title, anchor, 360, 0, false) {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 60) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        ExpiresAt = GUIRuntime.Now + duration;
    }
}

public sealed class Tooltip : GUIWindow {
    internal Tooltip(string text, Vector2 anchor) : base("", anchor, 280, 0, false) { InputTransparent = true; Add(new Text(text)); }
}
