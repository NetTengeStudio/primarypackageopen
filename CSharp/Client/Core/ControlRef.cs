using System;
using Barotrauma;
using Barotrauma.LuaCs.Data;
using Microsoft.Xna.Framework.Input;

namespace PrimaryPackage.Core;

public sealed class ControlRef : ISettingRef {
    private readonly string name;

    private ISettingControl source;

    public ControlRef(string name) => this.name = name;

    public string Name => name;

    public bool Bound => source != null && !source.IsDisposed;

    private KeyOrMouse Key => Bound ? source.Value : null;

    public void Bind() {
        Drop();
        if (ModConfig.TryGet(name, out ISettingControl setting)) { source = setting; }
    }

    public void Drop() => source = null;

    public bool Hit() {
        if (!Bound) { return false; }
        try {
            return source.IsHit();
        } catch (Exception) {
            return false;
        }
    }

    public bool DownUnfiltered() {
        KeyOrMouse bind = Key;
        if (bind == null) { return false; }
        if (bind.MouseButton == MouseButton.None) {
            return bind.Key != Keys.None && Keyboard.GetState().IsKeyDown(bind.Key);
        }
        MouseState mouse = Mouse.GetState();
        return bind.MouseButton switch {
            MouseButton.PrimaryMouse => mouse.LeftButton == ButtonState.Pressed,
            MouseButton.SecondaryMouse => mouse.RightButton == ButtonState.Pressed,
            MouseButton.MiddleMouse => mouse.MiddleButton == ButtonState.Pressed,
            MouseButton.MouseButton4 => mouse.XButton1 == ButtonState.Pressed,
            MouseButton.MouseButton5 => mouse.XButton2 == ButtonState.Pressed,
            _ => false
        };
    }
}
