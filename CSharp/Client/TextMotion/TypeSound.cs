using System;
using Barotrauma.Sounds;
using PrimaryPackage.Core;

namespace PrimaryPackage.TextMotion;

internal static class TypeSound {
    private const string Path = "Content/Sounds/UI/type_click.ogg";
    private static TypeClickBudget budget;
    private static Sound sound;
    private static SoundChannel voice;
    private static bool failed;
    internal static void Warm() {
        if (failed || TypeSettings.ClickGain <= 0 || TypeSettings.Pace.Instant) { return; }
        try { sound = SoundStore.Get(Path); }
        catch (Exception error) { Fail(error); }
    }

    internal static void Present(ref TypeClickCursor cursor, TypeText text, int limit, float age, float visibility, double now) {
        bool due = cursor.Advance(text, limit, age);
        float gain = TypeSettings.ClickGain;
        if (!due || failed || visibility < 0.05f || gain <= 0 || !budget.Try(now)) { return; }
        try {
            if (sound == null || sound.Disposed) { Warm(); }
            if (sound == null || sound.Disposed || sound.Loading || voice != null && voice.IsPlaying) { return; }
            voice?.Dispose(); voice = null;
            voice = sound.Play(gain * visibility, SoundManager.SoundCategoryUi);
        } catch (Exception error) {
            Fail(error);
        }
    }

    internal static void Release() {
        voice?.Dispose(); voice = null; sound = null; budget = default; failed = false;
    }

    private static void Fail(Exception error) {
        failed = true;
        ModLog.Error("TextMotion audio failed; clicks disabled until reset: " + error);
    }
}
