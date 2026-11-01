using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public interface ITypewriterConfig {
        string Text { get; }
        Color TextColor { get; }
        float TextScale { get; }
        string FontName { get; }
        float MaxWidthPercent { get; }
        float VerticalOffset { get; }
        ZoneEasing Easing { get; }
        float StartDelay { get; }
        float FadeInTime { get; }
        float HoldTime { get; }
        float FadeOutTime { get; }
        SlideFrom Slide { get; }
        float SlideOffset { get; }
        bool ScaleIn { get; }
        bool Typewriter { get; }
        float CharInterval { get; }
        float TypeSoundVolume { get; }
        string TypeSoundFile { get; }
        bool ShadowEnabled { get; }
        float ShadowOffsetX { get; }
        float ShadowOffsetY { get; }
        float ShadowBlur { get; }
        float ShadowAlpha { get; }
        Color ShadowColor { get; }
        bool OutlineEnabled { get; }
        Color OutlineColor { get; }
        bool BackgroundEnabled { get; }
        Color BackgroundColor { get; }
        float BackgroundAlpha { get; }
        float BackgroundPadding { get; }
    }
}
