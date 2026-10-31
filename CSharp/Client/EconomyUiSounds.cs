using System.IO;
using Barotrauma;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using SoundPolyphony;
namespace PrimaryPackage.Economy {
    public static class EconomyUiSounds {
        private const string OpenFile = "Content/Sounds/UI/twobeep.ogg";
        private const string ClickFile = "Content/Sounds/Interactions/short_print_and_rip.ogg";
        private static Sound Load(string file, int priority) {
            string p = PrimaryPackage.Core.ModAssets.Resolve(file);
            if (p.Length == 0 || !File.Exists(p)) { return null; }
            Sound s = PrimaryPackage.Core.SoundStore.Absolute(p);
            if (s != null) { SoundPriorityRegistry.Register(p, priority); }
            return s;
        }
        public static void PlayOpen(Vector2 worldPos) {
            Sound s = Load(OpenFile, SoundPriority.Ui);
            if (s != null) { SoundPlayer.PlaySound(s, worldPos, volume: 0.55f, range: 400f); }
        }
        public static void PlayClick(Vector2 worldPos) {
            Sound s = Load(ClickFile, SoundPriority.Ui);
            if (s != null) { SoundPlayer.PlaySound(s, worldPos, volume: 0.5f, range: 350f); }
        }
    }
}
