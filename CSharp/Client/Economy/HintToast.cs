using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace PrimaryPackage.Economy {
    public static class HintToast {
        private const float FadeInSeconds = 0.3f;
        private const float FadeOutSeconds = 0.3f;
        private const int MaxQueued = 6;
        private sealed class Toast {
            public string Text;
            public Color Color;
            public byte Severity;
            public double StartTime;
            public float HoldSeconds;
            public string WrappedText = "";
            public Vector2 Size;
        }
        private static readonly Queue<Toast> queue = new Queue<Toast>();
        private static Toast current;
        private static bool IsImportant(byte severity) =>
            severity == HintToastNet.SeveritySuccess || severity == HintToastNet.SeverityError;
        public static void Show(string text, Color? color = null)
            => Enqueue(text, color ?? Color.White, HintToastNet.SeverityInfo);
        public static void ShowSeverity(string text, byte severity)
            => Enqueue(text, severity switch {
                HintToastNet.SeveritySuccess => Color.LightGreen,
                HintToastNet.SeverityError => Color.OrangeRed,
                _ => Color.White
            }, severity);
        private static void Enqueue(string text, Color color, byte severity) {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            string trimmed = text.Trim();
            if (IsDuplicate(trimmed, severity)) { return; }
            if (queue.Count >= MaxQueued) {
                if (!IsImportant(severity) || !TryEvictOldestInfo()) { return; }
            }
            queue.Enqueue(new Toast {
                Text = trimmed,
                Color = color,
                Severity = severity,
                HoldSeconds = MathHelper.Clamp(1.8f + trimmed.Length * 0.045f, 2.0f, 7.0f)
            });
        }
        private static bool IsDuplicate(string text, byte severity) {
            if (current != null && current.Severity == severity && current.Text == text) { return true; }
            foreach (Toast t in queue) {
                if (t.Severity == severity && t.Text == text) { return true; }
            }
            return false;
        }
        private static bool TryEvictOldestInfo() {
            var kept = new List<Toast>(queue.Count);
            bool evicted = false;
            foreach (Toast t in queue) {
                if (!evicted && !IsImportant(t.Severity)) { evicted = true; continue; }
                kept.Add(t);
            }
            if (!evicted) { return false; }
            queue.Clear();
            foreach (Toast t in kept) { queue.Enqueue(t); }
            return true;
        }
        internal static void Draw(SpriteBatch spriteBatch) {
            if (GameMain.GameSession == null) {
                queue.Clear();
                current = null;
                return;
            }
            if (Screen.Selected != GameMain.GameScreen) {
                if (current != null) { current.StartTime += Timing.Step; }
                return;
            }
            double now = Timing.TotalTime;
            if (current == null) {
                if (queue.Count == 0) { return; }
                current = queue.Dequeue();
                current.StartTime = now;
                if (queue.Count > 0) {
                    float scale = queue.Count == 1 ? 0.7f : 0.4f;
                    current.HoldSeconds *= scale;
                }
                GUIFont font = GUIStyle.Font;
                float maxWidth = GameMain.GraphicsWidth * 0.35f;
                current.WrappedText = font.WrapText(current.Text, maxWidth);
                current.Size = font.MeasureString(current.WrappedText);
            }
            float elapsed = (float)(now - current.StartTime);
            float total = FadeInSeconds + current.HoldSeconds + FadeOutSeconds;
            if (elapsed >= total) { current = null; return; }
            float alpha =
                elapsed < FadeInSeconds ? elapsed / FadeInSeconds
                : elapsed > total - FadeOutSeconds ? (total - elapsed) / FadeOutSeconds
                : 1.0f;
            GUIFont activeFont = GUIStyle.Font;
            Rectangle hotbar = HUDLayoutSettings.InventoryAreaLower;
            var center = new Vector2(GameMain.GraphicsWidth / 2f, hotbar.Y - current.Size.Y * 0.5f - 25);
            Vector2 origin = current.Size * 0.5f;
            activeFont.DrawString(spriteBatch, current.WrappedText, center + new Vector2(1, 1), Color.Black * (alpha * 0.8f),
                0.0f, origin, Vector2.One, SpriteEffects.None, 0.0f);
            activeFont.DrawString(spriteBatch, current.WrappedText, center, current.Color * alpha,
                0.0f, origin, Vector2.One, SpriteEffects.None, 0.0f);
        }
    }
    public sealed class HintToastClientPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.HudFrame.Over(PrimaryPackage.Core.HudFrame.Hint, GuiDrawPostfix);
            INet.Net.Receive(HintToastNet.NetId, OnHintReceived);
        }
        public void Release() {
        }
        public static void GuiDrawPostfix(Camera cam, SpriteBatch spriteBatch) => HintToast.Draw(spriteBatch);
        private static void OnHintReceived(IReadMessage netMessage) {
            byte severity = netMessage.ReadByte();
            string text = netMessage.ReadString();
            HintToast.ShowSeverity(text, severity);
        }
    }
}
