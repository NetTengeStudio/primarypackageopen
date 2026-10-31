using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Inspect;

namespace PrimaryPackage.Zones {
    public static class ScriptToast {
        private const int MaxQueued = 4;
        private const float LoopGap = 0.8f;

        private static readonly Queue<ToastSpec> queue = new Queue<ToastSpec>();
        private static readonly TypewriterPlayback game = new TypewriterPlayback();
        private static bool gameActive;
        private static readonly TypewriterPlayback preview = new TypewriterPlayback();
        private static bool previewActive;
        private static ITypewriterPreview previewSource;
        private static double previewStamp;
        private static LocalizedString previewCaption;
        private static readonly TypewriterPlayback inspect = new TypewriterPlayback();
        private static bool inspectActive;
        private static ToastSpec pendingInspect;
        private static bool hasPendingInspect;

        public static bool InspectActive => inspectActive;

        internal static string Diag() =>
            $"toast: active={inspectActive} chars={inspect.CharCount} font={inspect.Layout.Font.HasValue} " +
            $"len={inspect.Spec.Text?.Length ?? -1} elapsed={inspect.Elapsed:F2} " +
            $"total={inspect.Spec.Total(inspect.CharCount):F2} anchorY={AnchorBottomY(inspect.Spec):F0}";

        public static void Show(in ToastSpec spec) {
            if (string.IsNullOrWhiteSpace(spec.Text)) { return; }
            if (gameActive && game.Spec.Text == spec.Text) { return; }
            foreach (ToastSpec s in queue) {
                if (s.Text == spec.Text) { return; }
            }
            if (queue.Count >= MaxQueued) { return; }
            queue.Enqueue(spec);
        }

        public static void ShowInspect(in ToastSpec spec, bool allowSkip) {
            if (string.IsNullOrWhiteSpace(spec.Text)) { return; }
            if (!inspectActive) {
                BeginInspect(spec);
                return;
            }
            ToastRenderer.EnsureLayout(inspect.Layout, inspect.Spec);
            if (inspect.Spec.Text == spec.Text) {
                if (allowSkip) { inspect.Skip(); }
                return;
            }
            pendingInspect = spec;
            hasPendingInspect = true;
            inspect.BeginFadeOut();
        }

        public static void StopInspect() {
            if (!inspectActive) { return; }
            hasPendingInspect = false;
            ToastRenderer.EnsureLayout(inspect.Layout, inspect.Spec);
            inspect.BeginFadeOut();
        }

        private static void BeginInspect(in ToastSpec spec) {
            inspect.Begin(spec);
            inspectActive = true;
        }

        public static void SetPreview(ITypewriterPreview source) {
            previewSource = source;
            previewStamp = Timing.TotalTime;
        }

        internal static void Draw(SpriteBatch spriteBatch) {
            float step = InspectClock.Step;
            if (Screen.Selected == GameMain.SubEditorScreen) {
                preview.Advance(step);
                DrawPreview(spriteBatch);
                return;
            }
            previewActive = false;
            previewSource = null;
            if (GameMain.GameSession == null) {
                queue.Clear();
                gameActive = false;
                inspectActive = false;
                hasPendingInspect = false;
                return;
            }
            if (Screen.Selected != GameMain.GameScreen) { return; }
            if (inspectActive) {
                inspect.Advance(step);
                if (RunPlayback(spriteBatch, inspect, loop: false)) {
                    InspectRuntime.NoteProgress(inspect);
                } else {
                    inspectActive = false;
                    if (hasPendingInspect) {
                        hasPendingInspect = false;
                        BeginInspect(pendingInspect);
                    }
                }
                return;
            }
            if (!gameActive) {
                if (queue.Count == 0) { return; }
                game.Begin(queue.Dequeue());
                gameActive = true;
            }
            game.Advance(step);
            if (!RunPlayback(spriteBatch, game, loop: false)) { gameActive = false; }
        }

        private static void DrawPreview(SpriteBatch spriteBatch) {
            if (previewSource == null || previewSource.PreviewEntity == null || previewSource.PreviewEntity.Removed ||
                Timing.TotalTime - previewStamp > 0.25) {
                previewActive = false;
                previewSource = null;
                return;
            }
            ToastSpec spec = previewSource.BuildSpec();
            if (!previewActive || !spec.VisualsEqual(preview.Spec)) {
                preview.Begin(spec);
                previewActive = true;
            }
            RunPlayback(spriteBatch, preview, loop: true);
            DrawPreviewCaption(spriteBatch, preview);
        }

        private static void DrawPreviewCaption(SpriteBatch spriteBatch, TypewriterPlayback p) {
            previewCaption ??= TextManager.Get("pp_scriptzone.preview").Fallback("Предпросмотр скрипта");
            GUIFont font = GUIStyle.SmallFont;
            string caption = previewCaption.Value;
            Vector2 size = font.MeasureString(caption);
            float top = AnchorBottomY(p.Spec) - p.Layout.UnscaledSize.Y * p.Layout.DrawScale -
                        (p.Spec.Background ? p.Spec.BackgroundPadding : 0.0f) - size.Y - 8.0f;
            var pos = new Vector2(GameMain.GraphicsWidth * 0.5f - size.X * 0.5f, top);
            font.DrawString(spriteBatch, caption, pos + Vector2.One, Color.Black * 0.6f);
            font.DrawString(spriteBatch, caption, pos, Color.White * 0.65f);
        }

        private static float AnchorBottomY(in ToastSpec spec) {
            Rectangle hotbar = HUDLayoutSettings.InventoryAreaLower;
            float bottom = hotbar.Height > 0 ? hotbar.Y : GameMain.GraphicsHeight * 0.8f;
            return bottom - spec.VerticalOffset;
        }

        private static bool RunPlayback(SpriteBatch spriteBatch, TypewriterPlayback p, bool loop) {
            ToastRenderer.EnsureLayout(p.Layout, p.Spec);
            int charCount = p.CharCount;
            float total = p.Spec.Total(charCount);
            if (loop) {
                if (total <= 0.05f) { total = 0.05f; }
                p.Wrap(total + LoopGap);
            }
            float elapsed = (float)p.Elapsed;
            if (elapsed >= total) {
                if (!loop) { return false; }
                p.ShownChars = 0;
                return true;
            }
            ToastState st = ToastTimeline.Evaluate(p.Spec, elapsed, charCount);
            TypewriterSound.Handle(p, st);
            ToastRenderer.Render(spriteBatch, p.Layout, p.Spec, st,
                new Vector2(GameMain.GraphicsWidth * 0.5f, AnchorBottomY(p.Spec)));
            return true;
        }
    }

    public sealed class ScriptToastPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.HudFrame.Over(PrimaryPackage.Core.HudFrame.Script, GuiDrawPostfix);
        }


        public void Release() {
        }

        public static void GuiDrawPostfix(Camera cam, SpriteBatch spriteBatch) => ScriptToast.Draw(spriteBatch);
    }
}
