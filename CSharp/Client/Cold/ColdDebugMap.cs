using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Thoughts;

namespace PrimaryPackage.Cold {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public static class ColdDebugMap {
        private const float Margin = 0.05f;
        private const float MinFlowReference = 40.0f;
        private const float FollowRate = 6.0f;
        private const float DashSpeed = 0.55f;
        private const float FadeRate = 6.0f;
        private const float MinZoom = 1.0f;
        private const float MaxZoom = 14.0f;
        private const float ZoomStep = 1.18f;
        private const float EdgeEpsilon = 3.0f;
        private const float DetailGaps = 0.035f;
        private const float DetailLabels = 0.06f;
        private const float DetailNumbers = 0.11f;

        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        private static readonly List<ColdSourceComponent> sourceBuffer = new List<ColdSourceComponent>();

        private static float[] snapTemp = Array.Empty<float>();
        private static float[] shownTemp = Array.Empty<float>();
        private static float[] groupTemp = Array.Empty<float>();
        private static int[] groupCount = Array.Empty<int>();
        private static float[] groupMinX = Array.Empty<float>();
        private static float[] groupMaxX = Array.Empty<float>();
        private static float[] groupMinY = Array.Empty<float>();
        private static float[] groupMaxY = Array.Empty<float>();
        private static readonly List<ColdZoneComponent> zoneBuffer = new List<ColdZoneComponent>();
        private static float[] snapFlow = Array.Empty<float>();
        private static float[] shownFlow = Array.Empty<float>();
        private static float flowReference = MinFlowReference;
        private static float snapTimer;
        private static float fade;
        private static int snapVersion = -1;
        private static float dashPhase;

        private static float zoom = 1.0f;
        private static float zoomTarget = 1.0f;
        private static Vector2 pan;
        private static Vector2 panTarget;
        private static bool dragging;
        private static float fitScale;
        private static float fitOriginX;
        private static float fitOriginY;

        public static void ResetView() {
            zoom = zoomTarget = 1.0f;
            pan = panTarget = Vector2.Zero;
            dragging = false;
        }

        public static void Release() {
            snapTemp = Array.Empty<float>();
            shownTemp = Array.Empty<float>();
            groupTemp = Array.Empty<float>();
            snapFlow = Array.Empty<float>();
            shownFlow = Array.Empty<float>();
            snapVersion = -1;
            snapTimer = 0.0f;
            fade = 0.0f;
            sourceBuffer.Clear();
            zoneBuffer.Clear();
            groupCount = Array.Empty<int>();
            groupMinX = Array.Empty<float>();
            groupMaxX = Array.Empty<float>();
            groupMinY = Array.Empty<float>();
            groupMaxY = Array.Empty<float>();
            ResetView();
        }

        public static void Update(float deltaTime) {
            float target = ColdDebug.MapOpen ? 1.0f : 0.0f;
            fade = PrimaryPackage.Core.Scalar.Approach(fade, target, FadeRate, deltaTime);
            if (fade <= 0.002f && target <= 0.0f) {
                if (snapTemp.Length > 0) { Release(); }
                return;
            }

            int hullCount = ColdField.Count;
            int edgeCount = ColdField.EdgeCount;
            if (snapTemp.Length < hullCount || snapVersion != ColdField.Version) {
                snapTemp = new float[hullCount];
                shownTemp = new float[hullCount];
                groupTemp = new float[hullCount];
                groupCount = new int[hullCount];
                groupMinX = new float[hullCount];
                groupMaxX = new float[hullCount];
                groupMinY = new float[hullCount];
                groupMaxY = new float[hullCount];
                snapVersion = ColdField.Version;
                snapTimer = 0.0f;
                for (int i = 0; i < hullCount; i++) { snapTemp[i] = shownTemp[i] = ColdField.Smoothed(i); }
                CollectSources();
            }
            if (snapFlow.Length < edgeCount) {
                snapFlow = new float[edgeCount];
                shownFlow = new float[edgeCount];
            }

            snapTimer -= deltaTime;
            if (snapTimer <= 0.0f) {
                snapTimer = ColdConfig.TickInterval;
                Snapshot(hullCount, edgeCount);
            }

            float k = PrimaryPackage.Core.Scalar.Decay(FollowRate, deltaTime);
            for (int i = 0; i < hullCount; i++) { shownTemp[i] += (snapTemp[i] - shownTemp[i]) * k; }
            for (int e = 0; e < edgeCount; e++) { shownFlow[e] += (snapFlow[e] - shownFlow[e]) * k; }
            Groups(hullCount);
            dashPhase += deltaTime;
            if (dashPhase > 1000.0f) { dashPhase -= 1000.0f; }

            zoom = PrimaryPackage.Core.Scalar.Approach(zoom, zoomTarget, 14.0f, deltaTime);
            pan = new Vector2(PrimaryPackage.Core.Scalar.Approach(pan.X, panTarget.X, 18.0f, deltaTime),
                PrimaryPackage.Core.Scalar.Approach(pan.Y, panTarget.Y, 18.0f, deltaTime));
        }

        public static void UpdateInput() {
            if (!ColdDebug.MapOpen) {
                dragging = false;
                return;
            }
            int wheel = PlayerInput.ScrollWheelSpeed;
            if (wheel != 0) {
                Vector2 mouse = PlayerInput.MousePosition;
                float previous = zoomTarget;
                zoomTarget *= wheel > 0 ? ZoomStep : 1.0f / ZoomStep;
                if (zoomTarget < MinZoom) { zoomTarget = MinZoom; }
                if (zoomTarget > MaxZoom) { zoomTarget = MaxZoom; }
                float ratio = zoomTarget / previous;
                Vector2 center = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight) * 0.5f;
                panTarget = (panTarget + center - mouse) * ratio - center + mouse;
            }

            if (PlayerInput.PrimaryMouseButtonHeld()) {
                if (dragging) {
                    panTarget += PlayerInput.MouseSpeed;
                    pan = panTarget;
                }
                dragging = true;
            } else {
                dragging = false;
            }

            if (PlayerInput.SecondaryMouseButtonClicked()) { ResetView(); }
        }

        private static void CollectSources() {
            sourceBuffer.Clear();
            zoneBuffer.Clear();
            for (int i = 0; i < Item.ItemList.Count; i++) {
                Item item = Item.ItemList[i];
                ColdSourceComponent source = item.GetComponent<ColdSourceComponent>();
                if (source != null) { sourceBuffer.Add(source); }
                ColdZoneComponent zone = item.GetComponent<ColdZoneComponent>();
                if (zone != null) { zoneBuffer.Add(zone); }
            }
        }

        private static void Snapshot(int hullCount, int edgeCount) {
            bool smoothed = !ColdDebug.Authoritative;
            for (int i = 0; i < hullCount; i++) { snapTemp[i] = smoothed ? ColdField.Smoothed(i) : ColdField.Raw(i); }
            float peak = MinFlowReference;
            for (int e = 0; e < edgeCount; e++) {
                float flow = ColdField.IsLinkEdge(e) ? 0.0f : ColdField.EdgeFlow(e, smoothed);
                snapFlow[e] = flow;
                float magnitude = flow < 0.0f ? -flow : flow;
                if (magnitude > peak) { peak = magnitude; }
            }
            flowReference += (peak - flowReference) * 0.35f;
            if (flowReference < MinFlowReference) { flowReference = MinFlowReference; }
        }

        private static void Groups(int hullCount) {
            if (groupTemp.Length < hullCount) { return; }
            for (int i = 0; i < hullCount; i++) {
                groupTemp[i] = 0.0f;
                groupCount[i] = 0;
                groupMinX[i] = float.MaxValue;
                groupMaxX[i] = float.MinValue;
                groupMinY[i] = float.MaxValue;
                groupMaxY[i] = float.MinValue;
            }
            for (int i = 0; i < hullCount; i++) {
                int root = ColdField.GroupOf(i);
                if (root < 0 || root >= hullCount) { continue; }
                groupTemp[root] += shownTemp[i];
                groupCount[root]++;
                Hull hull = ColdField.HullAt(i);
                if (hull == null || hull.Removed) { continue; }
                Rectangle r = hull.WorldRect;
                if (r.X < groupMinX[root]) { groupMinX[root] = r.X; }
                if (r.X + r.Width > groupMaxX[root]) { groupMaxX[root] = r.X + r.Width; }
                if (r.Y - r.Height < groupMinY[root]) { groupMinY[root] = r.Y - r.Height; }
                if (r.Y > groupMaxY[root]) { groupMaxY[root] = r.Y; }
            }
            for (int i = 0; i < hullCount; i++) {
                if (groupCount[i] > 1) { groupTemp[i] /= groupCount[i]; }
            }
        }

        private static float GroupTemperature(int i, int hullCount) {
            int root = ColdField.GroupOf(i);
            return root >= 0 && root < hullCount && groupTemp.Length > root ? groupTemp[root] : shownTemp[i];
        }

        public static Color Scale(float temperature) {
            float comfort = ColdConfig.Comfort;
            if (temperature <= comfort) {
                float span = comfort - ColdConfig.MinTemperature;
                float t = span <= 0.0f ? 0.0f : (comfort - temperature) / span;
                return Color.Lerp(ColdConfig.DebugNeutralColor, ColdConfig.DebugColdColor, PrimaryPackage.Core.Scalar.Clamp01(t));
            }
            float warmSpan = ColdConfig.MaxTemperature - comfort;
            float w = warmSpan <= 0.0f ? 0.0f : (temperature - comfort) / warmSpan;
            return Color.Lerp(ColdConfig.DebugNeutralColor, ColdConfig.DebugWarmColor, PrimaryPackage.Core.Scalar.Clamp01(w));
        }

        private static Vector2 World(float x, float y) {
            Vector2 center = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight) * 0.5f;
            float sx = fitOriginX + x * fitScale;
            float sy = fitOriginY - y * fitScale;
            return (new Vector2(sx, sy) - center) * zoom + center + pan;
        }

        public static void Draw(SpriteBatch spriteBatch) {
            if (fade <= 0.002f) { return; }
            int hullCount = ColdField.Count;
            if (hullCount == 0 || shownTemp.Length < hullCount) { return; }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < hullCount; i++) {
                Hull hull = ColdField.HullAt(i);
                if (hull == null || hull.Removed) { continue; }
                Rectangle r = hull.WorldRect;
                if (r.X < minX) { minX = r.X; }
                if (r.X + r.Width > maxX) { maxX = r.X + r.Width; }
                if (r.Y - r.Height < minY) { minY = r.Y - r.Height; }
                if (r.Y > maxY) { maxY = r.Y; }
            }
            if (minX > maxX || minY > maxY) { return; }

            float width = GameMain.GraphicsWidth;
            float height = GameMain.GraphicsHeight;
            float usableW = width * (1.0f - Margin * 2.0f);
            float usableH = height * (1.0f - Margin * 2.0f) - height * 0.09f;
            float spanX = Math.Max(maxX - minX, 1.0f);
            float spanY = Math.Max(maxY - minY, 1.0f);
            fitScale = Math.Min(usableW / spanX, usableH / spanY);
            fitOriginX = (width - spanX * fitScale) * 0.5f - minX * fitScale;
            fitOriginY = height * 0.5f - spanY * fitScale * 0.5f + maxY * fitScale;

            float scale = fitScale * zoom;
            float alpha = PrimaryPackage.Core.Scalar.Smooth(fade);
            Texture2D white = GUI.WhiteTexture;
            if (white == null) { return; }
            spriteBatch.Draw(white, Vector2.Zero, null, Color.Black * (0.74f * alpha), 0.0f, Vector2.Zero,
                new Vector2(width, height), SpriteEffects.None, 0.0f);

            GUIFont font = GUIStyle.SmallFont;
            float outline = Math.Max(1.0f, GUI.Scale);

            DrawZones(spriteBatch, alpha);
            DrawHulls(spriteBatch, font, hullCount, scale, outline, alpha);
            DrawFlows(spriteBatch, alpha);
            if (scale >= DetailGaps) { DrawGaps(spriteBatch, font, scale, alpha); }
            DrawSources(spriteBatch, font, scale, alpha);
            DrawThermalRegistry(spriteBatch, font, scale, alpha);
            DrawLegend(spriteBatch, font, alpha);
        }

        private static void DrawHulls(SpriteBatch spriteBatch, GUIFont font, int hullCount, float scale,
                                      float outline, float alpha) {
            for (int i = 0; i < hullCount; i++) {
                Hull hull = ColdField.HullAt(i);
                if (hull == null || hull.Removed) { continue; }
                Rectangle r = hull.WorldRect;
                Vector2 topLeft = World(r.X, r.Y);
                Vector2 bottomRight = World(r.X + r.Width, r.Y - r.Height);
                Vector2 size = bottomRight - topLeft;
                float temperature = GroupTemperature(i, hullCount);
                Color color = Scale(temperature);
                GUI.DrawRectangle(spriteBatch, topLeft, size, color * (0.5f * alpha), isFilled: true);
                DrawRoomBorder(spriteBatch, i, hullCount, r, color * (0.95f * alpha), outline);
            }

            if (font == null || !font.HasValue || scale < DetailLabels) { return; }
            for (int i = 0; i < hullCount; i++) {
                if (groupCount[i] == 0 || groupMinX[i] > groupMaxX[i]) { continue; }
                Hull hull = ColdField.HullAt(i);
                if (hull == null || hull.Removed) { continue; }
                Vector2 center = World((groupMinX[i] + groupMaxX[i]) * 0.5f,
                    (groupMinY[i] + groupMaxY[i]) * 0.5f);
                string temperature = GroupTemperature(i, hullCount).ToString("0.0", Culture) + "°";
                Vector2 size = font.MeasureString(temperature);
                font.DrawString(spriteBatch, temperature, center - size * 0.5f, Color.White * (0.95f * alpha),
                    0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
                if (scale < DetailNumbers) { continue; }
                string name = hull.DisplayName?.Value;
                if (string.IsNullOrEmpty(name)) { continue; }
                Vector2 nameSize = font.MeasureString(name);
                font.DrawString(spriteBatch, name,
                    new Vector2(center.X - nameSize.X * 0.5f, center.Y - size.Y * 0.5f - nameSize.Y),
                    Color.White * (0.6f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
            }
        }

        private static void DrawRoomBorder(SpriteBatch spriteBatch, int i, int hullCount, Rectangle r,
                                           Color color, float outline) {
            int root = ColdField.GroupOf(i);
            bool left = true, right = true, top = true, bottom = true;
            if (root < 0 || groupCount[root] <= 1) { goto draw; }
            for (int j = 0; j < hullCount; j++) {
                if (j == i || ColdField.GroupOf(j) != root) { continue; }
                Hull other = ColdField.HullAt(j);
                if (other == null || other.Removed) { continue; }
                Rectangle o = other.WorldRect;
                bool verticalOverlap = r.Y - r.Height < o.Y && o.Y - o.Height < r.Y;
                bool horizontalOverlap = r.X < o.X + o.Width && o.X < r.X + r.Width;
                if (verticalOverlap && Math.Abs(r.X - (o.X + o.Width)) < EdgeEpsilon) { left = false; }
                if (verticalOverlap && Math.Abs(r.X + r.Width - o.X) < EdgeEpsilon) { right = false; }
                if (horizontalOverlap && Math.Abs(r.Y - (o.Y - o.Height)) < EdgeEpsilon) { top = false; }
                if (horizontalOverlap && Math.Abs(r.Y - r.Height - o.Y) < EdgeEpsilon) { bottom = false; }
            }
            draw:
            Vector2 tl = World(r.X, r.Y);
            Vector2 tr = World(r.X + r.Width, r.Y);
            Vector2 bl = World(r.X, r.Y - r.Height);
            Vector2 br = World(r.X + r.Width, r.Y - r.Height);
            if (top) { GUI.DrawLine(spriteBatch, tl, tr, color, width: outline); }
            if (bottom) { GUI.DrawLine(spriteBatch, bl, br, color, width: outline); }
            if (left) { GUI.DrawLine(spriteBatch, tl, bl, color, width: outline); }
            if (right) { GUI.DrawLine(spriteBatch, tr, br, color, width: outline); }
        }

        private static void DrawFlows(SpriteBatch spriteBatch, float alpha) {
            int edgeCount = ColdField.EdgeCount;
            if (shownFlow.Length < edgeCount) { return; }
            float reference = flowReference;
            for (int e = 0; e < edgeCount; e++) {
                if (ColdField.IsLinkEdge(e)) { continue; }
                if (!ColdField.TryGetEdge(e, out int a, out int b, out Gap gap)) { continue; }
                if (gap == null || gap.Open <= 0.0f) { continue; }
                if (a >= shownTemp.Length || b >= shownTemp.Length) { continue; }
                float flow = shownFlow[e];
                float magnitude = flow < 0.0f ? -flow : flow;
                float strength = magnitude / reference;
                if (strength > 1.0f) { strength = 1.0f; }
                if (strength < 0.004f) { continue; }

                Vector2 centerA = Project(ColdField.HullAt(a));
                Vector2 centerB = Project(ColdField.HullAt(b));
                bool intoA = flow > 0.0f;
                Vector2 from = intoA ? centerB : centerA;
                Vector2 to = intoA ? centerA : centerB;

                Color color = Scale(shownTemp[intoA ? b : a]);
                float thickness = Math.Max(1.0f, (1.0f + 4.0f * PrimaryPackage.Core.Scalar.Smooth(strength)) * GUI.Scale);
                GUI.DrawLine(spriteBatch, from, to, color * (0.28f + 0.5f * strength) * alpha, width: thickness);

                float speed = DashSpeed * (0.35f + strength);
                float u = dashPhase * speed;
                u -= (float)Math.Floor(u);
                Vector2 direction = to - from;
                Vector2 head = from + direction * u;
                Vector2 tail = from + direction * Math.Max(0.0f, u - 0.16f);
                GUI.DrawLine(spriteBatch, tail, head, Color.White * (0.35f + 0.55f * strength) * alpha,
                    width: thickness);
            }
        }

        private static void DrawGaps(SpriteBatch spriteBatch, GUIFont font, float scale, float alpha) {
            int edgeCount = ColdField.EdgeCount;
            for (int e = 0; e < edgeCount; e++) {
                if (!ColdField.TryGetEdge(e, out int a, out int b, out Gap gap)) { continue; }
                if (gap == null) { continue; }
                Rectangle r = gap.WorldRect;
                Vector2 topLeft = World(r.X, r.Y);
                Vector2 bottomRight = World(r.X + r.Width, r.Y - r.Height);
                Vector2 size = bottomRight - topLeft;
                if (size.X < 2.0f) { size.X = 2.0f; }
                if (size.Y < 2.0f) { size.Y = 2.0f; }
                bool closed = gap.Open <= 0.0f;
                bool door = gap.ConnectedDoor != null;
                Color color = closed ? new Color(210, 90, 80)
                    : door ? new Color(120, 220, 140) : new Color(240, 210, 120);
                GUI.DrawRectangle(spriteBatch, topLeft, size, color * ((closed ? 0.75f : 0.9f) * alpha),
                    isFilled: true);
                if (font == null || !font.HasValue || scale < DetailNumbers) { continue; }
                string label = (door ? "D " : "H ") + gap.Open.ToString("0.00", Culture);
                Vector2 labelSize = font.MeasureString(label);
                font.DrawString(spriteBatch, label,
                    new Vector2(topLeft.X + size.X * 0.5f - labelSize.X * 0.5f, topLeft.Y - labelSize.Y),
                    color * (0.95f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
            }
        }

        private static void DrawZones(SpriteBatch spriteBatch, float alpha) {
            for (int i = 0; i < zoneBuffer.Count; i++) {
                ColdZoneComponent zone = zoneBuffer[i];
                Item item = zone.Item;
                if (item == null || item.Removed) { continue; }
                Rectangle r = item.WorldRect;
                Vector2 topLeft = World(r.X, r.Y);
                Vector2 size = World(r.X + r.Width, r.Y - r.Height) - topLeft;
                Color color = Scale(zone.TargetTemperature);
                GUI.DrawRectangle(spriteBatch, topLeft, size, color * (0.14f * alpha), isFilled: true);
                GUI.DrawRectangle(spriteBatch, topLeft, size, color * (0.5f * alpha),
                    thickness: Math.Max(1.0f, GUI.Scale));
            }
        }

        private static void DrawSources(SpriteBatch spriteBatch, GUIFont font, float scale, float alpha) {
            for (int i = 0; i < sourceBuffer.Count; i++) {
                ColdSourceComponent source = sourceBuffer[i];
                Item item = source.Item;
                if (item == null || item.Removed) { continue; }
                Vector2 at = World(item.WorldPosition.X, item.WorldPosition.Y);
                Color color = Scale(source.TargetTemperature);
                float radius = Math.Max(4.0f, 7.0f * GUI.Scale);
                GUI.DrawRectangle(spriteBatch, at - new Vector2(radius, radius),
                    new Vector2(radius * 2.0f, radius * 2.0f), color * ((source.Enabled ? 0.95f : 0.35f) * alpha),
                    isFilled: true);
                Hull bound = source.ColdHull;
                if (bound != null && !bound.Removed) {
                    GUI.DrawLine(spriteBatch, at, Project(bound), color * (0.6f * alpha),
                        width: Math.Max(1.0f, GUI.Scale));
                }
                if (font == null || !font.HasValue || scale < DetailNumbers) { continue; }
                string label = source.TargetTemperature.ToString("0", Culture) + "°";
                font.DrawString(spriteBatch, label, at + new Vector2(radius + 2.0f, -radius),
                    color * (0.95f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
            }
        }

        private static void DrawThermalRegistry(SpriteBatch spriteBatch, GUIFont font, float scale, float alpha) {
            if (ThermalSourceRegistry.Count == 0) { return; }
            float radius = Math.Max(3.0f, 5.0f * GUI.Scale);
            var color = new Color(255, 150, 60);
            int row = 0;
            foreach (ThermalSourceRegistry.Entry entry in ThermalSourceRegistry.All) {
                if (entry.Hull == null || entry.Hull.Removed) { continue; }
                Vector2 at = Project(entry.Hull) + new Vector2(0.0f, radius * 2.2f * (row % 4));
                GUI.DrawRectangle(spriteBatch, at - new Vector2(radius, radius), new Vector2(radius * 2.0f, radius * 2.0f),
                    color * (0.85f * alpha), isFilled: true);
                if (font != null && font.HasValue && scale >= DetailNumbers) {
                    string label = entry.Label + " → " + entry.Target.ToString("0", Culture) + "°";
                    font.DrawString(spriteBatch, label, at + new Vector2(radius + 2.0f, -radius),
                        color * (0.95f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
                }
                row++;
            }
        }

        private static Vector2 Project(Hull hull) {
            Rectangle r = hull.WorldRect;
            return World(r.X + r.Width * 0.5f, r.Y - r.Height * 0.5f);
        }

        private static void DrawLegend(SpriteBatch spriteBatch, GUIFont font, float alpha) {
            if (font == null || !font.HasValue) { return; }
            float scale = GUI.Scale;
            float barWidth = GameMain.GraphicsWidth * 0.26f;
            float barHeight = 10.0f * scale;
            var pos = new Vector2((GameMain.GraphicsWidth - barWidth) * 0.5f,
                GameMain.GraphicsHeight - 58.0f * scale);
            Texture2D white = GUI.WhiteTexture;
            if (white == null) { return; }
            const int Steps = 32;
            float stepWidth = barWidth / Steps;
            for (int i = 0; i < Steps; i++) {
                float t = i / (float)(Steps - 1);
                float temperature = MathHelper.Lerp(ColdConfig.MinTemperature, ColdConfig.MaxTemperature, t);
                spriteBatch.Draw(white, new Vector2(pos.X + stepWidth * i, pos.Y), null, Scale(temperature) * alpha,
                    0.0f, Vector2.Zero, new Vector2(stepWidth + 1.0f, barHeight), SpriteEffects.None, 0.0f);
            }
            string title = (ColdDebug.Authoritative
                    ? "карта холода · точные значения симуляции · "
                    : "карта холода · клиентская интерполяция · ")
                + "зум " + zoom.ToString("0.0", Culture) + "×";
            Vector2 titleSize = font.MeasureString(title);
            font.DrawString(spriteBatch, title,
                new Vector2((GameMain.GraphicsWidth - titleSize.X) * 0.5f, pos.Y - titleSize.Y - 6.0f * scale),
                Color.White * (0.85f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
            string hint = "колесо — зум к курсору · ЛКМ — тянуть · ПКМ — сброс · " +
                          ColdDebug.MapKey + "/Esc — закрыть · зелёный проём — дверь, жёлтый — пробоина, красный — закрыт";
            Vector2 hintSize = font.MeasureString(hint);
            font.DrawString(spriteBatch, hint,
                new Vector2((GameMain.GraphicsWidth - hintSize.X) * 0.5f, pos.Y + barHeight + 4.0f * scale),
                Color.White * (0.55f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
            string left = ColdConfig.MinTemperature.ToString("0", Culture) + "°";
            string right = ColdConfig.MaxTemperature.ToString("0", Culture) + "°";
            font.DrawString(spriteBatch, left, new Vector2(pos.X - font.MeasureString(left).X - 6.0f * scale, pos.Y),
                Color.White * (0.7f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
            font.DrawString(spriteBatch, right, new Vector2(pos.X + barWidth + 6.0f * scale, pos.Y),
                Color.White * (0.7f * alpha), 0.0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0.0f);
        }
    }
}
