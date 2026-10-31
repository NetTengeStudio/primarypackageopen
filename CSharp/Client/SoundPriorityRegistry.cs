using System.Collections.Concurrent;
using Barotrauma;
using Barotrauma.Sounds;
namespace SoundPolyphony {
    public static class SoundPriority {
        public const int Background = 0;
        public const int Ui = 1;
        public const int Gameplay = 2;
        public const int Critical = 3;
    }
    public static class SoundPriorityRegistry {
        private static readonly ConcurrentDictionary<string, int> byPath = new ConcurrentDictionary<string, int>();
        private static readonly Identifier UiCat = "ui".ToIdentifier();
        private static readonly Identifier MusicCat = "music".ToIdentifier();
        private static readonly Identifier WaterCat = "waterambience".ToIdentifier();
        private static readonly Identifier AmbientCat = "ambient".ToIdentifier();
        private static readonly Identifier HullCat = "hull".ToIdentifier();
        public static void Register(string fullPath, int priority) {
            if (!string.IsNullOrEmpty(fullPath)) { byPath[fullPath] = priority; }
        }
        public static int Resolve(SoundChannel channel) {
            Sound sound = channel?.Sound;
            if (sound == null) { return SoundPriority.Gameplay; }
            if (byPath.TryGetValue(sound.Filename ?? "", out int p)) { return p; }
            Identifier cat = channel.Category;
            if (cat == UiCat) { return SoundPriority.Ui; }
            if (cat == MusicCat || cat == WaterCat || cat == AmbientCat || cat == HullCat) { return SoundPriority.Background; }
            return SoundPriority.Gameplay;
        }
        public static void Clear() => byPath.Clear();
    }
}
