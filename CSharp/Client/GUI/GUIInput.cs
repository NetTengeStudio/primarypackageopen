using System;
using Barotrauma;
using EventInput;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PrimaryPackage.GUI;

public sealed class GUIInput {
    private readonly GUIRuntime runtime;
    private GUIComponent hovered, captured;
    private GUIWindow dragged;
    private Vector2 dragOffset;
    private MouseState previousMouse;
    private KeyboardState previousKeyboard;
    private bool consumeUntilRelease;
    private bool shortcutConsumed;
    private long lastClick;
    private double lastClickTime;
    private Vector2 lastClickPosition;
    public double DoubleClickInterval { get; set; } = .3;
    public GUIComponent Focus { get; private set; }
    public bool BlocksWorld { get; private set; }
    public Vector2 Pointer { get; private set; }
    internal bool TryGetCursor(out CursorState cursor) {
        cursor = dragged != null ? CursorState.Dragging
            : (captured ?? hovered) is TextInput ? CursorState.IBeam
            : (captured ?? hovered) != null ? CursorState.Hand : CursorState.Default;
        return BlocksWorld;
    }
    public event Action<Keys> Shortcut;
    internal event Action<Vector2> WorldPrimaryPressed;
    public void ConsumeShortcut() => shortcutConsumed = true;
    private bool DispatchShortcut(Keys key) { shortcutConsumed = false; Shortcut?.Invoke(key); return shortcutConsumed; }
    internal GUIInput(GUIRuntime runtime) { this.runtime = runtime; previousMouse = Mouse.GetState(); previousKeyboard = Keyboard.GetState(); }

    internal void Update() {
        MouseState mouse = Mouse.GetState();
        KeyboardState keyboard = Keyboard.GetState();
        Pointer = new Vector2(mouse.X, mouse.Y);
        bool down = mouse.LeftButton == ButtonState.Pressed;
        bool pressed = down && previousMouse.LeftButton == ButtonState.Released;
        bool released = !down && previousMouse.LeftButton == ButtonState.Pressed;
        if (Focus is TextInput && !ReferenceEquals(Barotrauma.GUI.KeyboardDispatcher.Subscriber, Focus)) { SetFocus(null); }
        var windows = runtime.Windows.Windows;
        GUIComponent hit = null;
        GUIWindow active = null;
        bool modal = false;
        for (int i = windows.Count - 1; i >= 0; i--) {
            var window = windows[i];
            if (window.Disposed || !window.Visible || window.InputTransparent) { continue; }
            modal |= window.Modal;
            if (window.Closing) { if (window.Bounds.Contains(mouse.X, mouse.Y) || window.Modal) { active = window; break; } continue; }
            if (window.Bounds.Contains(mouse.X, mouse.Y)) { active = window; hit = Hit(window, mouse.X, mouse.Y); break; }
            if (pressed && window.DismissOnOutside) { window.Close(); consumeUntilRelease = true; break; }
            if (window.Modal) { active = window; break; }
        }
        SetHovered(hit);
        if (hit is VirtualList hoveredList) { hoveredList.HoverAt(mouse.Y); }
        BlocksWorld = active != null || modal || captured != null || dragged != null || Focus != null || consumeUntilRelease;
        if (pressed) {
            if (active != null && !active.Closing && active.Bounds.Contains(mouse.X, mouse.Y)) {
                runtime.Windows.BringToFront(active);
                int titleHeight = (int)((GUITheme.TitleHeight + GUITheme.Edge) * runtime.Layout.Scale);
                if (mouse.Y < active.Bounds.Y + titleHeight) {
                    if (mouse.X >= active.Bounds.Right - titleHeight) { active.Close(); consumeUntilRelease = true; }
                    else { dragged = active; dragOffset = Pointer - new Vector2(active.Bounds.X, active.Bounds.Y); }
                } else if (hit != null) {
                    captured = hit; hit.Pressed = true; hit.Invalidate(GUIDirty.Style); SetFocus(hit);
                    if (hit is TextInput input) { input.PlaceCaret(mouse.X, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift)); }
                }
                else { SetFocus(null); }
            } else { SetFocus(null); }
        }
        if (dragged != null && down) { dragged.Move(Pointer - dragOffset); }
        if (captured != null && down && (mouse.X != previousMouse.X || mouse.Y != previousMouse.Y)) {
            if (captured is TextInput input) { input.PlaceCaret(mouse.X, true); }
            captured.Drag(Pointer, new Vector2(mouse.X - previousMouse.X, mouse.Y - previousMouse.Y));
        }
        if (released) {
            GUIComponent selected = captured;
            captured = null;
            dragged = null;
            if (selected != null) {
                selected.Pressed = false;
                selected.Invalidate(GUIDirty.Style);
                if (ReferenceEquals(selected, hit) && selected.Enabled && !selected.Disposed) {
                    if (selected is VirtualList list) { list.Pick(mouse.Y); } else { selected.Activate(); }
                    double now = GUIRuntime.Now;
                    if (lastClick == selected.Id && now - lastClickTime <= Math.Clamp(DoubleClickInterval, .1, .8) && Vector2.DistanceSquared(Pointer, lastClickPosition) <= 36) {
                        selected.DoubleActivate(); lastClick = 0;
                    } else { lastClick = selected.Id; lastClickTime = now; lastClickPosition = Pointer; }
                }
            }
            consumeUntilRelease = false;
        }
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (wheel != 0) {
            for (GUIComponent ancestor = hit ?? active; ancestor != null; ancestor = ancestor.Parent) { if (ancestor.Wheel(wheel)) { break; } }
        }
        if (keyboard.IsKeyDown(Keys.Escape) && previousKeyboard.IsKeyUp(Keys.Escape)) {
            GUIWindow top = null;
            for (int i = windows.Count - 1; i >= 0; i--) { if (windows[i].Visible && !windows[i].Disposed && !windows[i].InputTransparent) { top = windows[i]; break; } }
            bool consumed = DispatchShortcut(Keys.Escape);
            if (!consumed && top != null) { top.Close(); consumeUntilRelease = down || mouse.RightButton == ButtonState.Pressed; }
            SetFocus(null);
        }
        if (keyboard.IsKeyDown(Keys.Tab) && previousKeyboard.IsKeyUp(Keys.Tab) && windows.Count > 0) { NextFocus(windows[windows.Count - 1], keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift)); }
        if (keyboard.IsKeyDown(Keys.Enter) && previousKeyboard.IsKeyUp(Keys.Enter) && Focus is Button button) { button.Activate(); }
        if (Focus is VirtualList) {
            if (keyboard.IsKeyDown(Keys.Up) && previousKeyboard.IsKeyUp(Keys.Up)) { Focus.Navigate(-1); }
            if (keyboard.IsKeyDown(Keys.Down) && previousKeyboard.IsKeyUp(Keys.Down)) { Focus.Navigate(1); }
        }
        if (keyboard.IsKeyDown(Keys.F5) && previousKeyboard.IsKeyUp(Keys.F5)) { DispatchShortcut(Keys.F5); }
        if (keyboard.IsKeyDown(Keys.F2) && previousKeyboard.IsKeyUp(Keys.F2)) { DispatchShortcut(Keys.F2); }
        if (pressed && runtime.ViewportInputAvailable && !runtime.OverlayCapture) { WorldPrimaryPressed?.Invoke(Pointer); }
        previousMouse = mouse;
        previousKeyboard = keyboard;
        if (!down && mouse.RightButton == ButtonState.Released) { consumeUntilRelease = false; }
    }

    private static GUIComponent Hit(GUIComponent node, int x, int y) {
        if (!node.Visible || !node.Enabled || node.Disposed || !node.Clip.Contains(x, y)) { return null; }
        for (int i = node.Children.Count - 1; i >= 0; i--) {
            var hit = Hit(node.Children[i], x, y);
            if (hit != null) { return hit; }
        }
        return node.Interactive ? node : null;
    }

    private void SetHovered(GUIComponent component) {
        if (hovered == component) { return; }
        if (hovered != null) { hovered.SetHover(false); }
        hovered = component;
        if (hovered != null) { hovered.SetHover(true); }
    }

    public void SetFocus(GUIComponent component) {
        if (component != null && (component.Disposed || !component.Visible || !component.Enabled || !runtime.Windows.AllowsInput(component.Window))) { return; }
        if (Focus == component) { return; }
        if (Focus is TextInput oldInput) {
            oldInput.Selected = false;
            oldInput.EndEditing();
            if (ReferenceEquals(Barotrauma.GUI.KeyboardDispatcher.Subscriber, oldInput)) {
                Microsoft.Xna.Framework.TextInput.StopTextInput();
                Barotrauma.GUI.KeyboardDispatcher.Subscriber = null;
            }
        }
        Focus?.Invalidate(GUIDirty.Style);
        Focus = component;
        if (Focus != null) { for (GUIComponent ancestor = Focus.Parent; ancestor != null; ancestor = ancestor.Parent) { if (ancestor is ScrollView scroll) { scroll.Reveal(Focus.Rectangle); } } }
        if (Focus is TextInput input) {
            Barotrauma.GUI.KeyboardDispatcher.Subscriber = input;
            Microsoft.Xna.Framework.TextInput.StartTextInput();
            Microsoft.Xna.Framework.TextInput.SetTextInputRect(input.Rectangle);
        }
        Focus?.Invalidate(GUIDirty.Style);
    }

    private void NextFocus(GUIComponent root, bool reverse) {
        GUIComponent first = null, next = null;
        bool found = Focus == null;
        FindFocus(root, ref first, ref next, ref found, reverse);
        SetFocus(next ?? first);
    }
    private void FindFocus(GUIComponent node, ref GUIComponent first, ref GUIComponent next, ref bool found, bool reverse) {
        if (!node.Visible || !node.Enabled || node.Disposed) { return; }
        if (node.Interactive) {
            first ??= node;
            if (found && next == null) { next = node; }
            if (node == Focus) { found = true; }
        }
        for (int i = 0; i < node.Children.Count; i++) { FindFocus(node.Children[reverse ? node.Children.Count - 1 - i : i], ref first, ref next, ref found, reverse); }
    }
    internal void Detach(GUIComponent component) {
        if (Within(Focus, component)) { SetFocus(null); }
        if (Within(captured, component)) { GUIComponent previous = captured; captured = null; previous.Pressed = false; previous.Invalidate(GUIDirty.Style); previous.CancelInteraction(); consumeUntilRelease = true; }
        if (Within(hovered, component)) { SetHovered(null); }
        if (dragged == component) { dragged = null; }
    }
    private static bool Within(GUIComponent node, GUIComponent ancestor) {
        if (ancestor is GUIWindow window && node != null) {
            for (GUIWindow owner = node.Window; owner != null; owner = owner.OwnerWindow) { if (owner == window) { return true; } }
        }
        for (; node != null; node = node.Parent) { if (node == ancestor) { return true; } }
        return false;
    }
    public void Capture(GUIComponent component) {
        if (component == null || component.Disposed || !component.Enabled || !component.Visible || component.Window == null || !runtime.Windows.AllowsInput(component.Window)) { throw new ArgumentException(nameof(component)); }
        if (captured != null && captured != component) { captured.Pressed = false; captured.CancelInteraction(); captured.Invalidate(GUIDirty.Style); }
        captured = component; captured.Pressed = true; captured.Invalidate(GUIDirty.Style); BlocksWorld = true;
    }
    public void Cancel() {
        GUIComponent previous = captured; captured = null; dragged = null; consumeUntilRelease = false; BlocksWorld = false; lastClick = 0;
        SetFocus(null); SetHovered(null);
        if (previous != null) { previous.Pressed = false; previous.Invalidate(GUIDirty.Style); previous.CancelInteraction(); }
    }
    public void ConsumePointer() { consumeUntilRelease = true; BlocksWorld = true; }
    internal void Release() { Cancel(); Shortcut = null; WorldPrimaryPressed = null; }
}
