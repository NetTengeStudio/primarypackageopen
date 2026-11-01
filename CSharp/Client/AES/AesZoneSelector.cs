using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace PrimaryPackage.Aes {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public static class AesZoneSelector {
        private struct Zone { public float MinX, MinY, MaxX, MaxY; }

        private static readonly Color FillColor = Color.Gray * 0.12f;
        private static readonly Color OutlineColor = Color.Gray * 0.2f;
        private static readonly Color HitColor = Color.LightGreen;

        private static float[] tx, ty;
        private static Item[] titems;
        private static bool[] inZone;
        private static int tcount;

        private static readonly List<Zone> zones = new List<Zone>();
        private static Item source;
        private static bool dragging, armed;
        private static Vector2 dragStart, dragEnd;

        public static bool Active { get; private set; }

        public static void Begin(Item src, List<Item> targets) {
            source = src;
            tcount = targets.Count;
            EnsureCapacity(tcount);
            for (int i = 0; i < tcount; i++) {
                Item it = targets[i];
                Vector2 p = it.WorldPosition;
                tx[i] = p.X;
                ty[i] = p.Y;
                titems[i] = it;
                inZone[i] = false;
            }
            zones.Clear();
            dragging = false;
            armed = false;
            Active = true;
        }

        private static void End() {
            Active = false;
            dragging = false;
            source = null;
            if (titems != null) { Array.Clear(titems, 0, tcount); }
            tcount = 0;
        }

        private static void EnsureCapacity(int n) {
            if (tx != null && tx.Length >= n) { return; }
            int cap = Math.Max(n, 64);
            tx = new float[cap];
            ty = new float[cap];
            titems = new Item[cap];
            inZone = new bool[cap];
        }

        public static void Update() {
            if (!Active) { return; }
            if (!SubEditorScreen.IsSubEditor() || source == null || source.Removed) { End(); return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null) { End(); return; }

            if (!PlayerInput.PrimaryMouseButtonHeld()) { armed = true; }

            if (!PrimaryPackage.Core.PlayView.Typing) {
                if (PlayerInput.KeyHit(Keys.Enter)) { Confirm(); return; }
                if (PlayerInput.KeyHit(Keys.Escape)) { End(); return; }
                if (PlayerInput.KeyHit(Keys.Back)) {
                    if (dragging) { dragging = false; }
                    else if (zones.Count > 0) { zones.RemoveAt(zones.Count - 1); }
                }
            }

            Vector2 world = cam.ScreenToWorld(PlayerInput.MousePosition);
            if (armed && GUI.MouseOn == null) {
                if (!dragging && PlayerInput.PrimaryMouseButtonDown()) {
                    dragging = true;
                    dragStart = world;
                    dragEnd = world;
                } else if (dragging) {
                    dragEnd = world;
                    if (!PlayerInput.PrimaryMouseButtonHeld()) {
                        CommitDrag(cam.Zoom);
                        dragging = false;
                    }
                }
            }

            RecomputeHits();
        }

        private static void CommitDrag(float zoom) {
            float minX = Math.Min(dragStart.X, dragEnd.X);
            float maxX = Math.Max(dragStart.X, dragEnd.X);
            float minY = Math.Min(dragStart.Y, dragEnd.Y);
            float maxY = Math.Max(dragStart.Y, dragEnd.Y);
            float minWorldSize = 5.0f / Math.Max(zoom, 0.0001f);
            if (maxX - minX < minWorldSize || maxY - minY < minWorldSize) { return; }
            zones.Add(new Zone { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY });
        }

        private static void RecomputeHits() {
            for (int i = 0; i < tcount; i++) { inZone[i] = false; }
            for (int z = 0; z < zones.Count; z++) {
                Zone zone = zones[z];
                for (int i = 0; i < tcount; i++) {
                    if (inZone[i]) { continue; }
                    if (tx[i] >= zone.MinX && tx[i] <= zone.MaxX && ty[i] >= zone.MinY && ty[i] <= zone.MaxY) {
                        inZone[i] = true;
                    }
                }
            }
        }

        private static void Confirm() {
            for (int i = 0; i < tcount; i++) {
                if (inZone[i]) { AesLink.Link(source, titems[i]); }
            }
            End();
        }

        public static void Draw(SpriteBatch spriteBatch) {
            if (!Active) { return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null || cam.Zoom <= 0.0f) { return; }
            float px = 1.0f / cam.Zoom;
            float thickness = Math.Max(1.0f, 2.0f * px);

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, transformMatrix: cam.Transform);

            for (int z = 0; z < zones.Count; z++) {
                Zone zone = zones[z];
                DrawZone(spriteBatch, zone.MinX, zone.MinY, zone.MaxX, zone.MaxY, thickness);
            }
            if (dragging) {
                DrawZone(spriteBatch,
                    Math.Min(dragStart.X, dragEnd.X), Math.Min(dragStart.Y, dragEnd.Y),
                    Math.Max(dragStart.X, dragEnd.X), Math.Max(dragStart.Y, dragEnd.Y), thickness);
            }
            for (int i = 0; i < tcount; i++) {
                if (!inZone[i]) { continue; }
                Rectangle r = titems[i].WorldRect;
                GUI.DrawRectangle(spriteBatch, new Vector2(r.X, -r.Y), new Vector2(r.Width, r.Height), HitColor, thickness: thickness);
            }

            spriteBatch.End();
        }

        private static void DrawZone(SpriteBatch spriteBatch, float minX, float minY, float maxX, float maxY, float thickness) {
            Vector2 pos = new Vector2(minX, -maxY);
            Vector2 size = new Vector2(maxX - minX, maxY - minY);
            GUI.DrawRectangle(spriteBatch, pos, size, FillColor, isFilled: true);
            GUI.DrawRectangle(spriteBatch, pos, size, OutlineColor, thickness: thickness);
        }
    }
}
