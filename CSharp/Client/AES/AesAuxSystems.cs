using System;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Aes {
    public static class AesAuxSystems {
        public enum Kind { Fog = 0, Radiation = 1, Script = 2, Cold = 3 }

        private static readonly bool[] visible = { true, true, true, true };
        private static bool anyHidden;

        public static bool Visible(Kind kind) => visible[(int)kind];

        public static bool ShouldHideFromSelection(Item item) {
            if (!anyHidden) { return false; }
            if (!visible[(int)Kind.Fog] && item.GetComponent<FogZoneComponent>() != null) { return true; }
            if (!visible[(int)Kind.Radiation] && item.GetComponent<RadiationZoneComponent>() != null) { return true; }
            if (!visible[(int)Kind.Script] && item.GetComponent<ScriptMessageComponent>() != null) { return true; }
            if (!visible[(int)Kind.Cold] &&
                (item.GetComponent<PrimaryPackage.Cold.ColdZoneComponent>() != null ||
                 item.GetComponent<PrimaryPackage.Cold.ColdSourceComponent>() != null)) { return true; }
            return false;
        }

        private static readonly (string Label, Kind Kind)[] Entries = {
            ("Лёгкий туман", Kind.Fog),
            ("Радиоактивное поле", Kind.Radiation),
            ("Скрипт", Kind.Script),
            ("Холод", Kind.Cold)
        };

        public static void BuildPanel(GUIComponent showEntitiesPanel) {
            GUILayoutGroup padded = showEntitiesPanel?.GetChild<GUILayoutGroup>();
            if (padded == null || padded.GetChildByUserData("aux_0") != null) { return; }

            GUIComponent gap = padded.GetChildByUserData("gap");
            int insertAt = gap != null
                ? padded.RectTransform.GetChildIndex(gap.RectTransform) + 1
                : padded.RectTransform.Children.Count();

            var header = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), padded.RectTransform),
                "Вспомогательные системы", font: GUIStyle.SubHeadingFont);
            header.RectTransform.MinSize = new Point(0, (int)(header.Rect.Height * 1.5f));
            header.RectTransform.RepositionChildInHierarchy(insertAt);

            for (int i = 0; i < Entries.Length; i++) {
                Kind kind = Entries[i].Kind;
                var tb = new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), padded.RectTransform), Entries[i].Label) {
                    UserData = "aux_" + (int)kind,
                    Selected = visible[(int)kind],
                    OnSelected = obj => {
                        visible[(int)kind] = obj.Selected;
                        anyHidden = false;
                        for (int k = 0; k < visible.Length; k++) { anyHidden |= !visible[k]; }
                        return true;
                    }
                };
                tb.RectTransform.RepositionChildInHierarchy(insertAt + 1 + i);
            }

            showEntitiesPanel.RectTransform.NonScaledSize = new Point(
                (int)Math.Max(showEntitiesPanel.RectTransform.NonScaledSize.X,
                    padded.RectTransform.Children.Max(c => (int)((c.GUIComponent as GUITickBox)?.TextBlock.TextSize.X ?? 0)) / padded.RectTransform.RelativeSize.X),
                (int)(padded.RectTransform.Children.Sum(c => c.MinSize.Y) / padded.RectTransform.RelativeSize.Y));
            GUITextBlock.AutoScaleAndNormalize(padded.Children.Where(c => c is GUITickBox).Select(c => ((GUITickBox)c).TextBlock));
            padded.Recalculate();
        }
    }
}
