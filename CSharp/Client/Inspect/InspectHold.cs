using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public static class InspectHold {
        private const float HoldSeconds = 3.0f;
        private const float AppearSeconds = 0.22f;
        private const float VanishSeconds = 0.3f;
        private const float Radius = 34.0f;
        private const float Thickness = 3.2f;
        private const float MinScale = 0.7f;

        private static readonly Color TrackColor = new Color(20, 26, 32);
        private static readonly Color FillColor = new Color(120, 200, 255);
        private static readonly Color DoneColor = new Color(150, 255, 180);

        private static MapEntity target;
        private static InspectAnim hold, presence;
        private static bool holding;
        private static bool completed;

        public static void Update() {
            float step = (float)Timing.Step;
            MapEntity hovered = Hovered();
            bool keyDown = PlayerInput.KeyDown(Keys.F) && !PrimaryPackage.Core.PlayView.Typing && GUI.MouseOn == null;

            if (!keyDown || hovered == null || (target != null && !ReferenceEquals(hovered, target))) {
                holding = false;
            } else {
                if (!ReferenceEquals(hovered, target)) {
                    target = hovered;
                    hold.Reset();
                    completed = false;
                }
                holding = !completed;
            }

            if (holding) {
                presence.Advance(true, step, AppearSeconds);
                if (hold.Advance(true, step, HoldSeconds)) {
                    completed = true;
                    holding = false;
                    Complete();
                }
                return;
            }
            if (presence.Advance(false, step, VanishSeconds)) {
                target = null;
                hold.Reset();
                completed = false;
            }
        }

        private static MapEntity Hovered() {
            if (Screen.Selected != GameMain.SubEditorScreen) { return null; }
            MapEntity single = null;
            int count = 0;
            foreach (MapEntity entity in MapEntity.HighlightedEntities) {
                single = entity;
                if (++count > 1) { return null; }
            }
            return count == 1 && (single is Item || single is Structure) ? single : null;
        }

        private static void Complete() {
            if (target == null || InspectRegistry.Get(target) != null) { return; }
            InspectRegistry.Attach(target);
            MapEntity.SelectedList.Clear();
            MapEntity.AddSelection(target);
            if (target is Item item) {
                item.CreateEditingHUD();
            } else if (target is Structure structure) {
                structure.CreateEditingHUD();
            }
        }

        public static void Draw(SpriteBatch spriteBatch) {
            if (spriteBatch == null || target == null || presence.Idle) { return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null) { return; }
            float appear = ToastTimeline.Ease(holding ? ZoneEasing.Торможение : ZoneEasing.Разгон, presence.Interpolated);
            float fill = ToastTimeline.Ease(ZoneEasing.Плавно, hold.Interpolated);
            float scale = MinScale + (1.0f - MinScale) * appear;
            try {
                spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);
                try {
                    InspectRing.DrawGauge(spriteBatch, cam.WorldToScreen(target.DrawPosition), Radius * scale,
                        Thickness * scale, fill, TrackColor * (0.75f * appear),
                        Color.Lerp(FillColor, DoneColor, fill) * appear);
                } finally {
                    spriteBatch.End();
                }
            } catch (InvalidOperationException) { }
        }
    }
}
