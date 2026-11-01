using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Core;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

public static class HudFrame {
    public const int Chill = 0;

    public const int Hint = 100;
    public const int Prompt = 110;
    public const int Script = 120;
    public const int Thought = 130;
    public const int Wake = 140;
    public const int Zoom = 150;

    private static readonly List<int> underLayers = new List<int>();
    private static readonly List<Action<Camera, SpriteBatch>> underPainters = new List<Action<Camera, SpriteBatch>>();
    private static readonly List<int> overLayers = new List<int>();
    private static readonly List<Action<Camera, SpriteBatch>> overPainters = new List<Action<Camera, SpriteBatch>>();

    public static void Attach(Harmony harmony) =>
        harmony.Patch(
            AccessTools.Method(typeof(GUI), nameof(GUI.Draw)),
            prefix: ModuleHost.Bind(typeof(HudFrame), nameof(DrawPrefix)),
            postfix: ModuleHost.Bind(typeof(HudFrame), nameof(DrawPostfix)));

    public static void Under(int layer, Action<Camera, SpriteBatch> paint) =>
        Insert(underLayers, underPainters, layer, paint);

    public static void Over(int layer, Action<Camera, SpriteBatch> paint) =>
        Insert(overLayers, overPainters, layer, paint);

    public static void Remove(Action<Camera, SpriteBatch> paint) {
        int index = underPainters.IndexOf(paint);
        if (index >= 0) { underPainters.RemoveAt(index); underLayers.RemoveAt(index); }
        index = overPainters.IndexOf(paint);
        if (index >= 0) { overPainters.RemoveAt(index); overLayers.RemoveAt(index); }
    }

    public static void Release() {
        underLayers.Clear();
        underPainters.Clear();
        overLayers.Clear();
        overPainters.Clear();
    }

    public static void DrawPrefix(Camera cam, SpriteBatch spriteBatch) => Paint(underPainters, cam, spriteBatch);

    public static void DrawPostfix(Camera cam, SpriteBatch spriteBatch) => Paint(overPainters, cam, spriteBatch);

    private static void Insert(List<int> layers, List<Action<Camera, SpriteBatch>> painters, int layer,
        Action<Camera, SpriteBatch> paint) {
        if (paint == null) { return; }
        int at = layers.Count;
        while (at > 0 && layers[at - 1] > layer) { at--; }
        layers.Insert(at, layer);
        painters.Insert(at, paint);
    }

    private static void Paint(List<Action<Camera, SpriteBatch>> painters, Camera cam, SpriteBatch spriteBatch) {
        for (int i = 0; i < painters.Count; i++) {
            try {
                painters[i](cam, spriteBatch);
            } catch (Exception error) {
                ModLog.Once($"{painters[i].Method.Name} cannot draw: {error.Message}");
            }
        }
    }
}
