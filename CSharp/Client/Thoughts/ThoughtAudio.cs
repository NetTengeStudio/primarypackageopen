using System;
using Barotrauma;
using Barotrauma.Sounds;
using PrimaryPackage.Core;

namespace PrimaryPackage.Thoughts {
    public static class ThoughtAudio {
        private const float TickInterval = 0.045f;
        private const float TickVolume = 0.15f;

        private static string soundPath;
        private static bool soundMissing;
        private static float lastTickTime;

        public static void Reset() {
            lastTickTime = 0.0f;
        }

        public static void PlayTick(ThoughtCategory category, int fromIndex, int toIndex, string text) {
            if (soundMissing || category == ThoughtCategory.Agony || string.IsNullOrEmpty(text)) { return; }
            if (toIndex <= fromIndex) { return; }

            bool hasAudibleChar = false;
            int start = Math.Max(0, fromIndex);
            int end = Math.Min(text.Length, toIndex);
            for (int i = start; i < end; i++) {
                if (!char.IsWhiteSpace(text[i])) {
                    hasAudibleChar = true;
                    break;
                }
            }
            if (!hasAudibleChar) { return; }

            float now = ThoughtClock.Time;
            if (now - lastTickTime < TickInterval) { return; }
            lastTickTime = now;

            if (soundPath == null) {
                soundPath = ModAssets.Resolve("Content/Sounds/UI/pp_type.ogg");
                if (!Barotrauma.IO.File.Exists(soundPath)) {
                    soundMissing = true;
                    return;
                }
            }

            Sound sound = SoundStore.Absolute(soundPath);
            if (sound == null || sound.Disposed || sound.Loading) { return; }
            sound.Play(TickVolume, SoundManager.SoundCategoryUi);
        }
    }
}
