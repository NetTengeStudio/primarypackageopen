using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SubSeal.Client
{
    internal static class PreviewSurfacePatches
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly Type SelectionType =
            typeof(SubmarinePreview).Assembly.GetType("Barotrauma.SubmarineSelection");
        private static readonly FieldInfo DisplayCardsField =
            SelectionType?.GetField("submarineDisplays", InstanceFields);
        private static readonly FieldInfo BackgroundField =
            SelectionType?.GetField("listBackground", InstanceFields);
        private static readonly Type CardType = DisplayCardsField?.FieldType.GetElementType();
        private static readonly FieldInfo DisplayedSubmarineField =
            CardType?.GetField("displayedSubmarine", InstanceFields);
        private static readonly FieldInfo CardImageField =
            CardType?.GetField("submarineImage", InstanceFields);
        private static readonly FieldInfo CardTextField =
            CardType?.GetField("middleTextBlock", InstanceFields);

        private static readonly ConditionalWeakTable<GUIComponent, GUIFrame> overlays =
            new ConditionalWeakTable<GUIComponent, GUIFrame>();

        private static readonly List<WeakReference<GUIFrame>> overlayRefs =
            new List<WeakReference<GUIFrame>>();

        internal static void SetProtection(bool protectedContent)
        {
            for (int i = overlayRefs.Count - 1; i >= 0; i--)
            {
                if (overlayRefs[i].TryGetTarget(out GUIFrame overlay))
                {
                    overlay.Visible = protectedContent;
                }
                else
                {
                    overlayRefs.RemoveAt(i);
                }
            }
        }

        internal static bool BeforeGetSelectionPreview(ref Sprite __result)
        {
            if (!PreviewLock.ProtectionActive) { return true; }
            __result = null;
            return false;
        }

        internal static void BeforeCreateTabPreview(GUIFrame infoFrame, ref int __state)
            => __state = infoFrame is null ? -1 : infoFrame.CountChildren;

        internal static void AfterCreateTabPreview(GUIFrame infoFrame, Submarine sub, int __state)
        {
            if (sub?.Info is null || infoFrame is null || __state < 0) { return; }
            GUIComponent panel = infoFrame.GetChild(__state);
            GUIComponent padded = panel?.GetChild(0);
            Track(padded?.GetChild(0));
        }

        internal static void AfterSelectionRefresh(object __instance) => TrackSelection(__instance);

        internal static void AfterSelectSubmarine(object __instance) => TrackSelection(__instance);

        internal static void Track(GUIComponent surface)
        {
            if (surface is null) { return; }

            GUIFrame overlay = overlays.GetValue(surface, component =>
            {
                GUIFrame black = new GUIFrame(
                    new RectTransform(Vector2.One, component.RectTransform), style: null)
                {
                    Color = Color.Black,
                    HoverColor = Color.Black,
                    SelectedColor = Color.Black,
                    PressedColor = Color.Black,
                    CanBeFocused = false
                };

                new GUITextBlock(
                    new RectTransform(Vector2.One, black.RectTransform, Anchor.Center),
                    PreviewLock.Text,
                    textColor: Color.White,
                    font: GUIStyle.LargeFont,
                    textAlignment: Alignment.Center)
                {
                    CanBeFocused = false
                };

                overlayRefs.Add(new WeakReference<GUIFrame>(black));
                return black;
            });

            overlay.Visible = PreviewLock.ProtectionActive;
        }

        private static void TrackSelection(object selection)
        {
            if (selection is null || DisplayCardsField is null ||
                SelectionType is null || !SelectionType.IsInstanceOfType(selection)) { return; }

            bool protectedContent = PreviewLock.ProtectionActive;
            if (DisplayCardsField.GetValue(selection) is Array cards)
            {
                foreach (object card in cards)
                {
                    if (card is null || DisplayedSubmarineField?.GetValue(card) is not SubmarineInfo) {
                        continue;
                    }

                    GUIImage image = CardImageField?.GetValue(card) as GUIImage;
                    if (image is not null)
                    {
                        if (protectedContent) { image.Sprite = null; }
                        Track(image);
                    }

                    if (protectedContent &&
                        CardTextField?.GetValue(card) is GUITextBlock textBlock)
                    {
                        textBlock.AutoDraw = false;
                    }
                }
            }

            GUIImage background = BackgroundField?.GetValue(selection) as GUIImage;
            if (background is not null)
            {
                if (protectedContent) { background.Sprite = null; }
                Track(background);
            }
        }
    }
}
