using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public interface IInspectSettings : ITypewriterConfig {
        InspectMode Mode { get; }
        bool PromptEnabled { get; }
        string PromptText { get; }
        Color PromptColor { get; }
        float PromptFadeIn { get; }
        float PromptFadeOut { get; }
        ZoneEasing PromptEasingIn { get; }
        ZoneEasing PromptEasingOut { get; }
        float PromptRise { get; }
        float PromptOffsetY { get; }
        bool PromptScaleIn { get; }
        float PromptRange { get; }
        float HoldSeconds { get; }
        bool InspectOnce { get; }
        float DarkThreshold { get; }
        string DarkText { get; }
        bool AllowSkip { get; }
        float WorldMaxWidth { get; }
        float WorldOffsetX { get; }
        float WorldOffsetY { get; }
        float WorldScale { get; }
        float MaxViewDistance { get; }
        bool RequireLineOfSight { get; }
        float DepthOffset { get; }
    }
}
