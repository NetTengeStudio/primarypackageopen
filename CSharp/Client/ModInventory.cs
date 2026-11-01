using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.ModInventory {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public sealed class ModInventoryPlugin : PrimaryPackage.Core.IModule {
        private static readonly Dictionary<Identifier, int[][]> SlotRows = new Dictionary<Identifier, int[][]> {
            ["bodyarmorlighterc".ToIdentifier()] = new[] { new[] { 6 }, new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
            ["tacticalvestheavy".ToIdentifier()] = new[] { new[] { 2 }, new[] { 0, 1 } }
        };

        private const int TexRadius = 24;
        private const string RoundedKey = "modinventory/rounded";
        private static Texture2D rounded;
        private static ItemInventory drawing;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            Type self = typeof(ModInventoryPlugin);
            PrimaryPackage.Core.ModuleHost.Guard("Inventory.Update", () => harmony.Patch(
                AccessTools.Method(typeof(Inventory), nameof(Inventory.Update), new[] { typeof(float), typeof(Camera), typeof(bool) }),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(self, nameof(ArrangeSlots))));
            PrimaryPackage.Core.ModuleHost.Guard("ItemInventory.Draw", () => harmony.Patch(
                AccessTools.Method(typeof(ItemInventory), nameof(ItemInventory.Draw)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(self, nameof(CaptureInventory)),
                transpiler: PrimaryPackage.Core.ModuleHost.Bind(self, nameof(ReplaceBackgroundCall))));
        }


        public void Release() {
            drawing = null;
            rounded = null;
        }

        private static int[][] RowsFor(Inventory inventory) {
            return inventory is ItemInventory inv && inv.Owner is Barotrauma.Item item
                && SlotRows.TryGetValue(item.Prefab.Identifier, out int[][] rows) ? rows : null;
        }

        public static void ArrangeSlots(Inventory __instance, bool subInventory) {
            if (!subInventory) { return; }
            int[][] rows = RowsFor(__instance);
            if (rows == null) { return; }

            var slots = __instance.visualSlots;
            Barotrauma.Item item = (Barotrauma.Item)((ItemInventory)__instance).Owner;
            Inventory parent = item.ParentInventory;
            if (slots == null || parent?.visualSlots == null) { return; }

            int index = parent.FindIndex(item);
            if (index < 0 || index >= parent.visualSlots.Length) { return; }

            var anchor = parent.visualSlots[index];
            int dir = anchor.SubInventoryDir < 0 ? -1 : 1;
            int w = anchor.Rect.Width, h = anchor.Rect.Height;
            if (w <= 0 || h <= 0) { return; }

            float scale = Inventory.UIScale;
            float spacingX = 10f * scale;
            float spacingY = (10f + (Inventory.UnequippedIndicator?.size.Y ?? 0f)) * scale * GUI.AspectRatioAdjustment;
            int stepY = h + (int)spacingY;

            int nearestY = dir < 0
                ? anchor.EquipButtonRect.Y - h - (int)(35f * scale)
                : anchor.EquipButtonRect.Bottom + (int)(10f * scale);
            int total = rows.Length * stepY;
            Vector2 drawOffset = Vector2.SmoothStep(new Vector2(0f, -50f * dir), new Vector2(0f, total * dir), __instance.OpenState);

            for (int r = 0; r < rows.Length; r++) {
                int[] row = rows[r];
                int y = nearestY + (dir < 0 ? rows.Length - 1 - r : r) * stepY * dir - total * dir;
                int x = anchor.Rect.Center.X - (int)((w * row.Length + spacingX * (row.Length - 1)) / 2f);
                for (int c = 0; c < row.Length; c++) {
                    if (row[c] < 0 || row[c] >= slots.Length) { continue; }
                    var slot = slots[row[c]];
                    slot.Rect = new Rectangle(x + (int)(c * (w + spacingX)), y, w, h);
                    slot.DrawOffset = drawOffset;
                    slot.InteractRect = new Rectangle(
                        (int)(slot.Rect.X - spacingX / 2f - 1), (int)(slot.Rect.Y - spacingY / 2f - 1),
                        (int)(w + spacingX + 2), (int)(h + spacingY + 2));
                }
            }
        }

        public static void CaptureInventory(ItemInventory __instance) {
            drawing = __instance;
        }

        public static IEnumerable<CodeInstruction> ReplaceBackgroundCall(IEnumerable<CodeInstruction> instructions) {
            MethodInfo original = AccessTools.Method(typeof(GUI), nameof(GUI.DrawRectangle),
                new[] { typeof(SpriteBatch), typeof(Rectangle), typeof(Color), typeof(bool), typeof(float), typeof(float) });
            MethodInfo replacement = AccessTools.Method(typeof(ModInventoryPlugin), nameof(DrawBackground));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions) {
                if (instruction.Calls(original)) {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                }
                else {
                    yield return instruction;
                }
            }
            if (replaced == 0) {
                PrimaryPackage.Core.ModLog.Error("inventory background hook does not apply");
            }
        }

        public static void DrawBackground(SpriteBatch spriteBatch, Rectangle rect, Color color, bool isFilled, float depth, float thickness) {
            if (!isFilled) {
                GUI.DrawRectangle(spriteBatch, rect, color, false, depth, thickness);
                return;
            }
            ItemInventory inventory = drawing;
            int radius = (int)(8f * Inventory.UIScale);
            var panels = BuildPanels(inventory, radius);
            if (panels == null) {
                DrawPanel(spriteBatch, rect, color, radius, true, true, true, true);
                return;
            }
            foreach (var panel in panels) {
                DrawPanel(spriteBatch, panel.Rect, color, radius, panel.TopLeft, panel.TopRight, panel.BottomLeft, panel.BottomRight);
            }
        }

        private readonly struct Panel {
            public readonly Rectangle Rect;
            public readonly bool TopLeft, TopRight, BottomLeft, BottomRight;
            public Panel(Rectangle rect, bool topLeft, bool topRight, bool bottomLeft, bool bottomRight) {
                Rect = rect; TopLeft = topLeft; TopRight = topRight; BottomLeft = bottomLeft; BottomRight = bottomRight;
            }
        }

        private static List<Panel> BuildPanels(ItemInventory inventory, int radius) {
            int[][] rows = RowsFor(inventory);
            var slots = inventory?.visualSlots;
            if (rows == null || slots == null) { return null; }

            int pad = (int)(5f * Inventory.UIScale);
            var groups = new List<Rectangle>();
            foreach (int[] row in rows) {
                Rectangle rect = Rectangle.Empty;
                foreach (int i in row) {
                    if (i < 0 || i >= slots.Length) { continue; }
                    Rectangle slot = slots[i].Rect;
                    slot.Location += slots[i].DrawOffset.ToPoint();
                    rect = rect.IsEmpty ? slot : Rectangle.Union(rect, slot);
                }
                if (rect.IsEmpty) { continue; }
                rect.Inflate(pad, pad);
                if (groups.Count > 0 && groups[^1].X == rect.X && groups[^1].Width == rect.Width) {
                    groups[^1] = Rectangle.Union(groups[^1], rect);
                } else {
                    groups.Add(rect);
                }
            }
            if (groups.Count == 0) { return null; }

            for (int i = 0; i < groups.Count - 1; i++) {
                Rectangle current = groups[i];
                current.Height = groups[i + 1].Y - current.Y;
                groups[i] = current;
            }

            var panels = new List<Panel>(groups.Count);
            for (int i = 0; i < groups.Count; i++) {
                bool top = i == 0 || groups[i].Width > groups[i - 1].Width;
                bool bottom = i == groups.Count - 1 || groups[i].Width > groups[i + 1].Width;
                panels.Add(new Panel(groups[i], top, top, bottom, bottom));
            }
            return panels;
        }

        private static void DrawPanel(SpriteBatch spriteBatch, Rectangle rect, Color color, int radius, bool tl, bool tr, bool bl, bool br) {
            Texture2D tex = GetRounded(spriteBatch.GraphicsDevice);
            int c = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            if (tex == null || c <= 0) {
                GUI.DrawRectangle(spriteBatch, rect, color, true);
                return;
            }

            int far = TexRadius + 2;
            int[] dx = { rect.X, rect.X + c, rect.Right - c };
            int[] dy = { rect.Y, rect.Y + c, rect.Bottom - c };
            int[] dw = { c, rect.Width - 2 * c, c };
            int[] dh = { c, rect.Height - 2 * c, c };
            int[] sxy = { 0, TexRadius, far };
            int[] swh = { TexRadius, 2, TexRadius };
            bool[,] round = { { tl, bl }, { true, true }, { tr, br } };

            for (int x = 0; x < 3; x++) {
                if (dw[x] <= 0) { continue; }
                for (int y = 0; y < 3; y++) {
                    if (dh[y] <= 0) { continue; }
                    bool corner = x != 1 && y != 1;
                    bool keep = !corner || round[x, y == 2 ? 1 : 0];
                    spriteBatch.Draw(tex,
                        new Rectangle(dx[x], dy[y], dw[x], dh[y]),
                        keep ? new Rectangle(sxy[x], sxy[y], swh[x], swh[y]) : new Rectangle(TexRadius, TexRadius, 2, 2),
                        color);
                }
            }
        }

        private static Texture2D GetRounded(GraphicsDevice device) {
            if (rounded is { IsDisposed: false }) { return rounded; }
            if (device == null) { return null; }

            int size = TexRadius * 2 + 2;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++) {
                float ay = Offset(y);
                for (int x = 0; x < size; x++) {
                    float ax = Offset(x);
                    float alpha = MathHelper.Clamp(TexRadius + 0.5f - MathF.Sqrt(ax * ax + ay * ay), 0f, 1f);
                    pixels[y * size + x] = Color.White * alpha;
                }
            }
            rounded = new Texture2D(device, size, size);
            rounded.SetData(pixels);
            PrimaryPackage.Core.TextureStore.Adopt(RoundedKey, rounded);
            return rounded;

            static float Offset(int v) {
                if (v < TexRadius) { return TexRadius - (v + 0.5f); }
                if (v >= TexRadius + 2) { return v + 0.5f - (TexRadius + 2); }
                return 0f;
            }
        }
    }
}
