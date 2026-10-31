using Barotrauma;
using Barotrauma.Sounds;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public sealed class TypewriterPlayback {
        public ToastSpec Spec;
        public double Elapsed;
        public int ShownChars;
        public readonly ToastLayout Layout = new ToastLayout();

        public int CharCount => Layout.Wrapped.Length;

        public void Begin(in ToastSpec spec) {
            Spec = spec;
            Elapsed = 0.0;
            ShownChars = 0;
        }

        public void Advance(float step) => Elapsed += step;

        public void SetText(string text) => Spec.Text = text;

        public void Wrap(float period) {
            if (period > 0.0f && Elapsed >= period) { Elapsed %= period; }
        }

        public void Skip() {
            float inDuration = Spec.InDuration(CharCount);
            if (Elapsed < inDuration) { Elapsed = inDuration; }
        }

        public void BeginFadeOut() {
            float target = Spec.Total(CharCount) - Spec.FadeOut;
            if (Elapsed < target) { Elapsed = target; }
        }
    }

    public static class TypewriterSound {
        private const double MinInterval = 0.03;
        private static double next;

        public static void Handle(TypewriterPlayback playback, in ToastState state) {
            if (!playback.Spec.Typewriter) { return; }
            if (state.VisibleChars < playback.ShownChars) {
                playback.ShownChars = state.VisibleChars;
                return;
            }
            if (state.VisibleChars == playback.ShownChars) { return; }
            string wrapped = playback.Layout.Wrapped;
            bool audible = false;
            for (int i = playback.ShownChars; i < state.VisibleChars && i < wrapped.Length; i++) {
                if (!char.IsWhiteSpace(wrapped[i])) { audible = true; break; }
            }
            playback.ShownChars = state.VisibleChars;
            if (!audible || playback.Spec.TypeVolume <= 0.0f || string.IsNullOrEmpty(playback.Spec.TypeSound)) { return; }
            double now = Timing.TotalTime;
            if (now < next) { return; }
            next = now + MinInterval;
            Sound sound = PrimaryPackage.Core.SoundStore.Absolute(playback.Spec.TypeSound);
            if (sound != null && !sound.Disposed && !sound.Loading) {
                sound.Play(playback.Spec.TypeVolume, SoundManager.SoundCategoryUi);
            }
        }
    }
}
