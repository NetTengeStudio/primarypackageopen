using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace PrimaryPackage.Economy {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public sealed class OrderCrateBakerPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted {
        private Harmony harmony;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            Patch(AccessTools.Method(typeof(Item), nameof(Item.SetActiveSprite)),
                nameof(OrderCrateBaker.SetActiveSpritePostfix));
            MethodInfo draw = AccessTools.Method(typeof(Item), nameof(Item.Draw), new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) });
            if (draw != null) {
                harmony.Patch(draw,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(OrderCrateBaker), nameof(OrderCrateBaker.ItemDrawPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(OrderCrateBaker), nameof(OrderCrateBaker.ItemDrawPostfix))));
            }
        }
        private void Patch(MethodInfo original, string postfix) {
            if (original == null) { return; }
            harmony.Patch(original, postfix: new HarmonyMethod(AccessTools.Method(typeof(OrderCrateBaker), postfix)));
        }
        public void LoadCompleted() => OrderCrateBaker.Init();
        public void Release() {
            OrderCrateBaker.Shutdown();
        }
    }
    public static class OrderCrateBaker {
        private const int BakeScale = 1;
        private const float PaperTilt = -0.05f;
        private static readonly Color PaperColor = new Color(243, 236, 216);
        private static readonly Color PaperShadow = new Color(0, 0, 0, 110);
        private static readonly Color TapeColor = new Color(233, 226, 198, 190);
        private static readonly Color InkColor = new Color(26, 26, 26);
        private static readonly Color InkSoftColor = new Color(40, 38, 32);
        private static readonly FieldInfo ActiveSpriteField = AccessTools.Field(typeof(Item), "activeSprite");
        private static readonly MethodInfo SpriteTextureSetter = AccessTools.PropertySetter(typeof(Sprite), "texture");
        private static readonly object[] NullArgs = { null };
        private sealed class Cache : IDisposable {
            public RenderTarget2D Baked;
            public Sprite WorldSprite;
            public Sprite IconSprite;
            public string Sig;
            public string Number = "";
            public string Name = "";
            public void Dispose() {
                ReleaseSprite(WorldSprite);
                ReleaseSprite(IconSprite);
                PrimaryPackage.Core.TextureStore.Detach(WorldSprite);
                PrimaryPackage.Core.TextureStore.Detach(IconSprite);
                WorldSprite = null; IconSprite = null;
                Baked?.Dispose(); Baked = null;
            }
        }
        private readonly struct TrackedItem {
            public readonly WeakReference<Item> ItemRef;
            public readonly Cache Cache;
            public TrackedItem(Item item, Cache cache) { ItemRef = new WeakReference<Item>(item); Cache = cache; }
        }
        private static ConditionalWeakTable<Item, Cache> cache = new ConditionalWeakTable<Item, Cache>();
        private static readonly List<TrackedItem> active = new List<TrackedItem>();
        private static readonly object pendingLock = new object();
        private static Dictionary<Item, bool> pending;
        private static SpriteBatch bakeBatch;
        private static bool deviceHooked;
        public static void Init() {
            GraphicsDevice gd = GameMain.Instance?.GraphicsDevice;
            if (gd != null && !deviceHooked) { gd.DeviceReset += OnDeviceReset; deviceHooked = true; }
            _ = OrderHandFont.Font;
        }
        public static void Shutdown() {
            GraphicsDevice gd = GameMain.Instance?.GraphicsDevice;
            if (gd != null && deviceHooked) { gd.DeviceReset -= OnDeviceReset; deviceHooked = false; }
            ClearAll();
        }
        private static readonly Identifier OrderCrateTagId = CheckComponent.OrderCrateTag;
        private static bool IsCrate(Item item) =>
            item != null && item.HasTag(OrderCrateTagId);
        public static void ItemDrawPrefix(Item __instance, ref SpriteEffects __state) {
            if (!IsCrate(__instance)) { return; }
            __state = __instance.SpriteEffects;
            __instance.SpriteEffects &= ~(SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically);
        }
        public static void ItemDrawPostfix(Item __instance, SpriteEffects __state) {
            if (IsCrate(__instance)) { __instance.SpriteEffects = __state; }
        }
        public static void Request(Item item, string number, string name) {
            if (!IsCrate(item)) { return; }
            Cache c = cache.GetOrCreateValue(item);
            c.Number = number ?? "";
            c.Name = name ?? "";
            Enqueue(item);
        }
        public static void Release(Item item) {
            if (item == null || !cache.TryGetValue(item, out Cache c)) { return; }
            RestoreOriginal(item);
            c.Dispose();
            for (int i = active.Count - 1; i >= 0; i--) {
                if (active[i].ItemRef.TryGetTarget(out Item ex) && ReferenceEquals(ex, item)) {
                    active.RemoveAt(i);
                }
            }
        }
        public static void SetActiveSpritePostfix(Item __instance) {
            if (!IsCrate(__instance)) { return; }
            if (cache.TryGetValue(__instance, out Cache c) && c.WorldSprite != null) {
                ActiveSpriteField?.SetValue(__instance, c.WorldSprite);
                __instance.OverrideInventorySprite = c.IconSprite;
            }
        }
        private static void Enqueue(Item item) {
            if (GameMain.MainThread != null && Thread.CurrentThread != GameMain.MainThread) {
                lock (pendingLock) { pending ??= new Dictionary<Item, bool>(); pending[item] = true; }
                return;
            }
            BakeIfNeeded(item);
        }
        private static double nextPruneTime;
        public static void ProcessPending() {
            if (GameMain.GameSession == null) {
                if (active.Count > 0 || pending != null) { ClearAll(); }
                return;
            }
            if (Timing.TotalTime >= nextPruneTime) {
                nextPruneTime = Timing.TotalTime + 5.0;
                Prune();
            }
            if (pending == null) { return; }
            Dictionary<Item, bool> todo;
            lock (pendingLock) {
                if (pending == null || pending.Count == 0) { return; }
                todo = pending; pending = null;
            }
            foreach (KeyValuePair<Item, bool> kv in todo) {
                if (kv.Key != null && !kv.Key.Removed) { BakeIfNeeded(kv.Key); }
            }
        }
        public static void Prune() {
            for (int i = active.Count - 1; i >= 0; i--) {
                if (!active[i].ItemRef.TryGetTarget(out Item ex) || ex == null || ex.Removed) {
                    active[i].Cache?.Dispose();
                    active.RemoveAt(i);
                }
            }
        }
        private static void BakeIfNeeded(Item item) {
            if (!IsCrate(item)) { return; }
            Cache c = cache.GetOrCreateValue(item);
            if (!EnsureResources(item, c)) { return; }
            string sig = $"{c.Number}|{c.Name}";
            if (c.Sig != sig || c.Baked.IsContentLost) {
                c.Sig = sig;
                Bake(item, c);
            }
            Apply(item, c);
        }
        private static bool EnsureResources(Item item, Cache c) {
            if (c.Baked != null) { return true; }
            GraphicsDevice gd = GameMain.Instance?.GraphicsDevice;
            Sprite src = item.Prefab?.Sprite;
            if (gd == null || src?.Texture == null) { return false; }
            int w = src.SourceRect.Width, h = src.SourceRect.Height;
            if (w <= 0 || h <= 0) { return false; }
            int rw = w * BakeScale, rh = h * BakeScale;
            c.Baked = new RenderTarget2D(gd, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            Vector2 origin = src.Origin * BakeScale;
            c.WorldSprite = MakeSprite(c.Baked, rw, rh, w, h, origin, src.Depth);
            c.IconSprite = MakeSprite(c.Baked, rw, rh, w, h, origin, src.Depth);
            Register(item, c);
            return true;
        }
        private static Sprite MakeSprite(RenderTarget2D rt, int srcW, int srcH, int sizeW, int sizeH, Vector2 origin, float depth) {
            Sprite s = PrimaryPackage.Core.TextureStore.Borrow(rt, new Rectangle(0, 0, srcW, srcH), null);
            if (s == null) { return null; }
            s.Origin = origin;
            s.Depth = depth;
            s.size = new Vector2(sizeW, sizeH);
            return s;
        }
        private static void Bake(Item item, Cache c) {
            GraphicsDevice gd = GameMain.Instance?.GraphicsDevice;
            if (gd == null || c.Baked == null) { return; }
            RenderTargetBinding[] prev = gd.GetRenderTargets();
            gd.SetRenderTarget(c.Baked);
            try {
                gd.Clear(Color.Transparent);
                SpriteBatch sb = bakeBatch ??= new SpriteBatch(gd);
                sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
                Sprite src = item.Prefab.Sprite;
                sb.Draw(src.Texture, Vector2.Zero, src.SourceRect, Color.White, 0f, Vector2.Zero, (float)BakeScale, SpriteEffects.None, 0f);
                DrawSticker(sb, c, c.Baked.Width, c.Baked.Height);
                sb.End();
            }
            finally {
                if (prev == null || prev.Length == 0) { gd.SetRenderTarget(null); }
                else { gd.SetRenderTargets(prev); }
            }
        }
        private static void DrawSticker(SpriteBatch sb, Cache c, int W, int H) {
            float paperW = W * 0.62f, paperH = H * 0.62f;
            var center = new Vector2(W * 0.5f, H * 0.48f);
            Quad(sb, center + new Vector2(0f, 1.5f), new Vector2(paperW + 3f, paperH + 3f), PaperTilt, PaperShadow);
            Quad(sb, center, new Vector2(paperW, paperH), PaperTilt, PaperColor);
            var tape = new Vector2(paperW * 0.16f, paperH * 0.14f);
            Vector2 tl = OrderHandFont.RotateAround(center + new Vector2(-paperW / 2f, -paperH / 2f), center, PaperTilt);
            Vector2 br = OrderHandFont.RotateAround(center + new Vector2(paperW / 2f, paperH / 2f), center, PaperTilt);
            Quad(sb, tl, tape, PaperTilt + 0.5f, TapeColor);
            Quad(sb, br, tape, PaperTilt + 0.5f, TapeColor);
            ScalableFont font = OrderHandFont.Font;
            if (font == null) { return; }
            float maxW = paperW * 0.86f;
            Text(sb, font, c.Number, center, center.Y - paperH * 0.16f, paperH * 0.34f, maxW, InkColor);
            Text(sb, font, c.Name, center, center.Y + paperH * 0.20f, paperH * 0.20f, maxW, InkSoftColor);
        }
        private static void Quad(SpriteBatch sb, Vector2 center, Vector2 size, float angle, Color col) {
            sb.Draw(GUI.WhiteTexture, center, null, col, angle, new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
        }
        private static void Text(SpriteBatch sb, ScalableFont font, string text, Vector2 center, float lineY, float px, float maxW, Color col) {
            if (string.IsNullOrEmpty(text)) { return; }
            float scale = px / font.Size;
            Vector2 m = font.MeasureString(text) * scale;
            if (m.X > maxW && m.X > 0f) { scale *= maxW / m.X; m = font.MeasureString(text) * scale; }
            var tl = new Vector2(center.X - m.X / 2f, lineY - m.Y / 2f);
            Vector2 rot = OrderHandFont.RotateAround(tl, center, PaperTilt);
            font.DrawString(sb, text, rot, col, PaperTilt, Vector2.Zero, new Vector2(scale, scale), SpriteEffects.None, 0f);
        }
        private static void Apply(Item item, Cache c) {
            if (c.WorldSprite == null) { return; }
            ActiveSpriteField?.SetValue(item, c.WorldSprite);
            item.OverrideInventorySprite = c.IconSprite;
        }
        private static void RestoreOriginal(Item item) {
            if (item.OverrideInventorySprite != null) { item.OverrideInventorySprite = null; }
            if (!item.Removed) { item.SetActiveSprite(); }
        }
        private static void Register(Item item, Cache c) {
            for (int i = active.Count - 1; i >= 0; i--) {
                if (!active[i].ItemRef.TryGetTarget(out Item ex) || ex == null || ex.Removed) {
                    active[i].Cache?.Dispose();
                    active.RemoveAt(i);
                }
                else if (ReferenceEquals(ex, item)) { return; }
            }
            active.Add(new TrackedItem(item, c));
        }
        private static void OnDeviceReset(object sender, EventArgs e) {
            for (int i = active.Count - 1; i >= 0; i--) {
                if (!active[i].ItemRef.TryGetTarget(out Item item)) {
                    active[i].Cache?.Dispose();
                    active.RemoveAt(i);
                    continue;
                }
                if (cache.TryGetValue(item, out Cache c)) {
                    c.Dispose();
                    c.Sig = null;
                }
                Enqueue(item);
            }
        }
        private static void ReleaseSprite(Sprite sprite) {
            if (sprite == null) { return; }
            SpriteTextureSetter?.Invoke(sprite, NullArgs);
            sprite.Remove();
        }
        private static void ClearAll() {
            for (int i = 0; i < active.Count; i++) {
                if (active[i].ItemRef.TryGetTarget(out Item item)) { RestoreOriginal(item); }
                active[i].Cache?.Dispose();
            }
            active.Clear();
            cache = new ConditionalWeakTable<Item, Cache>();
            lock (pendingLock) { pending = null; }
            bakeBatch?.Dispose();
            bakeBatch = null;
        }
    }
}
