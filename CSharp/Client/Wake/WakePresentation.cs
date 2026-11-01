using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Inspect;
using PrimaryPackage.Zones;
using PrimaryPackage.Core;

namespace PrimaryPackage.Wake {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public enum WakePhase {
        None,
        Sleeping,
        Dots,
        Alarm
    }

    public static class WakePresentation {
        private const double ArmLifetime = 180.0;

        private static bool armed;
        private static double armedAt;
        private static bool roundRunning;

        private static Character owner;
        private static WakePhase phase;
        private static double lastFrameAt;

        private static readonly TypewriterPlayback playback = new TypewriterPlayback();
        private static string typeSoundPath;
        private static string alarmSoundPath;
        private static Barotrauma.Sounds.SoundChannel alarmChannel;

        public static bool Enabled { get; private set; } = true;

        public static WakePhase Phase => phase;

        public static bool Active => phase != WakePhase.None;

        public static bool IsLocked(Character character) => Active && character != null && character == owner;

        public static string Diag() =>
            $"фаза={phase}, ожидание раунда={armed}, раунд идёт={roundRunning}, вуаль={Veil():F2}";

        public static void SetEnabled(bool value) {
            Enabled = value;
            if (value) { return; }
            armed = false;
            Stop();
        }

        public static void Reset() {
            armed = false;
            roundRunning = false;
            Stop();
        }

        public static void ControlPrefix(Character character) {
            if (!IsLocked(character)) { return; }
            Character.DisableControls = true;
            WakeScreen.Hold();
        }

        public static void Update() {
            double now = Timing.TotalTime;
            float step = lastFrameAt <= 0.0 ? 0.0f : (float)(now - lastFrameAt);
            lastFrameAt = now;
            if (step > 0.25f) { step = 0.25f; }

            WatchRound();
            if (phase == WakePhase.None) {
                Resolve();
                return;
            }

            if (!Enabled || owner == null || owner.Removed || owner.IsDead || owner != Character.Controlled ||
                Screen.Selected != GameMain.GameScreen || WakeGate.Applies(owner)) {
                Stop();
                return;
            }

            playback.Advance(step);
            AnimateDots();
            if (playback.Elapsed < Duration()) { return; }
            Enter(phase + 1);
        }

        public static void Draw(SpriteBatch spriteBatch) {
            if (phase == WakePhase.None || GUI.PauseMenuOpen) { return; }

            float veil = Veil();
            if (veil > 0.002f) {
                GUI.DrawRectangle(spriteBatch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                    Color.Black * MathHelper.Clamp(veil, 0.0f, 1.0f), isFilled: true);
            }

            ToastRenderer.EnsureLayout(playback.Layout, playback.Spec);
            ToastState state = ToastTimeline.Evaluate(playback.Spec, (float)playback.Elapsed, playback.CharCount);
            TypewriterSound.Handle(playback, state);
            ToastRenderer.Render(spriteBatch, playback.Layout, playback.Spec, state, Center());
        }

        private static void AnimateDots() {
            if (phase != WakePhase.Sleeping) { return; }
            string next = WakeTexts.SleepingAt((float)playback.Elapsed);
            if (ReferenceEquals(next, playback.Spec.Text)) { return; }
            playback.SetText(next);
        }

        private static float Duration() {
            if (!playback.Spec.Typewriter) { return playback.Spec.Total(0); }
            ToastRenderer.EnsureLayout(playback.Layout, playback.Spec);
            return playback.Spec.Total(playback.CharCount);
        }

        private static void WatchRound() {
            bool running = GameMain.GameSession is { IsRunning: true };
            if (running == roundRunning) { return; }
            roundRunning = running;
            if (!running) {
                armed = false;
                Stop();
                return;
            }
            if (!Enabled) { return; }
            armed = true;
            armedAt = Timing.TotalTime;
        }

        private static void Resolve() {
            if (!armed) { return; }
            if (Timing.TotalTime - armedAt > ArmLifetime) {
                armed = false;
                return;
            }
            if (!Enabled || GameMain.GameSession == null || Screen.Selected != GameMain.GameScreen) { return; }
            Character controlled = Character.Controlled;
            if (controlled == null || controlled.Removed || controlled.IsDead || !controlled.IsPlayer) { return; }
            armed = false;
            if (WakeGate.Applies(controlled)) { return; }
            owner = controlled;
            PrimaryPackage.Core.SoundStore.Absolute(typeSoundPath ??= ModAssets.Resolve(WakeConfig.TypeSound));
            PrimaryPackage.Core.SoundStore.Absolute(alarmSoundPath ??= ModAssets.Resolve(WakeConfig.AlarmSound));
            Enter(WakePhase.Sleeping);
        }

        private static void Enter(WakePhase next) {
            if (next > WakePhase.Alarm) {
                Stop();
                return;
            }
            phase = next;
            playback.Begin(Spec(next));
            if (next == WakePhase.Alarm) {
                alarmChannel?.Dispose();
                alarmChannel = null;
                try {
                    Barotrauma.Sounds.Sound snd = PrimaryPackage.Core.SoundStore.Absolute(alarmSoundPath ??= ModAssets.Resolve(WakeConfig.AlarmSound));
                    if (snd != null) { alarmChannel = snd.Play(1.0f); }
                } catch (Exception e) {
                    DebugConsole.ThrowError("pp_wake: не удалось воспроизвести звук будильника", e);
                }
            }
        }

        private static void Stop() {
            phase = WakePhase.None;
            owner = null;
            WakeScreen.Give();
            alarmChannel?.Dispose();
            alarmChannel = null;
        }

        private static float Veil() {
            if (phase == WakePhase.None) { return 0.0f; }
            if (phase != WakePhase.Alarm) { return 1.0f; }
            float fade = playback.Spec.FadeOut;
            if (fade <= 0.0f) { return 1.0f; }
            float t = ((float)playback.Elapsed - (Duration() - fade)) / fade;
            return t <= 0.0f ? 1.0f : 1.0f - ToastTimeline.Ease(ZoneEasing.Плавно, t);
        }

        private static ToastSpec Spec(WakePhase target) {
            var spec = new ToastSpec {
                Text = WakeTexts.Of(target),
                TextColor = Color.White,
                Scale = WakeConfig.TextScale,
                FontName = WakeConfig.FontName,
                MaxWidthPercent = 70.0f,
                Easing = ZoneEasing.Плавно,
                Shadow = true,
                ShadowOffset = new Vector2(2.0f, 2.0f),
                ShadowAlpha = 0.8f,
                ShadowColor = Color.Black
            };
            switch (target) {
                case WakePhase.Sleeping:
                    spec.FadeIn = WakeConfig.FadeSec;
                    spec.Hold = Hold(WakeConfig.SleepSec, WakeConfig.FadeSec, 0.0f);
                    break;
                case WakePhase.Dots:
                    spec.FadeIn = WakeConfig.DotsFadeSec;
                    spec.Hold = Hold(WakeConfig.DotsSec, WakeConfig.DotsFadeSec, 0.0f);
                    break;
                default:
                    spec.Typewriter = true;
                    spec.CharInterval = WakeConfig.CharInterval;
                    spec.TypeVolume = 1.0f;
                    spec.TypeSound = typeSoundPath ??= ModAssets.Resolve(WakeConfig.TypeSound);
                    spec.Hold = WakeConfig.AlarmHoldSec;
                    spec.FadeOut = WakeConfig.FadeSec;
                    break;
            }
            return spec;
        }

        private static float Hold(float total, float fadeIn, float fadeOut) {
            float hold = total - fadeIn - fadeOut;
            return hold > 0.0f ? hold : 0.0f;
        }

        private static Vector2 Center() =>
            new Vector2(GameMain.GraphicsWidth * 0.5f, GameMain.GraphicsHeight * 0.5f);
    }

    public static class WakeTexts {
        private static string sleeping, dots, alarm;
        private static int languageVersion = -1;

        private static readonly string[] sleepingSteps = new string[4];
        private static string sleepingSource;

        public static string SleepingAt(float elapsed) {
            string source = Sleeping;
            if (!ReferenceEquals(sleepingSource, source)) {
                sleepingSource = source;
                string root = source.TrimEnd('.');
                for (int i = 0; i < sleepingSteps.Length; i++) { sleepingSteps[i] = root + new string('.', i); }
            }
            return sleepingSteps[(int)(elapsed / WakeConfig.DotIntervalSec) % sleepingSteps.Length];
        }

        public static string Sleeping => Cached(ref sleeping, "pp_wake.sleeping", "Вы спите...");
        public static string Dots => Cached(ref dots, "pp_wake.dots", "...");
        public static string Alarm => Cached(ref alarm, "pp_wake.alarm", "Вы слышите звук будильника.. Нужно вставать");

        public static string Of(WakePhase phase) => phase switch {
            WakePhase.Sleeping => Sleeping,
            WakePhase.Dots => Dots,
            _ => Alarm
        };

        private static string Cached(ref string slot, string tag, string fallback) {
            if (languageVersion != TextManager.LanguageVersion) {
                languageVersion = TextManager.LanguageVersion;
                sleeping = dots = alarm = null;
            }
            if (slot != null) { return slot; }
            LocalizedString text = TextManager.Get(tag);
            slot = text.Loaded ? text.Value : fallback;
            return slot;
        }
    }
}
