#nullable enable
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.RPWheel;

internal static class Theme {
    public static readonly Color PanelDark = PrimaryPackage.GUI.GUITheme.Panel;
    public static readonly Color NanoGold = PrimaryPackage.GUI.GUITheme.Text;
    public static readonly Color NanoGoldBright = new(255, 255, 255, 255);
    public static readonly Color ButtonDefault = PrimaryPackage.GUI.GUITheme.Control;
    public static readonly Color ButtonHovered = PrimaryPackage.GUI.GUITheme.Hover;
    public static readonly Color CloseIdle = new(120, 130, 150, 255);
    public static readonly Color CloseHover = new(255, 100, 100, 255);
    public static readonly Color BorderDefault = PrimaryPackage.GUI.GUITheme.Border;
    public static readonly Color BorderHover = PrimaryPackage.GUI.GUITheme.Accent;
    public static readonly Color Separator = new(128, 128, 128, 128);
    public static readonly Color GlowBase = PrimaryPackage.GUI.GUITheme.Accent;
    public static readonly Color TextShadow = new(0, 0, 0, 140);
    public const float BorderThickness = 2;
    public const float SeparatorThickness = 1.5f;
    public const float GlowExpandOuter = 12;
    public const float GlowExpandInner = 6;
    public const float GlowAlphaMax = 0.15f;
    public const float CrossLineWidth = 0.072f;
    public const float CrossSize = 0.30f;
    private static ScalableFont? labelFont;
    private static GraphicsDevice? device;
    public static ScalableFont? LabelFont {
        get {
            GraphicsDevice? current = GameMain.Instance?.GraphicsDevice;
            if (device != current) {
                Dispose();
                device = current;
                if (device != null) { device.DeviceReset += Reset; }
            }
            return labelFont ??= PrimaryPackage.Fonts.FontRegistry.Get("noto_sans_regular".ToIdentifier(), 16, true);
        }
    }

    public static void Initialize() => _ = LabelFont;

    private static void Reset(object? sender, System.EventArgs args) => labelFont = null;

    public static void Dispose() {
        if (device != null) { device.DeviceReset -= Reset; }
        device = null;
        labelFont = null;
    }
}
