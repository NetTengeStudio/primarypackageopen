using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.GUI;

[Flags]
public enum GUIDirty : byte { None = 0, Layout = 1, Style = 2, Content = 4, Render = 8, All = 15 }
public enum GUIFlow : byte { Vertical, Horizontal, Overlay }

public class GUIComponent : IDisposable {
    private static long nextId;
    private readonly List<GUIComponent> children = new();
    private string text = "";
    private LocalizedString localizedText;
    private bool visible = true, enabled = true;
    private float width, height = GUITheme.RowHeight;
    private bool fillHeight;
    private GUIFlow flow;
    internal GUIDirty Dirty = GUIDirty.All;
    internal Rectangle Bounds, Clip;
    internal Vector2 TextSize;
    internal bool Hovered, Pressed, Disposed;
    internal bool Queued;
    internal int RenderStart = -1, RenderCount;
    internal int MeasuredHeight;
    internal float MeasuredScale;
    internal bool MeasureDirty = true;
    private MotionValue hoverMotion;
    internal float HoverAmount => (float)(hoverMotion?.Position ?? 0);
    internal GUIWindow Window;
    public long Id { get; } = ++nextId;
    public GUIComponent Parent { get; private set; }
    public bool IsDisposed => Disposed;
    public IReadOnlyList<GUIComponent> Children => children;
    public Rectangle Rectangle => Bounds;
    public string Text { get => localizedText?.Value ?? text; set { value ??= ""; if (text == value && localizedText == null) { return; } text = value; localizedText = null; Invalidate(GUIDirty.Content | GUIDirty.Render); } }
    public LocalizedString LocalizedText { get => localizedText; set { localizedText = value; Invalidate(GUIDirty.Content); } }
    public bool Visible { get => visible; set { if (visible == value) { return; } visible = value; if (!value) { GUIRuntime.Instance?.Input.Detach(this); FinishAnimations(); } Invalidate(GUIDirty.Layout | GUIDirty.Render); } }
    public bool Enabled { get => enabled; set { if (enabled == value) { return; } enabled = value; Invalidate(GUIDirty.Style | GUIDirty.Render); } }
    public float Width { get => width; set { ValidateSize(value); if (width == value) { return; } width = value; Invalidate(GUIDirty.Layout); } }
    public float Height { get => height; set { ValidateSize(value); if (height == value) { return; } height = value; Invalidate(GUIDirty.Layout); } }
    public bool FillHeight { get => fillHeight; set { if (fillHeight == value) { return; } fillHeight = value; Invalidate(GUIDirty.Layout); } }
    public GUIFlow Flow { get => flow; set { if (flow == value) { return; } flow = value; Invalidate(GUIDirty.Layout); } }
    public virtual bool Interactive => false;
    public event Action<GUIComponent> Activated;
    public event Action<GUIComponent> DoubleActivated;
    public event Action<GUIComponent> Cancelled;
    public event Action<GUIComponent, Vector2, Vector2> Dragged;

    public T Add<T>(T child) where T : GUIComponent {
        if (Disposed) { throw new ObjectDisposedException(GetType().Name); }
        if (child == null || child.Parent != null || child.Disposed || child.Window != null) { throw new ArgumentException(nameof(child)); }
        int depth = 0;
        for (GUIComponent ancestor = this; ancestor != null; ancestor = ancestor.Parent) {
            if (ReferenceEquals(ancestor, child)) { throw new InvalidOperationException("Cyclic GUI tree"); }
            if (++depth > 128) { throw new InvalidOperationException("GUI tree depth exceeds 128"); }
        }
        child.Parent = this;
        child.Attach(Window);
        children.Add(child);
        Invalidate(GUIDirty.Layout | GUIDirty.Render);
        return child;
    }

    public void Clear() {
        while (children.Count > 0) {
            int index = children.Count - 1;
            GUIComponent child = children[index];
            children.RemoveAt(index);
            child.Parent = null;
            child.Dispose();
        }
        Invalidate(GUIDirty.Layout | GUIDirty.Render);
    }

    public void Invalidate(GUIDirty dirty) {
        if (Disposed) { return; }
        Dirty |= dirty;
        if ((dirty & GUIDirty.Layout) != 0) { MeasureDirty = true; }
        if ((dirty & GUIDirty.Layout) != 0 && Parent != null) { Parent.Invalidate(GUIDirty.Layout); }
        Window?.Enqueue(this, dirty);
    }

    public virtual void Dispose() {
        if (Disposed) { return; }
        Disposed = true;
        GUIRuntime.Instance?.Input.Detach(this);
        Clear();
        Activated = null;
        DoubleActivated = Cancelled = null;
        Dragged = null;
        hoverMotion?.Dispose();
        GUIComponent parent = Parent;
        Parent = null;
        if (parent != null) { parent.children.Remove(this); parent.Invalidate(GUIDirty.Layout); }
        Window = null;
    }

    internal void Attach(GUIWindow window) {
        Window = window;
        for (int i = 0; i < children.Count; i++) { children[i].Attach(window); }
    }

    internal virtual void Activate() { if (Enabled && !Disposed) { Activated?.Invoke(this); } }
    internal virtual void Navigate(int direction) { }
    internal virtual bool Wheel(float delta) => false;
    internal virtual void FinishAnimations() {
        hoverMotion?.Finish();
        for (int i = 0; i < children.Count; i++) { children[i].FinishAnimations(); }
    }
    internal void DoubleActivate() { if (Enabled && !Disposed) { DoubleActivated?.Invoke(this); } }
    internal void CancelInteraction() => Cancelled?.Invoke(this);
    internal void Drag(Vector2 pointer, Vector2 delta) => Dragged?.Invoke(this, pointer, delta);
    internal void SetHover(bool hovered) {
        Hovered = hovered;
        if (hoverMotion == null && hovered) {
            hoverMotion = GUIMotion.Shared.Create(0, MotionSettings.Hover);
            hoverMotion.Changed += HoverChanged;
        }
        hoverMotion?.MoveTo(hovered ? 1 : 0);
        Invalidate(GUIDirty.Style);
    }
    private void HoverChanged(MotionValue value) => Invalidate(GUIDirty.Style);
    internal virtual void Paint(GUIRenderer renderer, float opacity) {
        if (Text.Length > 0) { renderer.Text(this, GUITheme.Text, opacity); }
    }

    private static void ValidateSize(float value) {
        if (!float.IsFinite(value) || value < 0 || value > 100000) { throw new ArgumentOutOfRangeException(nameof(value)); }
    }
}

public class Panel : GUIComponent {
    public Panel() { Height = 0; }
    internal override void Paint(GUIRenderer renderer, float opacity) { }
}

public class Text : GUIComponent {
    public bool Wrap { get; set; }
    public Color Color { get; set; } = GUITheme.Text;
    public Text(string text) { Text = text; }
    public Text(LocalizedString text) { LocalizedText = text; }
    internal override void Paint(GUIRenderer renderer, float opacity) => renderer.Text(this, Color, opacity);
}

public class Button : GUIComponent {
    public bool Selected { get; set; }
    public override bool Interactive => true;
    public Button(string text, Action<GUIComponent> action = null) { Height = GUITheme.ButtonHeight; Text = text; if (action != null) { Activated += action; } }
    public Button(LocalizedString text, Action<GUIComponent> action = null) { Height = GUITheme.ButtonHeight; LocalizedText = text; if (action != null) { Activated += action; } }
    internal override void Paint(GUIRenderer renderer, float opacity) {
        renderer.Fill(Bounds, Pressed ? GUITheme.Pressed : Selected ? GUITheme.Hover : Color.Lerp(Color.Black, GUITheme.Hover, HoverAmount), opacity);
        renderer.Border(Bounds, !Enabled ? GUITheme.Rule : Selected || GUIRuntime.Instance?.Input.Focus == this ? GUITheme.Accent : Hovered ? GUITheme.EdgeLit : GUITheme.Border, opacity);
        renderer.Text(this, !Enabled ? GUITheme.Muted : Selected ? GUITheme.Accent : Hovered ? Color.White : GUITheme.Text, opacity, centered: true);
    }
}

public sealed class GUIComponentRegistry {
    private readonly Dictionary<string, Func<GUIComponent>> factories = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, Func<GUIComponent>> typed = new();
    public void Register<T>(Func<T> factory) where T : GUIComponent {
        if (factory == null || typed.ContainsKey(typeof(T))) { throw new ArgumentException(nameof(factory)); }
        typed.Add(typeof(T), () => factory());
    }
    public T Create<T>() where T : GUIComponent => typed.TryGetValue(typeof(T), out var factory) ? (T)factory() : throw new InvalidOperationException("Component is not registered");
    public void Register(string name, Func<GUIComponent> factory) {
        if (string.IsNullOrWhiteSpace(name) || factory == null || !factories.TryAdd(name, factory)) { throw new ArgumentException(nameof(name)); }
    }
    public GUIComponent Create(string name) => factories.TryGetValue(name, out var factory) ? factory() : throw new ArgumentException(nameof(name));
    public void Clear() { factories.Clear(); typed.Clear(); }
}
