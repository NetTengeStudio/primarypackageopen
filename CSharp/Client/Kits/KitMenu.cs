using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Kits {
    internal static class KitMenu {
        private enum Tab { Overview, Variants }

        private const int VariantCount = 6;

        private static GUIFrame frame;
        private static GUIListBox list;
        private static GUIFrame rightPanel;
        private static GUITextBox searchBox;
        private static GUIButton overviewButton;
        private static GUIButton variantsButton;
        private static GUIFrame tabContent;
        private static Tab tab = Tab.Overview;
        private static Entry selected;
        private static readonly List<CharacterInfo> variantInfos = new();

        private sealed class Entry {
            public HumanPrefab Prefab;
            public bool IsKit;
        }

        public static void Toggle() {
            if (frame != null) {
                Close();
                return;
            }
            Open();
        }

        public static void Close() {
            frame = null;
            list = null;
            rightPanel = null;
            searchBox = null;
            overviewButton = null;
            variantsButton = null;
            tabContent = null;
            selected = null;
            variantInfos.Clear();
        }

        public static void AddToGUIUpdateList() => frame?.AddToGUIUpdateList();

        public static void OnKitsChanged() {
            if (frame == null) { return; }
            RefreshList(searchBox?.Text ?? "");
        }

        private static void Open() {
            KitClient.RequestList();
            tab = Tab.Overview;
            frame = new GUIFrame(new RectTransform(new Vector2(0.66f, 0.74f), GUI.Canvas, Anchor.Center));
            var main = new GUILayoutGroup(new RectTransform(new Vector2(0.96f, 0.94f), frame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.012f };
            var header = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.07f), main.RectTransform), isHorizontal: true) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.9f, 1f), header.RectTransform), TextManager.Get("pp_kits.title"), font: GUIStyle.LargeFont);
            new GUIButton(new RectTransform(new Vector2(0.1f, 1f), header.RectTransform), "X") {
                OnClicked = (_, _) => { Close(); return true; }
            };
            var content = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.93f), main.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            var left = new GUILayoutGroup(new RectTransform(new Vector2(0.4f, 1f), content.RectTransform)) { Stretch = true, RelativeSpacing = 0.01f };
            searchBox = new GUITextBox(new RectTransform(new Vector2(1f, 0.07f), left.RectTransform), "");
            searchBox.OnTextChanged += (_, text) => { RefreshList(text); return true; };
            list = new GUIListBox(new RectTransform(new Vector2(1f, 0.93f), left.RectTransform)) {
                OnSelected = (_, userData) => { Select(userData as Entry); return true; }
            };
            var right = new GUILayoutGroup(new RectTransform(new Vector2(0.6f, 1f), content.RectTransform)) { Stretch = true, RelativeSpacing = 0.008f };
            var tabs = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.07f), right.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.01f };
            overviewButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), tabs.RectTransform), TextManager.Get("pp_kits.tab.overview"), style: "GUITabButton") {
                Selected = true,
                OnClicked = (_, _) => { SetTab(Tab.Overview); return true; }
            };
            variantsButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), tabs.RectTransform), TextManager.Get("pp_kits.tab.variants"), style: "GUITabButton") {
                OnClicked = (_, _) => { SetTab(Tab.Variants); return true; }
            };
            rightPanel = new GUIFrame(new RectTransform(new Vector2(1f, 0.93f), right.RectTransform), style: "InnerFrame");
            tabContent = new GUIFrame(new RectTransform(new Vector2(0.96f, 0.96f), rightPanel.RectTransform, Anchor.Center), style: null);
            RefreshList("");
        }

        private static void SetTab(Tab next) {
            tab = next;
            if (overviewButton != null) { overviewButton.Selected = next == Tab.Overview; }
            if (variantsButton != null) { variantsButton.Selected = next == Tab.Variants; }
            RefreshTab();
        }

        private static void RefreshList(string filter) {
            if (list == null) { return; }
            Identifier selectedId = selected?.Prefab?.Identifier ?? Identifier.Empty;
            list.ClearChildren();
            foreach (var pair in KitStore.All) {
                if (pair.Value.Prefab == null) { continue; }
                AddRow($"★ {pair.Key.Value}", new Entry { Prefab = pair.Value.Prefab, IsKit = true }, filter, GUIStyle.Orange);
            }
            foreach (HumanPrefab human in NPCSet.Sets.SelectMany(s => s.Humans).OrderBy(h => h.Identifier.Value)) {
                AddRow($"{human.Identifier.Value} ({human.NpcSetIdentifier.Value})", new Entry { Prefab = human, IsKit = false }, filter, GUIStyle.TextColorNormal);
            }
            if (!selectedId.IsEmpty) {
                Entry match = list.Content.Children
                    .Select(c => c.UserData as Entry)
                    .FirstOrDefault(e => e?.Prefab?.Identifier == selectedId);
                Select(match);
            }
        }

        private static void AddRow(string label, Entry entry, string filter, Color color) {
            if (!string.IsNullOrWhiteSpace(filter) && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)) { return; }
            new GUITextBlock(new RectTransform(new Point(list.Content.Rect.Width, (int)(22 * GUI.Scale)), list.Content.RectTransform), label, textColor: color) {
                UserData = entry,
                CanBeFocused = true
            };
        }

        private static void Select(Entry entry) {
            selected = entry;
            RegenerateVariants();
            RefreshTab();
        }

        private static void RefreshTab() {
            if (tabContent == null) { return; }
            tabContent.ClearChildren();
            if (selected?.Prefab == null) { return; }
            if (tab == Tab.Overview) {
                BuildOverview();
            } else {
                BuildVariants();
            }
        }

        private static void BuildOverview() {
            HumanPrefab prefab = selected.Prefab;
            CharacterInfo info = prefab.CreateCharacterInfo();
            var lay = new GUILayoutGroup(new RectTransform(Vector2.One, tabContent.RectTransform)) { RelativeSpacing = 0.01f, Stretch = true };
            new GUICustomComponent(new RectTransform(new Vector2(1f, 0.26f), lay.RectTransform),
                onDraw: (sb, comp) => info?.DrawIcon(sb, comp.Rect.Center.ToVector2(), comp.Rect.Size.ToVector2() * 0.95f));
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), lay.RectTransform), info.Name, textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont);
            JobPrefab job = info.Job?.Prefab;
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), lay.RectTransform),
                job != null ? job.Name : TextManager.Get("pp_kits.nojob"),
                textColor: job?.UIColor ?? GUIStyle.TextColorNormal, textAlignment: Alignment.Center);
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), lay.RectTransform),
                string.Format(KitClient.Text("pp_kits.stats"), prefab.HealthMultiplier.ToString("0.##"), prefab.SkillMultiplier.ToString("0.##"), prefab.ItemSets.Count),
                textAlignment: Alignment.Center, font: GUIStyle.SmallFont);
            var itemList = new GUIListBox(new RectTransform(new Vector2(1f, 0.44f), lay.RectTransform));
            ContentXElement? itemSet = prefab?.ItemSets?.FirstOrDefault().element;
            if (itemSet != null) {
                foreach (ContentXElement itemEl in itemSet.Elements()) { AddItemRow(itemList, itemEl, 0); }
            }
            hostileBox = new GUITickBox(new RectTransform(new Vector2(1f, 0.05f), lay.RectTransform),
                TextManager.Get("pp_kits.hostile")) { Selected = false };
            var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.08f), lay.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
            float w = selected.IsKit ? 0.27f : 0.5f;
            new GUIButton(new RectTransform(new Vector2(w, 1f), buttons.RectTransform), TextManager.Get("pp_kits.possess")) {
                OnClicked = (_, _) => {
                    SpawnSelected(control: true);
                    Close();
                    return true;
                }
            };
            new GUIButton(new RectTransform(new Vector2(w, 1f), buttons.RectTransform), TextManager.Get("pp_kits.spawnnpc")) {
                OnClicked = (_, _) => {
                    SpawnSelected(control: false);
                    return true;
                }
            };
            if (!selected.IsKit) { return; }
            new GUIButton(new RectTransform(new Vector2(0.23f, 1f), buttons.RectTransform), TextManager.Get("pp_kits.export")) {
                OnClicked = (_, _) => {
                    KitClient.ExportKit(selected.Prefab.Identifier.Value);
                    return true;
                }
            };
            new GUIButton(new RectTransform(new Vector2(0.23f, 1f), buttons.RectTransform), TextManager.Get("pp_kits.delete"), color: GUIStyle.Red) {
                OnClicked = (_, _) => {
                    KitClient.DeleteKit(selected.Prefab.Identifier.Value);
                    return true;
                }
            };
        }

        private static GUITickBox hostileBox;

        private static void SpawnSelected(bool control) {
            if (selected?.Prefab == null) { return; }
            bool hostile = !control && hostileBox is { Selected: true };
            if (KitClient.Mp) {
                KitClient.SendSpawn(selected.Prefab.Identifier.Value, KitClient.MenuSpawnPos(), control, hostile);
            } else {
                KitClient.SpawnLocal(selected.Prefab, KitClient.MenuSpawnPos(), control, hostile);
            }
        }

        private static void RegenerateVariants() {
            variantInfos.Clear();
            HumanPrefab prefab = selected?.Prefab;
            if (prefab == null) { return; }
            for (int i = 0; i < VariantCount; i++) { variantInfos.Add(prefab.CreateCharacterInfo()); }
        }

        private static void BuildVariants() {
            var lay = new GUILayoutGroup(new RectTransform(Vector2.One, tabContent.RectTransform)) { RelativeSpacing = 0.01f, Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), lay.RectTransform), TextManager.Get("pp_kits.variants.hint"), font: GUIStyle.SmallFont, wrap: true);
            var grid = new GUIListBox(new RectTransform(new Vector2(1f, 0.84f), lay.RectTransform));
            int cardHeight = (int)(84 * GUI.Scale);
            foreach (CharacterInfo info in variantInfos) {
                var card = new GUIFrame(new RectTransform(new Point(grid.Content.Rect.Width, cardHeight), grid.Content.RectTransform), style: "ListBoxElement") { CanBeFocused = false };
                CharacterInfo captured = info;
                new GUICustomComponent(new RectTransform(new Vector2(0.18f, 0.95f), card.RectTransform, Anchor.CenterLeft) { RelativeOffset = new Vector2(0.01f, 0f) },
                    onDraw: (sb, comp) => captured.DrawIcon(sb, comp.Rect.Center.ToVector2(), comp.Rect.Size.ToVector2() * 0.95f));
                var col = new GUILayoutGroup(new RectTransform(new Vector2(0.78f, 0.92f), card.RectTransform, Anchor.CenterRight) { RelativeOffset = new Vector2(0.01f, 0f) }) { RelativeSpacing = 0.03f };
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.33f), col.RectTransform), captured.Name, font: GUIStyle.SubHeadingFont);
                JobPrefab job = captured.Job?.Prefab;
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.3f), col.RectTransform),
                    job != null ? job.Name : TextManager.Get("pp_kits.nojob"),
                    textColor: job?.UIColor ?? GUIStyle.TextColorNormal, font: GUIStyle.SmallFont);
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.3f), col.RectTransform), SkillSummary(captured), font: GUIStyle.SmallFont);
            }
            var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.08f), lay.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttons.RectTransform), TextManager.Get("pp_kits.variants.reroll")) {
                OnClicked = (_, _) => {
                    RegenerateVariants();
                    RefreshTab();
                    return true;
                }
            };
        }

        private static string SkillSummary(CharacterInfo info) {
            if (info.Job == null) { return ""; }
            return string.Join("  ", info.Job.GetSkills()
                .OrderByDescending(s => s.Level)
                .Take(3)
                .Select(s => $"{TextManager.Get("SkillName." + s.Identifier).Value} {(int)s.Level}"));
        }

        private static void AddItemRow(GUIListBox itemList, ContentXElement itemEl, int depth) {
            string id = itemEl.GetAttributeString("identifier", "");
            var itemPrefab = MapEntityPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
            int height = (int)(24 * GUI.Scale);
            var row = new GUIFrame(new RectTransform(new Point(itemList.Content.Rect.Width, height), itemList.Content.RectTransform), style: null);
            int indent = depth * height;
            Sprite icon = itemPrefab?.InventoryIcon ?? itemPrefab?.Sprite;
            if (icon != null) {
                new GUIImage(new RectTransform(new Point(height, height), row.RectTransform) { AbsoluteOffset = new Point(indent, 0) }, icon, null, GUIImage.ScalingMode.ScaleToFitSmallestExtent) {
                    Color = itemPrefab.InventoryIcon != null ? itemPrefab.InventoryIconColor : itemPrefab.SpriteColor
                };
            }
            bool equip = itemEl.GetAttributeBool("equip", false);
            new GUITextBlock(new RectTransform(new Point(itemList.Content.Rect.Width - indent - height - 4, height), row.RectTransform) { AbsoluteOffset = new Point(indent + height + 4, 0) },
                (itemPrefab?.Name.Value ?? id) + (equip ? KitClient.Text("pp_kits.equipped") : ""),
                textColor: itemPrefab != null ? GUIStyle.TextColorNormal : GUIStyle.Red,
                font: GUIStyle.SmallFont);
            foreach (ContentXElement child in itemEl.Elements()) { AddItemRow(itemList, child, depth + 1); }
        }
    }
}
