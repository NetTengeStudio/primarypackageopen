using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace PrimaryPackage.Economy {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public partial class OrderCheckerComponent : IDrawableComponent {
        private struct Row { public int Id; public string Name; public OrderState State; public float Remaining; }
        private readonly List<Row> rows = new List<Row>();
        private double localClockAtSync;
        private double lastRequestTime = -100.0;
        private double nextDisplayRefresh;
        private const double DisplayRefreshInterval = 0.25;
        private GUIListBox list;
        private GUITextBlock emptyLabel;
        partial void InitProjSpecific() {
            BuildUI();
            IsActive = true;
        }
        private void BuildUI() {
            if (GuiFrame == null) { return; }
            GuiFrame.ClearChildren();
            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), GuiFrame.RectTransform, Anchor.Center))
                { Stretch = true, RelativeSpacing = 0.02f };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), content.RectTransform),
                OrderText.Get("pp.order.checker.title", "Чекер Заказов"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), content.RectTransform),
                OrderText.Get("pp.order.checker.subtitle", "Активные наряды и время до истечения"), font: GUIStyle.Font, textAlignment: Alignment.Center);
            list = new GUIListBox(new RectTransform(new Vector2(1f, 0.72f), content.RectTransform));
            emptyLabel = new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), content.RectTransform),
                "", textAlignment: Alignment.Center);
        }
        public override bool Select(Character character) {
            if (character == Character.Controlled) { EconomyUiSounds.PlayOpen(item.WorldPosition); RequestRefresh(); }
            return base.Select(character);
        }
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            double now = Timing.TotalTime;
            bool isUiVisible = GuiFrame is { Visible: true };
            float pollInterval = 20f;
            bool shouldPoll = false;
            if (isUiVisible) {
                pollInterval = 3f;
                shouldPoll = true;
            }
            else if (Character.Controlled != null) {
                const float MAX_VISIBLE_RANGE_SQ = 1500f * 1500f;
                if (Vector2.DistanceSquared(Character.Controlled.WorldPosition, item.WorldPosition) < MAX_VISIBLE_RANGE_SQ) {
                    shouldPoll = true;
                }
            }
            if (shouldPoll && now - lastRequestTime > pollInterval) {
                RequestRefresh();
            }
            if (isUiVisible && now >= nextDisplayRefresh) {
                nextDisplayRefresh = now + DisplayRefreshInterval;
                RefreshDisplay();
            }
        }
        private void RequestRefresh() {
            lastRequestTime = Timing.TotalTime;
            if (GameMain.Client != null) { item.CreateClientEvent(this, new RefreshRequestEventData()); }
            else { RefreshDisplay(); }
        }
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData) {
            msg.WriteByte(0);
        }
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            int count = msg.ReadByte();
            rows.Clear();
            for (int i = 0; i < count; i++) {
                Row r;
                r.Id = msg.ReadInt32();
                r.Name = msg.ReadString();
                r.State = (OrderState)msg.ReadByte();
                r.Remaining = msg.ReadSingle();
                rows.Add(r);
            }
            localClockAtSync = Timing.TotalTime;
            RefreshDisplay();
        }
        private void RefreshDisplay() {
            if (list == null) { return; }
            list.Content.ClearChildren();
            if (GameMain.Client == null) {
                if (emptyLabel != null) { emptyLabel.Text = OrderText.Get("pp.order.checker.offline", "Доступно только в сетевой игре."); }
                return;
            }
            if (rows.Count == 0) {
                if (emptyLabel != null) { emptyLabel.Text = OrderText.Get("pp.order.checker.no_active", "Активных нарядов нет."); }
                return;
            }
            if (emptyLabel != null) { emptyLabel.Text = ""; }
            double elapsed = Timing.TotalTime - localClockAtSync;
            foreach (Row r in rows) {
                double remaining = Math.Max(0.0, r.Remaining - elapsed);
                Color col = remaining <= 0.0 ? Color.OrangeRed
                    : (remaining < 120.0 ? new Color(255, 200, 80) : Color.LightGreen);
                string stateName = r.State.DisplayName();
                string timeText = OrderCountdown.Format(remaining);
                string line = OrderText.Format("pp.order.checker.row",
                    $"Наряд №{r.Id}, {r.Name}\n{stateName} · {timeText}",
                    ("[num]", r.Id.ToString()), ("[name]", r.Name), ("[state]", stateName), ("[time]", timeText));
                var block = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), list.Content.RectTransform),
                    line, textColor: col, wrap: true) { CanBeFocused = false };
                GuiRowFit.FitWrappedRow(list, block);
            }
        }
        private static readonly Color LampYellow = new Color(255, 200, 60);
        private static readonly Color LampGreen = new Color(70, 220, 90);
        private static readonly Color LampRed = new Color(240, 70, 60);
        public Vector2 DrawSize => new Vector2(220f, 220f);
        public void Draw(SpriteBatch spriteBatch, bool editing = false, float itemDepth = -1, Color? overrideColor = null) {
            if (item.ParentInventory != null) { return; }
            if (!TryGetLampColor(out Color col, out bool blink)) { return; }
            if (blink) {
                float t = (float)((Math.Sin(Timing.TotalTime * 6.0) + 1.0) * 0.5);
                col *= 0.45f + 0.55f * t;
            }
            float cx = item.DrawPosition.X;
            float cy = -item.DrawPosition.Y;
            float w = item.Rect.Width, h = item.Rect.Height;
            var pos = new Vector2(cx + w * 0.30f, cy - h * 0.28f);
            float size = Math.Max(4f, h * 0.13f);
            float depth = item.SpriteDepth - 0.0002f;
            spriteBatch.Draw(GUI.WhiteTexture, pos, null, col * 0.35f, 0f, new Vector2(0.5f, 0.5f), new Vector2(size * 1.9f), SpriteEffects.None, depth);
            spriteBatch.Draw(GUI.WhiteTexture, pos, null, col, 0f, new Vector2(0.5f, 0.5f), new Vector2(size), SpriteEffects.None, depth - 0.00001f);
        }
        private bool TryGetLampColor(out Color color, out bool blink) {
            color = Color.Transparent; blink = false;
            if (rows.Count == 0) { return false; }
            double elapsed = Timing.TotalTime - localClockAtSync;
            bool urgent = false, awaiting = false, progress = false;
            foreach (Row r in rows) {
                double rem = r.Remaining - elapsed;
                if (rem <= 15.0) { urgent = true; }
                if (r.State == OrderState.AwaitingShuttle) { awaiting = true; } else { progress = true; }
            }
            if (urgent) { color = LampRed; blink = true; return true; }
            if (awaiting) { color = LampGreen; return true; }
            if (progress) { color = LampYellow; return true; }
            return false;
        }
    }
}
