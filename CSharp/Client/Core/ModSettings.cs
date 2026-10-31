using System;
using System.Collections.Generic;

namespace PrimaryPackage.Core;

public static class ModSettings {
    public static readonly ControlRef ZoomInKey = new ControlRef("ZoomInKey");
    public static readonly ControlRef ZoomOutKey = new ControlRef("ZoomOutKey");
    public static readonly ControlRef ZoomResetKey = new ControlRef("ZoomResetKey");
    public static readonly ControlRef ReloadKey = new ControlRef("ReloadKey");
    public static readonly ControlRef RPWheelKey = new ControlRef("RPWheelKey");

    public static readonly SettingRef<bool> BloomEnabled = new SettingRef<bool>("BloomEnabled", true);
    public static readonly SettingRef<float> BloomIntensity = new SettingRef<float>("BloomIntensity", 1f);
    public static readonly SettingRef<float> BloomThreshold = new SettingRef<float>("BloomThreshold", 0.55f);
    public static readonly SettingRef<float> BloomKnee = new SettingRef<float>("BloomKnee", 0.5f);
    public static readonly SettingRef<float> BloomMultiplier = new SettingRef<float>("BloomMultiplier", 1.2f);
    public static readonly SettingRef<float> BloomRadius = new SettingRef<float>("BloomRadius", 4f);
    public static readonly SettingRef<float> BloomSpread = new SettingRef<float>("BloomSpread", 0.6f);
    public static readonly SettingRef<int> BloomLevels = new SettingRef<int>("BloomLevels", 6);

    public static readonly SettingRef<float> ExplosionFlash = new SettingRef<float>("ExplosionFlash", 1f);
    public static readonly SettingRef<float> ExplosionDebris = new SettingRef<float>("ExplosionDebris", 1.5f);
    public static readonly SettingRef<float> ExplosionShock = new SettingRef<float>("ExplosionShock", 1f);
    public static readonly SettingRef<float> BlastSmoke = new SettingRef<float>("BlastSmoke", 1f);

    public static readonly SettingRef<bool> MotionBlurEnabled = new SettingRef<bool>("MotionBlurEnabled", true);
    public static readonly SettingRef<float> MotionBlurStrength = new SettingRef<float>("MotionBlurStrength", 1.5f);

    public static readonly SettingRef<bool> ColdEffects = new SettingRef<bool>("ColdEffects", true);
    public static readonly SettingRef<bool> ThoughtsEnabled = new SettingRef<bool>("ThoughtsEnabled", true);
    public static readonly SettingRef<bool> WakeIntroEnabled = new SettingRef<bool>("WakeIntroEnabled", true);

    public static readonly SettingRef<bool> ColdDebug = new SettingRef<bool>("ColdDebug", false);
    public static readonly SettingRef<bool> InspectDebug = new SettingRef<bool>("InspectDebug", false);


    private static readonly ISettingRef[] all = {
        ZoomInKey, ZoomOutKey, ZoomResetKey, ReloadKey, RPWheelKey,
        BloomEnabled, BloomIntensity, BloomThreshold, BloomKnee, BloomMultiplier, BloomRadius, BloomSpread, BloomLevels,
        ExplosionFlash, ExplosionDebris, ExplosionShock, BlastSmoke,
        MotionBlurEnabled, MotionBlurStrength,
        ColdEffects, ThoughtsEnabled, WakeIntroEnabled,
        ColdDebug, InspectDebug
    };

    private static bool bound;

    public static int Count => all.Length;

    public static IReadOnlyList<ISettingRef> All => all;

    public static void Bind() {
        if (bound) { return; }
        bound = true;
        for (int i = 0; i < all.Length; i++) {
            try {
                all[i].Bind();
            } catch (Exception error) {
                ModLog.Once($"{all[i].Name} config does not bind: {error.Message}");
            }
        }
    }

    public static void Release() {
        bound = false;
        for (int i = 0; i < all.Length; i++) {
            try {
                all[i].Drop();
            } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("ModSettings.1", noted); }
        }
    }
}
