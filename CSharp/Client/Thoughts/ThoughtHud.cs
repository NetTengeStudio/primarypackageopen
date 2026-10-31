using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Thoughts {
    public static class ThoughtHud {
        private const float SamplePeriod = 0.25f;
        private const float GateDivisor = 4.0f;
        private const float CooldownNormal = 7.0f;
        private const float CooldownUrgent = 3.5f;

        private static readonly ThoughtProbe probe = new ThoughtProbe();
        private static readonly ThoughtTimeline timeline = new ThoughtTimeline();
        private static readonly ThoughtLayout layout = new ThoughtLayout();

        private static ThoughtRandom rng = new ThoughtRandom(0x5EED1234u);
        private static ThoughtSignals signals;
        private static Character? tracked;
        private static float sampleTimer = SamplePeriod;
        private static float gate;
        private static float cooldown;
        private static float painSmooth;
        private static float severitySmooth;
        private static int lastSolid = -1;
        private static ThoughtCategory lastCategory;
        private static ushort lastIndex = ushort.MaxValue;

        public static bool Enabled => PrimaryPackage.Core.ModSettings.ThoughtsEnabled.Value;

        public static ThoughtSignals Signals => signals;

        public static float Gate => gate;

        public static Color Tint(ThoughtCategory category, float pain) => ThoughtRenderer.Tint(category, pain);

        public static void Draw(SpriteBatch spriteBatch) {
            ThoughtClock.Tick();
            float dt = ThoughtClock.Step;
            Character character = Character.Controlled;

            if (!Enabled || GameMain.GameSession == null || Screen.Selected != GameMain.GameScreen ||
                character == null || character.Removed || character.IsDead) {
                if (tracked != null || timeline.IsActive) { Reset(); }
                return;
            }

            if (!ReferenceEquals(character, tracked)) {
                Reset();
                tracked = character;
            }

            Update(character, dt);

            if (!timeline.IsActive || timeline.CurrentFrame.Alpha <= 0.004f || ScriptToast.InspectActive ||
                CharacterHealth.OpenHealthWindow != null || GUI.InputBlockingMenuOpen) {
                return;
            }

            int solid = timeline.CurrentFrame.Solid;
            if (solid != lastSolid) {
                ThoughtAudio.PlayTick(timeline.CurrentCategory, lastSolid, solid, layout.Wrapped);
                lastSolid = solid;
            }

            ThoughtRenderer.Draw(spriteBatch, layout, timeline.CurrentCategory, timeline.CurrentFrame, painSmooth, severitySmooth);
        }

        public static void Reset() {
            tracked = null;
            timeline.Reset();
            gate = 0.0f;
            cooldown = 0.0f;
            sampleTimer = SamplePeriod;
            signals = default;
            lastSolid = -1;
            lastIndex = ushort.MaxValue;
            lastCategory = ThoughtCategory.None;
            painSmooth = 0.0f;
            severitySmooth = 0.0f;
            probe.Reset();
            ThoughtAudio.Reset();
        }

        public static bool Force(ThoughtCategory category) {
            int count = ThoughtPhrases.Count(category);
            if (count == 0) { return false; }
            timeline.Reset();
            StartThought(category, (ushort)rng.Below(count), Math.Max(severitySmooth, 0.35f));
            return true;
        }

        private static void Update(Character character, float dt) {
            if (cooldown > 0.0f) { cooldown -= dt; }

            sampleTimer += dt;
            if (sampleTimer >= SamplePeriod) {
                float step = sampleTimer;
                sampleTimer = 0.0f;
                signals = probe.Sample(character, step);
                Gauge(step);
            }

            painSmooth = Scalar.Approach(painSmooth, signals.Pain, 2.2f, dt);
            severitySmooth = Scalar.Approach(severitySmooth, signals.Severity, 2.0f, dt);

            if (signals.Has(ThoughtFlags.Unconscious)) {
                timeline.Cut();
            }

            if (!timeline.IsActive && timeline.TryPopNext(out var next)) {
                StartThought(next.Category, next.Index, next.Severity);
            }

            timeline.Update(dt);
        }

        private static void Gauge(float step) {
            float weight = ThoughtSelector.Weight(signals);
            if (weight <= 0.0f) {
                gate -= gate * Scalar.Decay(0.4f, step);
                return;
            }

            if (cooldown > 0.0f || timeline.QueueCount >= 2) { return; }

            gate += rng.Unit() * weight * step / GateDivisor;
            if (gate < 1.0f) { return; }
            gate = 0.0f;

            ThoughtCategory category = ThoughtSelector.Pick(signals, ref rng);
            if (category == ThoughtCategory.None) { return; }

            int count = ThoughtPhrases.Count(category);
            if (count == 0) { return; }

            ushort index = (ushort)rng.Below(count);
            if (category == lastCategory && index == lastIndex && count > 1) {
                index = (ushort)((index + 1) % count);
            }

            cooldown = signals.Pain > ThoughtSelector.PainAgony ? CooldownUrgent : CooldownNormal;
            Enqueue(category, index, signals.Severity);
        }

        private static void Enqueue(ThoughtCategory category, ushort index, float severity) {
            if (timeline.IsActive && timeline.CurrentCategory == category && timeline.CurrentIndex == index) { return; }
            if (!timeline.IsActive && timeline.QueueCount == 0) {
                StartThought(category, index, severity);
                return;
            }
            timeline.Enqueue(category, index, severity);
        }

        private static void StartThought(ThoughtCategory category, ushort index, float severity) {
            string text = ThoughtPhrases.Get(category, index);
            if (string.IsNullOrEmpty(text)) { return; }

            layout.Build(text, ThoughtRenderer.WrapWidth());
            int charCount = layout.Wrapped.Length;
            if (charCount == 0) { return; }

            timeline.Begin(category, index, severity, charCount, signals);
            lastCategory = category;
            lastIndex = index;
            lastSolid = -1;
        }
    }
}
