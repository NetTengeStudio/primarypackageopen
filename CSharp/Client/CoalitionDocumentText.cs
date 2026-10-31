using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Documents;

namespace PrimaryPackage.CoalitionDocument
{
    public sealed class CoalitionDocumentPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted
    {
        private Harmony harmony;

        public void RoundStart() { }

        public void Attach(Harmony harmony)
        {
            this.harmony = harmony;
            Patch("terminal show",
                AccessTools.Method(typeof(Terminal), "ShowOnDisplay", new[] { typeof(string), typeof(bool), typeof(Color), typeof(bool) }),
                postfix: nameof(CoalitionDocumentRenderer.TerminalShowOnDisplayPostfix));
            Patch("terminal item loaded",
                AccessTools.Method(typeof(Terminal), nameof(Terminal.OnItemLoaded)),
                postfix: nameof(CoalitionDocumentRenderer.TerminalItemLoadedPostfix));
            Patch("apply stamp",
                AccessTools.Method(typeof(DocumentMarkComponent), nameof(DocumentMarkComponent.ApplyStamp)),
                postfix: nameof(CoalitionDocumentRenderer.ApplyStampPostfix));
            Patch("set active sprite",
                AccessTools.Method(typeof(Item), nameof(Item.SetActiveSprite)),
                postfix: nameof(CoalitionDocumentRenderer.SetActiveSpritePostfix));
            Patch("item draw flip",
                AccessTools.Method(typeof(Item), nameof(Item.Draw), new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) }),
                prefix: nameof(CoalitionDocumentRenderer.ItemDrawPrefix),
                postfix: nameof(CoalitionDocumentRenderer.ItemDrawPostfix));
            Patch("main thread pump",
                AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: nameof(CoalitionDocumentRenderer.ProcessPendingBakes));
            Patch("item set description",
                AccessTools.PropertySetter(typeof(Item), nameof(Item.Description)),
                postfix: nameof(CoalitionDocumentRenderer.ItemSetDescriptionPostfix));
            Patch("item remove",
                AccessTools.Method(typeof(Item), nameof(Item.Remove)),
                postfix: nameof(CoalitionDocumentRenderer.ItemRemovePostfix));
        }

        private void Patch(string label, MethodInfo original, string prefix = null, string postfix = null)
        {
            try
            {
                MethodInfo p = prefix != null ? AccessTools.Method(typeof(CoalitionDocumentRenderer), prefix) : null;
                MethodInfo q = postfix != null ? AccessTools.Method(typeof(CoalitionDocumentRenderer), postfix) : null;
                harmony.Patch(original, p != null ? new HarmonyMethod(p) : null, q != null ? new HarmonyMethod(q) : null);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"pp_document patch {label} failed", e);
            }
        }

        public void LoadCompleted()
        {
            if (ItemPrefab.Prefabs.TryGet(CoalitionDocumentRenderer.DocumentId, out ItemPrefab prefab))
            {
                CoalitionDocumentRenderer.DocumentPrefab = prefab;
            }
            if (ItemPrefab.Prefabs.TryGet(CoalitionDocumentRenderer.CheckId, out ItemPrefab checkPrefab))
            {
                CoalitionDocumentRenderer.CheckPrefab = checkPrefab;
            }
            CoalitionDocumentRenderer.SubscribeDeviceReset();
        }

        public void Release()
        {
            CoalitionDocumentRenderer.UnsubscribeDeviceReset();
            CoalitionDocumentRenderer.ClearCache();
            CoalitionDocumentRenderer.DocumentPrefab = null;
            CoalitionDocumentRenderer.CheckPrefab = null;
        }
    }

    public static class CoalitionDocumentRenderer
    {
        public static readonly Identifier DocumentId = "primarypkg_coalitiondocument".ToIdentifier();
        public static ItemPrefab DocumentPrefab;
        public static readonly Identifier CheckId = "pp_check".ToIdentifier();
        public static ItemPrefab CheckPrefab;

        private const int TextureWidth = 258;
        private const int TextureHeight = 348;
        private const float BodyLeft = 30f;
        private const float BodyTop = 68f;
        private const float BodyRight = 230f;
        private const float BodyBottom = 302f;
        private const float BodyWidth = BodyRight - BodyLeft;
        private const float TextScaleFactor = 0.5f;
        private const float UnscaledWrapWidth = BodyWidth / TextScaleFactor;
        private const float FallbackLineHeightPx = 24f;
        private const float StampSizeTexPx = 64f;
        private const float StampBleedScale = 1.018f;
        private const float DefaultSpriteDepth = 0.54f;
        private const float IconTiltRadians = 0.2434f;
        private const SpriteEffects FlipMask = SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically;

        private static readonly Vector2 DefaultOrigin = new Vector2(TextureWidth * 0.5f, TextureHeight * 0.5f);
        private static readonly Vector2 WorldFitSize = new Vector2(TextureWidth, TextureHeight);
        private static readonly Vector2 IconFitSize = CalculateRotatedFitSize(IconTiltRadians);
        private static readonly Rectangle FullTextureRect = new Rectangle(0, 0, TextureWidth, TextureHeight);

        private static readonly Color DocumentInkColor = new Color(36, 34, 32, 244);
        private static readonly Color DocumentInkBleedColor = new Color(36, 34, 32, 38);
        private static readonly Color CheckInkColor = new Color(42, 46, 54, 236);
        private static readonly Color CheckInkBleedColor = new Color(42, 46, 54, 32);
        private static readonly Color StampInkColor = new Color(255, 255, 255, 234);
        private static readonly Color StampBleedColor = new Color(245, 240, 235, 34);

        private static readonly AccessTools.FieldRef<Terminal, List<TerminalMessage>> MessageHistoryRef =
            CreateFieldRef<Terminal, List<TerminalMessage>>("messageHistory");
        private static readonly AccessTools.FieldRef<Item, Sprite> ActiveSpriteRef =
            CreateFieldRef<Item, Sprite>("activeSprite");
        private static readonly Action<Sprite, Texture2D> SetSpriteTextureDelegate = CreateSpriteTextureSetter();

        private static readonly FieldInfo FallbackMessageHistoryField = AccessTools.Field(typeof(Terminal), "messageHistory");
        private static readonly FieldInfo FallbackActiveSpriteField = AccessTools.Field(typeof(Item), "activeSprite");
        private static readonly MethodInfo FallbackSpriteTextureSetter = AccessTools.PropertySetter(typeof(Sprite), "texture");
        private static readonly object[] SpriteTextureNullArgs = { null };

        private static readonly Dictionary<Item, DocCache> cache = new Dictionary<Item, DocCache>(64);
        private static readonly List<Item> snapshotItems = new List<Item>(64);
        private static readonly StringBuilder textBuilder = new StringBuilder(512);

        private static readonly object pendingLock = new object();
        private static Dictionary<Item, bool> pendingQueueActive = new Dictionary<Item, bool>(16);
        private static Dictionary<Item, bool> pendingQueueStaging = new Dictionary<Item, bool>(16);
        private static volatile int hasPendingBakes;

        private static SpriteBatch bakeSpriteBatch;
        private static RenderTargetBinding[] savedRenderTargets = new RenderTargetBinding[4];
        private static GUIFont cachedFont;
        private static float cachedLineHeight = FallbackLineHeightPx * TextScaleFactor;
        private static bool deviceResetSubscribed;

        private sealed class DocCache
        {
            public RenderTarget2D Baked;
            public Sprite WorldSprite;
            public Sprite IconSprite;
            public Terminal TerminalComponent;
            public List<TerminalMessage> History;
            public DocumentMarkComponent MarkComponent;
            public ItemLabel LabelComponent;
            public bool ComponentsResolved;
            public bool IsCheckItem;
            public int SigHistoryCount;
            public string SigLastLine;
            public string SigStampsData;
            public string SigLabelText;
            public string SigDescription;
            public bool SigInitialized;
            public string[] WrappedLines = Array.Empty<string>();
            public int WrappedCount = -1;
            public string WrappedLastLine;
            public string[] LabelWrappedLines = Array.Empty<string>();
            public string LabelWrappedRawText;
            public string LabelNormalizedText;
        }

        private static AccessTools.FieldRef<T, F> CreateFieldRef<T, F>(string fieldName) where T : class
        {
            try { return AccessTools.FieldRefAccess<T, F>(fieldName); }
            catch { return null; }
        }

        private static Action<Sprite, Texture2D> CreateSpriteTextureSetter()
        {
            try
            {
                MethodInfo setter = AccessTools.PropertySetter(typeof(Sprite), "texture");
                if (setter != null)
                {
                    return (Action<Sprite, Texture2D>)Delegate.CreateDelegate(typeof(Action<Sprite, Texture2D>), setter);
                }
            }
            catch { }
            return null;
        }

        private static Vector2 CalculateRotatedFitSize(float radians)
        {
            float cos = MathF.Abs(MathF.Cos(radians));
            float sin = MathF.Abs(MathF.Sin(radians));
            return new Vector2(TextureWidth * cos + TextureHeight * sin, TextureWidth * sin + TextureHeight * cos);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDocument(Item item) =>
            item != null && (DocumentPrefab != null ? ReferenceEquals(item.Prefab, DocumentPrefab) : item.Prefab?.Identifier == DocumentId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCheck(Item item) =>
            item != null && (CheckPrefab != null ? ReferenceEquals(item.Prefab, CheckPrefab) : item.Prefab?.Identifier == CheckId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBakeTarget(Item item) => IsDocument(item) || IsCheck(item);

        public static void TerminalShowOnDisplayPostfix(Terminal __instance) => UpdateDocument(__instance?.Item, true);
        public static void TerminalItemLoadedPostfix(Terminal __instance) => UpdateDocument(__instance?.Item, true);
        public static void ApplyStampPostfix(DocumentMarkComponent __instance) => UpdateDocument(__instance?.Item, true);
        public static void ItemSetDescriptionPostfix(Item __instance) => UpdateDocument(__instance, true);
        public static void ItemRemovePostfix(Item __instance) => ReleaseOnRemove(__instance);

        public static void SetActiveSpritePostfix(Item __instance)
        {
            if (!IsBakeTarget(__instance)) { return; }
            if (GameMain.MainThread != null && Thread.CurrentThread != GameMain.MainThread)
            {
                EnqueuePendingBake(__instance, false);
                return;
            }
            if (cache.TryGetValue(__instance, out DocCache entry) && entry.Baked != null && !entry.Baked.IsContentLost)
            {
                ApplySprites(__instance, entry);
                return;
            }
            UpdateDocumentOnMainThread(__instance, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ItemDrawPrefix(Item __instance, ref SpriteEffects __state)
        {
            SpriteEffects current = __instance.SpriteEffects;
            if ((current & FlipMask) != 0 && IsBakeTarget(__instance))
            {
                __state = current;
                __instance.SpriteEffects = current & ~FlipMask;
            }
            else
            {
                __state = current;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ItemDrawPostfix(Item __instance, SpriteEffects __state)
        {
            if (__instance.SpriteEffects != __state)
            {
                __instance.SpriteEffects = __state;
            }
        }

        public static void UpdateDocument(Item item, bool forceBake)
        {
            if (!IsBakeTarget(item)) { return; }
            if (GameMain.MainThread != null && Thread.CurrentThread != GameMain.MainThread)
            {
                EnqueuePendingBake(item, forceBake);
                return;
            }
            UpdateDocumentOnMainThread(item, forceBake);
        }

        private static void EnqueuePendingBake(Item item, bool forceBake)
        {
            lock (pendingLock)
            {
                ref bool existing = ref CollectionsMarshal.GetValueRefOrAddDefault(pendingQueueActive, item, out bool present);
                existing = present ? (existing || forceBake) : forceBake;
                hasPendingBakes = 1;
            }
        }

        public static void ProcessPendingBakes()
        {
            if (hasPendingBakes == 0) { return; }
            Dictionary<Item, bool> toProcess;
            lock (pendingLock)
            {
                if (pendingQueueActive.Count == 0)
                {
                    hasPendingBakes = 0;
                    return;
                }
                toProcess = pendingQueueActive;
                pendingQueueActive = pendingQueueStaging;
                pendingQueueStaging = toProcess;
                hasPendingBakes = 0;
            }

            foreach (KeyValuePair<Item, bool> pair in toProcess)
            {
                Item item = pair.Key;
                if (item != null && !item.Removed)
                {
                    UpdateDocumentOnMainThread(item, pair.Value);
                }
            }
            toProcess.Clear();
        }

        private static void UpdateDocumentOnMainThread(Item item, bool forceBake)
        {
            if (item == null || item.Removed) { return; }
            DocCache entry = EnsureResources(item);
            if (entry?.Baked == null) { return; }

            bool contentLost = entry.Baked.IsContentLost;
            bool changed = SignatureChanged(item, entry);
            if (forceBake || changed || contentLost)
            {
                Bake(item, entry);
            }
            ApplySprites(item, entry);
        }

        private static DocCache EnsureResources(Item item)
        {
            ref DocCache entry = ref CollectionsMarshal.GetValueRefOrAddDefault(cache, item, out bool exists);
            if (!exists || entry == null)
            {
                entry = new DocCache { IsCheckItem = IsCheck(item) };
            }
            if (entry.Baked != null) { return entry; }

            GraphicsDevice graphicsDevice = GameMain.Instance?.GraphicsDevice;
            if (graphicsDevice == null) { return null; }

            entry.Baked = new RenderTarget2D(
                graphicsDevice, TextureWidth, TextureHeight, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

            Sprite prefabSprite = item.Prefab.Sprite;
            Vector2 origin = prefabSprite?.Origin ?? DefaultOrigin;
            float depth = prefabSprite?.Depth ?? DefaultSpriteDepth;

            entry.WorldSprite = CreateBakedSprite(entry.Baked, origin, depth, 0f, WorldFitSize);
            entry.IconSprite = CreateBakedSprite(entry.Baked, origin, depth, IconTiltRadians, IconFitSize);
            return entry;
        }

        private static Sprite CreateBakedSprite(RenderTarget2D renderTarget, Vector2 origin, float depth, float rotation, Vector2 fitSize)
        {
            Sprite sprite = PrimaryPackage.Core.TextureStore.Borrow(renderTarget, FullTextureRect, null);
            if (sprite == null) { return null; }
            sprite.rotation = rotation;
            sprite.Origin = origin;
            sprite.Depth = depth;
            sprite.size = fitSize;
            return sprite;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplySprites(Item item, DocCache entry)
        {
            Sprite worldSprite = entry.WorldSprite;
            if (worldSprite == null) { return; }

            if (ActiveSpriteRef != null)
            {
                ActiveSpriteRef(item) = worldSprite;
            }
            else
            {
                FallbackActiveSpriteField?.SetValue(item, worldSprite);
            }
            item.OverrideInventorySprite = entry.IconSprite;
        }

        private static void ResolveComponents(Item item, DocCache entry)
        {
            if (!entry.ComponentsResolved)
            {
                entry.TerminalComponent = item.GetComponent<Terminal>();
                entry.MarkComponent = item.GetComponent<DocumentMarkComponent>();
                entry.LabelComponent = item.GetComponent<ItemLabel>();
                entry.ComponentsResolved = true;
            }
            if (entry.History == null && entry.TerminalComponent != null)
            {
                entry.History = MessageHistoryRef != null
                    ? MessageHistoryRef(entry.TerminalComponent)
                    : FallbackMessageHistoryField?.GetValue(entry.TerminalComponent) as List<TerminalMessage>;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetLineHeight(GUIFont font)
        {
            if (ReferenceEquals(font, cachedFont))
            {
                return cachedLineHeight;
            }
            float native = font.MeasureChar('A').Y;
            cachedFont = font;
            cachedLineHeight = (native <= 1f ? FallbackLineHeightPx : native) * TextScaleFactor;
            return cachedLineHeight;
        }

        private static bool SignatureChanged(Item item, DocCache entry)
        {
            ResolveComponents(item, entry);

            List<TerminalMessage> history = entry.History;
            int count = history?.Count ?? 0;
            string lastLine = count > 0 ? history[count - 1].Text : string.Empty;
            string stampsData = entry.MarkComponent?.StampsData ?? string.Empty;
            string labelText = entry.LabelComponent?.Text ?? string.Empty;
            string descText = item.Description ?? string.Empty;

            bool changed = !entry.SigInitialized ||
                           count != entry.SigHistoryCount ||
                           !string.Equals(lastLine, entry.SigLastLine, StringComparison.Ordinal) ||
                           !string.Equals(stampsData, entry.SigStampsData, StringComparison.Ordinal) ||
                           !string.Equals(labelText, entry.SigLabelText, StringComparison.Ordinal) ||
                           !string.Equals(descText, entry.SigDescription, StringComparison.Ordinal);

            entry.SigHistoryCount = count;
            entry.SigLastLine = lastLine;
            entry.SigStampsData = stampsData;
            entry.SigLabelText = labelText;
            entry.SigDescription = descText;
            entry.SigInitialized = true;
            return changed;
        }

        private static void Bake(Item item, DocCache entry)
        {
            GraphicsDevice graphicsDevice = GameMain.Instance?.GraphicsDevice;
            if (graphicsDevice == null || entry.Baked == null) { return; }

            int targetCount = graphicsDevice.RenderTargetCount;
            if (targetCount > 0)
            {
                if (savedRenderTargets.Length < targetCount)
                {
                    savedRenderTargets = new RenderTargetBinding[targetCount];
                }
                graphicsDevice.GetRenderTargets(savedRenderTargets);
            }

            graphicsDevice.SetRenderTarget(entry.Baked);
            try
            {
                graphicsDevice.Clear(Color.Transparent);
                SpriteBatch spriteBatch = bakeSpriteBatch ??= new SpriteBatch(graphicsDevice);
                spriteBatch.Begin(
                    SpriteSortMode.Deferred,
                    BlendState.NonPremultiplied,
                    SamplerState.LinearClamp,
                    DepthStencilState.None,
                    RasterizerState.CullNone);

                DrawBase(spriteBatch, item);
                DrawBodyText(spriteBatch, item, entry);
                DrawStamps(spriteBatch, item, entry);

                spriteBatch.End();
            }
            finally
            {
                if (targetCount == 0)
                {
                    graphicsDevice.SetRenderTarget(null);
                }
                else
                {
                    graphicsDevice.SetRenderTargets(savedRenderTargets);
                    Array.Clear(savedRenderTargets, 0, targetCount);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawBase(SpriteBatch spriteBatch, Item item)
        {
            Sprite baseSprite = item.Prefab.Sprite;
            Texture2D texture = baseSprite?.Texture;
            if (texture != null)
            {
                spriteBatch.Draw(texture, Vector2.Zero, baseSprite.SourceRect, Color.White);
            }
        }

        private static void DrawBodyText(SpriteBatch spriteBatch, Item item, DocCache entry)
        {
            GUIFont font = GUIStyle.UnscaledSmallFont;
            if (font == null) { return; }

            string[] lines = ResolveBodyLines(item, entry, font);
            if (lines.Length == 0) { return; }

            float lineHeight = GetLineHeight(font);
            Color mainInk = entry.IsCheckItem ? CheckInkColor : DocumentInkColor;
            Color bleedInk = entry.IsCheckItem ? CheckInkBleedColor : DocumentInkBleedColor;
            float baseX = MathF.Round(BodyLeft);

            for (int i = 0; i < lines.Length; i++)
            {
                float rawY = BodyTop + i * lineHeight;
                if (rawY + lineHeight > BodyBottom) { break; }

                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) { continue; }

                float snappedY = MathF.Round(rawY);
                font.DrawString(
                    spriteBatch, line, new Vector2(baseX + 0.35f, snappedY + 0.35f), bleedInk,
                    rotation: 0f, origin: Vector2.Zero, scale: TextScaleFactor,
                    spriteEffects: SpriteEffects.None, layerDepth: 0f);
                font.DrawString(
                    spriteBatch, line, new Vector2(baseX, snappedY), mainInk,
                    rotation: 0f, origin: Vector2.Zero, scale: TextScaleFactor,
                    spriteEffects: SpriteEffects.None, layerDepth: 0f);
            }
        }

        private static string[] WrapAndCache(DocCache entry, GUIFont font, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return Array.Empty<string>(); }
            if (entry.LabelWrappedLines.Length > 0 && string.Equals(entry.LabelWrappedRawText, text, StringComparison.Ordinal))
            {
                return entry.LabelWrappedLines;
            }

            string normalized = text.IndexOf("\\n", StringComparison.Ordinal) >= 0
                ? text.Replace("\\n", "\n")
                : text;

            entry.LabelWrappedRawText = text;
            entry.LabelNormalizedText = normalized;
            entry.LabelWrappedLines = font.WrapText(normalized, UnscaledWrapWidth).Split('\n');
            return entry.LabelWrappedLines;
        }

        private static string[] ResolveBodyLines(Item item, DocCache entry, GUIFont font)
        {
            if (entry.IsCheckItem)
            {
                return WrapAndCache(entry, font, item.Description);
            }
            if (entry.History != null && entry.History.Count > 0)
            {
                return GetWrappedLines(entry, font);
            }
            return WrapAndCache(entry, font, entry.LabelComponent?.Text);
        }

        private static string[] GetWrappedLines(DocCache entry, GUIFont font)
        {
            List<TerminalMessage> history = entry.History;
            int count = history.Count;
            string lastLine = history[count - 1].Text;

            if (entry.WrappedCount == count &&
                entry.WrappedLines.Length > 0 &&
                string.Equals(entry.WrappedLastLine, lastLine, StringComparison.Ordinal))
            {
                return entry.WrappedLines;
            }

            textBuilder.Clear();
            ReadOnlySpan<TerminalMessage> span = CollectionsMarshal.AsSpan(history);
            for (int i = 0; i < span.Length; i++)
            {
                if (i > 0) { textBuilder.Append('\n'); }
                textBuilder.Append(span[i].Text);
            }

            string wrapped = font.WrapText(textBuilder.ToString(), UnscaledWrapWidth);
            entry.WrappedLines = wrapped.Split('\n');
            entry.WrappedCount = count;
            entry.WrappedLastLine = lastLine;
            return entry.WrappedLines;
        }

        private static void DrawStamps(SpriteBatch spriteBatch, Item item, DocCache entry)
        {
            DocumentMarkComponent mark = entry.MarkComponent;
            if (mark == null || !mark.IsStamped) { return; }

            foreach (DocumentMarkComponent.StampEntry stamp in mark.GetStamps())
            {
                Sprite sprite = StampAssets.GetStampSprite(item, stamp.StampId);
                if (sprite == null || sprite.size.X <= 0f) { continue; }

                float baseScale = StampSizeTexPx / sprite.size.X;
                sprite.Draw(
                    spriteBatch, stamp.Offset, StampBleedColor,
                    rotate: stamp.Rotation, scale: baseScale * StampBleedScale,
                    spriteEffect: SpriteEffects.None, depth: 0f);
                sprite.Draw(
                    spriteBatch, stamp.Offset, StampInkColor,
                    rotate: stamp.Rotation, scale: baseScale,
                    spriteEffect: SpriteEffects.None, depth: 0f);
            }
        }

        public static float GetOccupiedTextBottom(Item item)
        {
            if (!IsDocument(item)) { return BodyTop; }
            ref DocCache entry = ref CollectionsMarshal.GetValueRefOrAddDefault(cache, item, out bool exists);
            if (!exists || entry == null)
            {
                entry = new DocCache { IsCheckItem = false };
            }
            ResolveComponents(item, entry);

            GUIFont font = GUIStyle.UnscaledSmallFont;
            if (font == null) { return BodyTop; }

            string[] lines = ResolveBodyLines(item, entry, font);
            if (lines.Length == 0) { return BodyTop; }

            float lineHeight = GetLineHeight(font);
            int maxLines = (int)((BodyBottom - BodyTop) / lineHeight);
            int drawnLines = Math.Min(lines.Length, Math.Max(0, maxLines));
            return MathF.Min(BodyTop + drawnLines * lineHeight, BodyBottom);
        }

        public static void SubscribeDeviceReset()
        {
            GraphicsDevice graphicsDevice = GameMain.Instance?.GraphicsDevice;
            if (graphicsDevice == null || deviceResetSubscribed) { return; }
            graphicsDevice.DeviceReset += OnDeviceReset;
            deviceResetSubscribed = true;
        }

        public static void UnsubscribeDeviceReset()
        {
            GraphicsDevice graphicsDevice = GameMain.Instance?.GraphicsDevice;
            if (graphicsDevice == null || !deviceResetSubscribed) { return; }
            graphicsDevice.DeviceReset -= OnDeviceReset;
            deviceResetSubscribed = false;
        }

        private static void OnDeviceReset(object sender, EventArgs args)
        {
            snapshotItems.Clear();
            foreach (KeyValuePair<Item, DocCache> pair in cache)
            {
                snapshotItems.Add(pair.Key);
            }

            ReadOnlySpan<Item> items = CollectionsMarshal.AsSpan(snapshotItems);
            for (int i = 0; i < items.Length; i++)
            {
                Item item = items[i];
                if (item == null || item.Removed)
                {
                    if (item != null && cache.Remove(item, out DocCache removedEntry))
                    {
                        DisposeEntryResources(removedEntry);
                    }
                    continue;
                }
                if (cache.TryGetValue(item, out DocCache entry))
                {
                    DisposeEntryResources(entry);
                    entry.SigInitialized = false;
                }
                UpdateDocument(item, true);
            }
            snapshotItems.Clear();
        }

        public static void ClearCache()
        {
            foreach (KeyValuePair<Item, DocCache> pair in cache)
            {
                Item item = pair.Key;
                DisposeEntryResources(pair.Value);
                if (item != null)
                {
                    if (item.OverrideInventorySprite != null) { item.OverrideInventorySprite = null; }
                    if (!item.Removed) { item.SetActiveSprite(); }
                }
            }
            cache.Clear();
            snapshotItems.Clear();
            textBuilder.Clear();

            lock (pendingLock)
            {
                pendingQueueActive.Clear();
                pendingQueueStaging.Clear();
                hasPendingBakes = 0;
            }

            bakeSpriteBatch?.Dispose();
            bakeSpriteBatch = null;
            cachedFont = null;
        }

        private static void ReleaseOnRemove(Item item)
        {
            if (item == null || !cache.Remove(item, out DocCache entry)) { return; }
            DisposeEntryResources(entry);
            if (item.OverrideInventorySprite != null)
            {
                item.OverrideInventorySprite = null;
            }
        }

        private static void DisposeEntryResources(DocCache entry)
        {
            if (entry == null) { return; }
            if (entry.WorldSprite != null)
            {
                ReleaseSprite(entry.WorldSprite);
                PrimaryPackage.Core.TextureStore.Detach(entry.WorldSprite);
                entry.WorldSprite = null;
            }
            if (entry.IconSprite != null)
            {
                ReleaseSprite(entry.IconSprite);
                PrimaryPackage.Core.TextureStore.Detach(entry.IconSprite);
                entry.IconSprite = null;
            }
            if (entry.Baked != null)
            {
                entry.Baked.Dispose();
                entry.Baked = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReleaseSprite(Sprite sprite)
        {
            if (SetSpriteTextureDelegate != null)
            {
                SetSpriteTextureDelegate(sprite, null);
            }
            else
            {
                FallbackSpriteTextureSetter?.Invoke(sprite, SpriteTextureNullArgs);
            }
            sprite.Remove();
        }
    }
}