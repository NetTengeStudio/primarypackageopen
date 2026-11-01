using System;
using Barotrauma;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    internal static class GuiRowFit {
        public static void FitWrappedRow(GUIListBox owner, GUITextBlock block, int paddingPx = 6) {
            for (int pass = 0; pass < 4; pass++) {
                owner.RecalculateChildren();
                int height = (int)Math.Ceiling(block.TextSize.Y) + paddingPx;
                if (block.RectTransform.MinSize.Y == height) { break; }
                block.RectTransform.MinSize = new Point(0, height);
            }
        }
    }
}
