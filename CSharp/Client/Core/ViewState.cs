using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Core;

public enum ViewChannel {
    Cold = 0,
    Immersion = 1
}

public static class ViewState {
    public const float Ceiling = 6.0f;
    public const float EngineMaxZoom = 2.0f;

    private const int Channels = 2;
    private const float ZoomOutExtend = 0.85f;
    private const float MinMultiplier = 0.1f;
    private const float MaxMultiplier = 4.0f;

    private static readonly AccessTools.FieldRef<Camera, float?> defaultZoom = Ref();
    private static readonly float[] channels = { 1.0f, 1.0f };

    private static Camera bound;
    private static float restoreMaxZoom = EngineMaxZoom;
    private static float bias;

    public static bool Supported => defaultZoom != null;

    public static float Bias {
        get => bias;
        set => bias = MathHelper.Clamp(value, -Ceiling, Ceiling);
    }

    public static float BaseZoom => GameSettings.CurrentConfig.EnableMouseLook ? 1.3f : 1.0f;

    public static float Multiplier {
        get {
            float product = 1.0f;
            for (int i = 0; i < Channels; i++) { product *= channels[i]; }
            return MathHelper.Clamp(product, MinMultiplier, MaxMultiplier);
        }
    }

    public static float Resting => BaseZoom * Multiplier;

    public static float Floor => BaseZoom * ZoomOutExtend;

    public static float Target => MathHelper.Clamp(Resting + bias, Floor, Ceiling);

    public static void Contribute(ViewChannel channel, float factor) {
        int index = (int)channel;
        if (index < 0 || index >= Channels) { return; }
        channels[index] = MathHelper.Clamp(factor, MinMultiplier, MaxMultiplier);
    }

    public static void Reset() {
        bias = 0f;
        for (int i = 0; i < Channels; i++) { channels[i] = 1.0f; }
    }

    public static void Apply(Camera cam) {
        if (cam == null || defaultZoom == null) { return; }
        if (!ReferenceEquals(cam, bound)) { Bind(cam); }
        if (cam.MaxZoom < Ceiling) { cam.MaxZoom = Ceiling; }
        defaultZoom(cam) = Target;
    }

    public static void Release() {
        Camera cam = bound;
        bound = null;
        Reset();
        if (cam == null) { return; }
        try {
            cam.MaxZoom = restoreMaxZoom;
            if (defaultZoom != null) { defaultZoom(cam) = null; }
        } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("ViewState.1", noted); }
        restoreMaxZoom = EngineMaxZoom;
    }

    private static void Bind(Camera cam) {
        bound = cam;
        restoreMaxZoom = cam.MaxZoom >= Ceiling ? EngineMaxZoom : cam.MaxZoom;
    }

    private static AccessTools.FieldRef<Camera, float?> Ref() {
        try {
            System.Reflection.FieldInfo field = AccessTools.Field(typeof(Camera), "defaultZoom");
            return field == null ? null : AccessTools.FieldRefAccess<Camera, float?>(field);
        } catch (Exception) {
            return null;
        }
    }
}
