using PrimaryPackage.Core;

namespace PrimaryPackage.MotionBlur;

public static class MotionBlurPrefs {
    private const float ShutterSeconds = 1.0f / 60.0f;
    private const float MaxTapSpacing = 2.0f;
    private const float CutFraction = 0.25f;

    public static bool Enabled => ModSettings.MotionBlurEnabled.Value;

    public static float Shutter => ShutterSeconds * ModSettings.MotionBlurStrength.Value;

    public static float MinimumPixels => 1.5f;

    public static float SoftPixels => 5.0f;

    public static float MaximumPixels(int taps) => (taps - 1) * MaxTapSpacing;

    public static float CutPixels(int screenHeight) => screenHeight * CutFraction;
}
