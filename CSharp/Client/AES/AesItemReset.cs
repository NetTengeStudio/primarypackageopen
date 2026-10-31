using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Aes {
    internal static class AesItemReset {
        private const string CommandName = "aes_fixitems";
        private const float Epsilon = 0.0001f;
        private const int MaxReportRows = 40;

        [Flags]
        private enum Fix { None = 0, Scale = 1, Depth = 2, Color = 4, All = Scale | Depth | Color }

        private sealed class Row {
            public int Total, Scale, Depth, Color, OutOfRange;
            public float ScaleFrom, ScaleTo, DepthFrom, DepthTo;
        }

        private static DebugConsole.Command command;

        public static void Register() {
            if (command != null) { return; }
            command = new DebugConsole.Command(CommandName,
                CommandName + " [apply <scale|depth|color|all>] <sel|all|идентификатор> — сверить предметы с их префабами; без apply только отчёт",
                Execute);
            PrimaryPackage.Core.ModCommands.Adopt(command);
        }

        public static void Unregister() {
            if (command == null) { return; }
            PrimaryPackage.Core.ModCommands.Drop(command);
            command = null;
        }

        private static void Execute(string[] args) {
            if (!SubEditorScreen.IsSubEditor()) {
                DebugConsole.NewMessage(CommandName + " работает только в редакторе подлодок.", Color.Yellow);
                return;
            }
            int i = 0;
            bool apply = args.Length > i && string.Equals(args[i], "apply", StringComparison.OrdinalIgnoreCase);
            Fix groups = Fix.None;
            if (apply) {
                i++;
                if (args.Length <= i || !TryParseGroup(args[i], out groups)) {
                    DebugConsole.NewMessage("Укажите что чинить: scale, depth, color или all.", Color.Yellow);
                    return;
                }
                i++;
                if (args.Length <= i) {
                    DebugConsole.NewMessage("Укажите область: sel (выделенные), all (вся лодка) или идентификатор.", Color.Yellow);
                    return;
                }
            }
            string scope = args.Length > i ? args[i] : null;
            bool selectionOnly = scope != null && string.Equals(scope, "sel", StringComparison.OrdinalIgnoreCase);
            bool wholeSub = scope == null || string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
            string filter = selectionOnly || wholeSub ? null : scope;

            if (selectionOnly && MapEntity.SelectedList.Count == 0) {
                DebugConsole.NewMessage("Ничего не выделено.", Color.Yellow);
                return;
            }

            var rows = new Dictionary<string, Row>();
            int scanned = 0;
            foreach (Item it in Item.ItemList) {
                if (it == null) { continue; }
                ItemPrefab prefab = it.Prefab;
                if (prefab == null) { continue; }
                if (selectionOnly && !MapEntity.SelectedList.Contains(it)) { continue; }
                string id = prefab.Identifier.Value;
                if (filter != null && id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                scanned++;

                float wantScale = prefab.Scale;
                bool scaleOff = Math.Abs(it.Scale - wantScale) > Epsilon;
                bool outOfRange = it.Scale < prefab.MinScale - Epsilon || it.Scale > prefab.MaxScale + Epsilon;

                bool depthOff = false;
                float wantDepth = 0f;
                if (prefab.Sprite != null && it.Sprite != null) {
                    wantDepth = prefab.Sprite.Depth;
                    depthOff = Math.Abs(it.SpriteDepth - wantDepth) > Epsilon;
                }
                bool colorOff = it.SpriteColor != prefab.SpriteColor || it.InventoryIconColor != prefab.InventoryIconColor;

                if (!scaleOff && !depthOff && !colorOff) { continue; }
                if (!rows.TryGetValue(id, out Row row)) { row = new Row(); rows[id] = row; }
                row.Total++;
                if (scaleOff) { if (row.Scale == 0) { row.ScaleFrom = it.Scale; row.ScaleTo = wantScale; } row.Scale++; }
                if (outOfRange) { row.OutOfRange++; }
                if (depthOff) { if (row.Depth == 0) { row.DepthFrom = it.SpriteDepth; row.DepthTo = wantDepth; } row.Depth++; }
                if (colorOff) { row.Color++; }

                if (!apply) { continue; }
                if ((groups & Fix.Scale) != 0 && scaleOff) { it.Scale = wantScale; }
                if ((groups & Fix.Depth) != 0 && depthOff) { it.SpriteDepth = wantDepth; }
                if ((groups & Fix.Color) != 0 && colorOff) {
                    it.SpriteColor = prefab.SpriteColor;
                    it.InventoryIconColor = prefab.InventoryIconColor;
                }
            }

            if (scanned == 0) {
                DebugConsole.NewMessage(filter == null ? "Предметов не найдено." : $"Предметов по фильтру '{filter}' не найдено.", Color.Yellow);
                return;
            }
            if (rows.Count == 0) {
                DebugConsole.NewMessage($"Проверено предметов: {scanned}. Расхождений с префабами нет.", Color.LightGreen);
                return;
            }

            var ids = new List<string>(rows.Keys);
            ids.Sort(StringComparer.Ordinal);
            int totalScale = 0, totalDepth = 0, totalColor = 0, totalItems = 0, totalOutOfRange = 0, shown = 0;
            foreach (string id in ids) {
                Row row = rows[id];
                totalScale += row.Scale; totalDepth += row.Depth; totalColor += row.Color;
                totalItems += row.Total; totalOutOfRange += row.OutOfRange;
                if (shown++ >= MaxReportRows) { continue; }
                DebugConsole.NewMessage($"  {id} — {row.Total} шт.: {Describe(row)}",
                    row.OutOfRange > 0 ? Color.Orange : Color.Cyan);
            }
            if (ids.Count > MaxReportRows) {
                DebugConsole.NewMessage($"  ...и ещё идентификаторов: {ids.Count - MaxReportRows}", Color.Gray);
            }
            string where = selectionOnly ? " среди выделенных" : filter != null ? $" по фильтру '{filter}'" : "";
            DebugConsole.NewMessage(
                $"Проверено {scanned}{where}. Отличается от префаба: {totalItems} шт. в {ids.Count} идентификаторах — масштаб {totalScale}, глубина {totalDepth}, цвет {totalColor}.",
                Color.Cyan);
            if (totalOutOfRange > 0) {
                DebugConsole.NewMessage(
                    $"Вне допустимого диапазона префаба (почти наверняка сломано): {totalOutOfRange} шт.",
                    Color.Orange);
            }
            if (apply) {
                DebugConsole.NewMessage(
                    $"Приведено к префабу: масштаб {((groups & Fix.Scale) != 0 ? totalScale : 0)}, " +
                    $"глубина {((groups & Fix.Depth) != 0 ? totalDepth : 0)}, " +
                    $"цвет {((groups & Fix.Color) != 0 ? totalColor : 0)}. Сохраните лодку.",
                    Color.LightGreen);
            }
            else {
                DebugConsole.NewMessage(
                    "Отличие от префаба само по себе не поломка — строитель мог задать размер намеренно. " +
                    "Чините точечно: выделите предметы и " + CommandName + " apply scale sel, либо " + CommandName + " apply scale <идентификатор>.",
                    Color.Yellow);
            }
        }

        private static string Describe(Row row) {
            var parts = new List<string>(4);
            if (row.Scale > 0) { parts.Add($"масштаб {row.Scale} ({F(row.ScaleFrom)} → {F(row.ScaleTo)})"); }
            if (row.OutOfRange > 0) { parts.Add($"вне диапазона {row.OutOfRange}"); }
            if (row.Depth > 0) { parts.Add($"глубина {row.Depth} ({F(row.DepthFrom)} → {F(row.DepthTo)})"); }
            if (row.Color > 0) { parts.Add($"цвет {row.Color}"); }
            return string.Join(", ", parts);
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryParseGroup(string arg, out Fix fix) {
            switch (arg.ToLowerInvariant()) {
                case "scale": fix = Fix.Scale; return true;
                case "depth": fix = Fix.Depth; return true;
                case "color": fix = Fix.Color; return true;
                case "all": fix = Fix.All; return true;
                default: fix = Fix.None; return false;
            }
        }
    }
}
