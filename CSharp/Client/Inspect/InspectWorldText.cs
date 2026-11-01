using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public static class InspectWorldText {
        private const float VisibilityInterval = 0.25f;
        private const float FadeMargin = 0.25f;
        private const float LoopGap = 0.8f;

        private static readonly TypewriterPlayback playback = new TypewriterPlayback();
        private static readonly TypewriterPlayback preview = new TypewriterPlayback();
        private static InspectTarget current;
        private static InspectTarget pending;
        private static ToastSpec pendingSpec;
        private static InspectTarget previewTarget;
        private static bool active;
        private static bool hasPending;
        private static bool previewActive;
        private static bool occluded;
        private static bool drawnThisFrame;
        private static float distanceFade = 1.0f;
        private static double nextVisibilityCheck;

        internal static string Diag() =>
            $"world: active={active} occl={occluded} fade={distanceFade:F2} drawn={drawnThisFrame} " +
            $"chars={playback.CharCount} font={playback.Layout.Font.HasValue} " +
            $"len={playback.Spec.Text?.Length ?? -1} elapsed={playback.Elapsed:F2} " +
            $"total={playback.Spec.Total(playback.CharCount):F2}";

        public static bool Playing => active;

        public static void Show(in InspectTarget target, in ToastSpec spec) {
            if (!active || !current.Valid) {
                Begin(target, spec);
                return;
            }
            if (target.Same(current)) {
                if (target.Settings.AllowSkip) { playback.Skip(); }
                return;
            }
            pending = target;
            pendingSpec = spec;
            hasPending = true;
            playback.BeginFadeOut();
        }

        public static void FadeOut() {
            if (!active) { return; }
            hasPending = false;
            playback.BeginFadeOut();
        }

        public static void Stop() {
            active = false;
            hasPending = false;
            current = default;
            pending = default;
        }

        private static void Begin(in InspectTarget target, in ToastSpec spec) {
            current = target;
            playback.Begin(spec);
            EnsureLayout(playback, current);
            active = true;
            occluded = false;
            distanceFade = 1.0f;
            nextVisibilityCheck = 0.0;
        }

        private static void EnsureLayout(TypewriterPlayback target, in InspectTarget source) {
            float scale = MathHelper.Max(source.Settings.WorldScale, 0.01f);
            ToastRenderer.EnsureLayout(target.Layout, target.Spec, source.Settings.WorldMaxWidth / scale);
        }

        internal static void Tick() {
            drawnThisFrame = false;
            float step = InspectClock.Step;
            if (Screen.Selected == GameMain.SubEditorScreen) { preview.Advance(step); }
            if (!active) { return; }
            Character character = Character.Controlled;
            if (character == null || !current.Valid) {
                Stop();
                return;
            }
            if (Screen.Selected != GameMain.GameScreen) { return; }
            playback.Advance(step);
            if (playback.Elapsed >= playback.Spec.Total(playback.CharCount)) {
                active = false;
                if (hasPending) {
                    hasPending = false;
                    Begin(pending, pendingSpec);
                }
                return;
            }
            distanceFade = DistanceFade(character, current);
            double now = Timing.TotalTime;
            if (now < nextVisibilityCheck) { return; }
            nextVisibilityCheck = now + VisibilityInterval;
            occluded = current.Settings.RequireLineOfSight && distanceFade > 0.0f &&
                       !character.CanSeeTarget(current.Entity, seeThroughWindows: true);
        }

        private static float DistanceFade(Character character, in InspectTarget target) {
            float max = MathHelper.Max(target.Settings.MaxViewDistance, 1.0f);
            float distance = Vector2.Distance(character.WorldPosition, target.Entity.WorldPosition);
            return MathHelper.Clamp((max - distance) / (max * FadeMargin), 0.0f, 1.0f);
        }

        internal static void DrawEntity(SpriteBatch spriteBatch, MapEntity entity, bool editing) {
            if (editing) {
                DrawPreview(spriteBatch, entity);
                return;
            }
            if (drawnThisFrame || !active || !current.Valid || occluded) { return; }
            if (!ReferenceEquals(entity, current.Entity) || Screen.Selected != GameMain.GameScreen) { return; }
            Character character = Character.Controlled;
            if (character == null) { return; }
            float fade = DistanceFade(character, current);
            if (fade <= 0.0f) { return; }
            drawnThisFrame = true;
            EnsureLayout(playback, current);
            ToastState state = ToastTimeline.Evaluate(playback.Spec, (float)playback.Elapsed, playback.CharCount);
            TypewriterSound.Handle(playback, state);
            InspectRuntime.NoteProgress(playback);
            state.Alpha *= fade;
            Render(spriteBatch, playback, current, state);
        }

        private static void DrawPreview(SpriteBatch spriteBatch, MapEntity entity) {
            if (Screen.Selected != GameMain.SubEditorScreen) { return; }
            if (!entity.IsSelected || MapEntity.SelectedList.Count != 1) { return; }
            InspectTarget target = InspectSource.For(entity);
            if (!target.Valid) { return; }
            if (target.Settings.Mode != InspectMode.SelfInspect) {
                ScriptToast.SetPreview(InspectSource.Preview(target));
                return;
            }
            ToastSpec spec = target.BuildSpec();
            if (!previewActive || !target.Same(previewTarget) || !spec.VisualsEqual(preview.Spec)) {
                previewTarget = target;
                preview.Begin(spec);
                previewActive = true;
            }
            EnsureLayout(preview, previewTarget);
            int charCount = preview.CharCount;
            float total = MathHelper.Max(preview.Spec.Total(charCount), 0.05f);
            preview.Wrap(total + LoopGap);
            float elapsed = (float)preview.Elapsed;
            if (elapsed >= total) {
                preview.ShownChars = 0;
                return;
            }
            ToastState state = ToastTimeline.Evaluate(preview.Spec, elapsed, charCount);
            TypewriterSound.Handle(preview, state);
            Render(spriteBatch, preview, previewTarget, state);
        }

        private static void Render(SpriteBatch spriteBatch, TypewriterPlayback source, in InspectTarget target, in ToastState state) {
            IInspectSettings settings = target.Settings;
            MapEntity entity = target.Entity;
            float depth = MathHelper.Clamp(entity.SpriteDepth - settings.DepthOffset, 0.0f, 1.0f);
            var anchor = new Vector2(entity.DrawPosition.X + settings.WorldOffsetX,
                                     -(entity.DrawPosition.Y + settings.WorldOffsetY));
            ToastRenderer.Render(spriteBatch, source.Layout, source.Spec, state, anchor,
                MathHelper.Max(settings.WorldScale, 0.01f), depth);
        }
    }
}
