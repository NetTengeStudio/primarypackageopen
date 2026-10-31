using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public static class TypewriterSpec {
        public static ToastSpec Build(ITypewriterConfig cfg, ContentPackage package, TypewriterCaches caches) => new ToastSpec {
            Text = TypewriterText.Cached(cfg.Text, ref caches.RawText, ref caches.ResolvedText),
            TextColor = cfg.TextColor,
            Scale = cfg.TextScale,
            FontName = cfg.FontName,
            MaxWidthPercent = cfg.MaxWidthPercent,
            VerticalOffset = cfg.VerticalOffset,
            Easing = cfg.Easing,
            StartDelay = cfg.StartDelay,
            FadeIn = cfg.FadeInTime,
            Hold = cfg.HoldTime,
            FadeOut = cfg.FadeOutTime,
            Slide = cfg.Slide,
            SlideOffset = cfg.SlideOffset,
            ScaleIn = cfg.ScaleIn,
            Typewriter = cfg.Typewriter,
            CharInterval = cfg.CharInterval,
            TypeVolume = cfg.TypeSoundVolume,
            TypeSound = TypewriterText.ResolvePath(package, cfg.TypeSoundFile, ref caches.RawTypeSound, ref caches.ResolvedTypeSound),
            Shadow = cfg.ShadowEnabled,
            ShadowOffset = new Vector2(cfg.ShadowOffsetX, cfg.ShadowOffsetY),
            ShadowBlur = cfg.ShadowBlur,
            ShadowAlpha = cfg.ShadowAlpha,
            ShadowColor = cfg.ShadowColor,
            Outline = cfg.OutlineEnabled,
            OutlineColor = cfg.OutlineColor,
            Background = cfg.BackgroundEnabled,
            BackgroundColor = cfg.BackgroundColor,
            BackgroundAlpha = cfg.BackgroundAlpha,
            BackgroundPadding = cfg.BackgroundPadding
        };
    }
}
