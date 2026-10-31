using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SubSeal.Client
{
    // GUI-only masking. Never changes the submarine data, button callbacks,
    // selection state, keyboard navigation or expanded-preview Close/Escape.
    internal static class PreviewSurfacePatches
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly ConditionalWeakTable<GUIComponent, GUIFrame> overlays =
            new ConditionalWeakTable<GUIComponent, GUIFrame>();
        private static readonly List<WeakReference<GUIFrame>> overlayRefs = new List<WeakReference<GUIFrame>>();

        internal static void HideAll()
        {
            for (int i = overlayRefs.Count - 1; i >= 0; i--)
            {
                if (overlayRefs[i].TryGetTarget(out GUIFrame overlay))
                {
                    overlay.Visible = false;
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
            if (!PreviewLock.ProtectionActive || sub?.Info is null || __state < 0) { return; }
            GUIComponent panel = infoFrame.GetChild(__state);
            GUIComponent padded = panel?.GetChild(0);
            GUIComponent button = padded?.GetChild(0);
            Mask(button);
        }

        internal static void AfterSelectionRefresh(object __instance) => MaskSelection(__instance);

        internal static void AfterSelectSubmarine(object __instance) => MaskSelection(__instance);

        private static void MaskSelection(object selection)
        {
            if (!PreviewLock.ProtectionActive || selection is null) { return; }
            Type type = selection.GetType();
            Array cards = type.GetField("submarineDisplays", InstanceFields)?.GetValue(selection) as Array;
            if (cards is not null)
            {
                foreach (object card in cards)
                {
                    if (card is null) { continue; }
                    Type cardType = card.GetType();
                    if (cardType.GetField("displayedSubmarine", InstanceFields)?.GetValue(card) is not SubmarineInfo) { continue; }

                    GUIImage image = cardType.GetField("submarineImage", InstanceFields)?.GetValue(card) as GUIImage;
                    if (image is not null)
                    {
                        image.Sprite = null;
                        Mask(image);
                    }
                    GUITextBlock middle = cardType.GetField("middleTextBlock", InstanceFields)?.GetValue(card) as GUITextBlock;
                    if (middle is not null)
                    {
                        middle.Text = PreviewLock.Text;
                        middle.AutoDraw = true;
                    }
                }
            }

            GUIImage background = type.GetField("listBackground", InstanceFields)?.GetValue(selection) as GUIImage;
            if (background is not null)
            {
                background.Sprite = null;
                Mask(background);
            }
        }

        private static void Mask(GUIComponent surface)
        {
            if (surface is null) { return; }
            GUIFrame overlay = overlays.GetValue(surface, component =>
            {
                GUIFrame black = new GUIFrame(new RectTransform(Vector2.One, component.RectTransform), style: null)
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
    }
}
