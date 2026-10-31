using System;
using System.Globalization;
using System.Text;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Thoughts;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Cold {
    public static class ColdDebug {
        private const int MaxLines = 24;
        private const int MaxGapLines = 8;
        private const float PadX = 10.0f;
        private const float PadY = 8.0f;
        private const float MarginX = 12.0f;
        private const float MarginY = 12.0f;

        private static readonly Color HeaderColor = new Color(150, 200, 245);
        private static readonly Color LabelColor = new Color(198, 202, 208);
        private static readonly Color ValueColor = new Color(240, 240, 236);
        private static readonly Color WarnColor = new Color(238, 196, 120);
        private static readonly Color SilentColor = new Color(140, 146, 152);

        private static readonly StringBuilder builder = new StringBuilder(128);
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        private static string[] lines;
        private static Color[] colors;
        private static int lineCount;
        private static float panelWidth;
        private static float rebuildTimer;
        private static int signature;

        private static Keys mapKey = Keys.None;
        private static bool mapKeyResolved;

        public static bool Enabled => PrimaryPackage.Core.ModSettings.ColdDebug.Value;

        public static bool MapOpen { get; private set; }

        public static Keys MapKey {
            get {
                if (!mapKeyResolved) { ResolveKey(); }
                return mapKey;
            }
        }

        public static bool Authoritative =>
            GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient;

        public static void CloseMap() => MapOpen = false;

        public static void Reset() {
            MapOpen = false;
            lineCount = 0;
            signature = 0;
            rebuildTimer = 0.0f;
        }

        public static void Disable() {
            Reset();
            lines = null;
            colors = null;
        }

        private static void ResolveKey() {
            mapKeyResolved = true;
            ColdConfig.EnsureLoaded();
            if (!Enum.TryParse(ColdConfig.DebugMapKey, true, out Keys parsed) || parsed == Keys.None) {
                DebugConsole.AddWarning("pp_cold: не удалось разобрать mapkey=\"" + ColdConfig.DebugMapKey +
                                        "\", используется F6.");
                mapKey = Keys.F6;
                return;
            }
            if (ConflictsWithChat(parsed) && !ColdConfig.DebugAllowChatKey) {
                DebugConsole.AddWarning("pp_cold: клавиша " + parsed + " занята под чат (InputType.ActiveChat). " +
                                        "Карта переведена на F6. Чтобы всё-таки использовать её, поставьте " +
                                        "allowchatkeyoverride=\"true\" в Data/pp_cold.xml — чат при этом всё равно " +
                                        "будет открываться по нажатию.");
                mapKey = Keys.F6;
                return;
            }
            if (ConflictsWithChat(parsed)) {
                DebugConsole.AddWarning("pp_cold: карта повешена на " + parsed +
                                        ", которая открывает чат. Чат подавляться не будет.");
            }
            mapKey = parsed;
        }

        private static bool ConflictsWithChat(Keys key) {
            var bindings = GameSettings.CurrentConfig.KeyMap.Bindings;
            return Bound(bindings, InputType.ActiveChat, key)
                   || Bound(bindings, InputType.Chat, key)
                   || Bound(bindings, InputType.RadioChat, key)
                   || Bound(bindings, InputType.ChatBox, key);
        }

        private static bool Bound(System.Collections.Immutable.ImmutableDictionary<InputType, KeyOrMouse> bindings,
                                  InputType type, Keys key) =>
            bindings.TryGetValue(type, out KeyOrMouse bind) && bind.MouseButton == MouseButton.None && bind.Key == key;

        public static void UpdateData(float deltaTime, Character character) {
            if (!Enabled) { return; }
            rebuildTimer -= deltaTime;
            if (rebuildTimer > 0.0f) { return; }
            rebuildTimer = ColdConfig.TickInterval;
            Rebuild(character);
        }

        public static void UpdateInput() {
            if (PrimaryPackage.Core.PlayView.Typing || Screen.Selected != GameMain.GameScreen) {
                MapOpen = false;
                return;
            }
            if (PlayerInput.KeyHit(MapKey)) {
                MapOpen = !MapOpen;
                if (MapOpen) { ColdDebugMap.ResetView(); }
                return;
            }
            if (!MapOpen) { return; }
            if (PlayerInput.KeyHit(Keys.Escape) || GUI.InputBlockingMenuOpen) {
                MapOpen = false;
                return;
            }
            Character.DisableControls = true;
        }

        private static void EnsureBuffers() {
            lines ??= new string[MaxLines];
            colors ??= new Color[MaxLines];
        }

        private static void Add(string text, Color color) {
            if (lineCount >= MaxLines) { return; }
            lines[lineCount] = text;
            colors[lineCount] = color;
            lineCount++;
        }

        private static void Flush(Color color) {
            Add(builder.ToString(), color);
            builder.Clear();
        }

        private static void Number(float value, int decimals) {
            builder.Append(value.ToString(decimals == 0 ? "0" : decimals == 1 ? "0.0" : "0.00", Culture));
        }

        private static void Rebuild(Character character) {
            EnsureBuffers();
            int hash = Signature(character);
            if (hash == signature && lineCount > 0) { return; }
            signature = hash;
            lineCount = 0;
            builder.Clear();

            bool authoritative = Authoritative;
            builder.Append("ХОЛОД · debug · ");
            builder.Append(authoritative ? "сервер/одиночка (точные значения)" : "клиент (сеть, шаг ");
            if (!authoritative) {
                Number((ColdConfig.MaxTemperature - ColdConfig.MinTemperature) / 255.0f, 2);
                builder.Append("°, интерполяция)");
            }
            Flush(HeaderColor);

            Hull hull = character?.CurrentHull;
            if (character == null) {
                Add("персонаж не выбран", SilentColor);
            } else if (hull == null) {
                builder.Append("отсек: вне помещения (");
                Number(ColdAffliction.TemperatureAround(character), 1);
                builder.Append("°)");
                Flush(LabelColor);
            } else {
                builder.Append("отсек: ");
                builder.Append(hull.DisplayName?.Value ?? "?");
                Flush(LabelColor);
                builder.Append("   raw ");
                Number(ColdField.Get(hull), 1);
                builder.Append("°   сглаж ");
                Number(ColdField.GetSmoothed(hull), 1);
                builder.Append('°');
                Flush(ValueColor);
                AddGaps(hull);
            }

            AddAffliction(character);
            AddArmor(character);
            AddNetwork(authoritative);

            builder.Append("граф: версия ");
            builder.Append(ColdField.Version.ToString(Culture));
            builder.Append(", отсеков ");
            builder.Append(ColdField.Count.ToString(Culture));
            builder.Append(", рёбер ");
            builder.Append(ColdField.EdgeCount.ToString(Culture));
            Flush(LabelColor);

            builder.Append("карта: ");
            builder.Append(MapKey.ToString());
            builder.Append(MapOpen ? " — закрыть, колесо — зум, ЛКМ — тянуть" : " — открыть");
            Flush(SilentColor);

            Measure();
        }

        private static void AddGaps(Hull hull) {
            int shown = 0;
            int total = 0;
            foreach (Gap gap in hull.ConnectedGaps) {
                if (gap.Removed || gap.linkedTo.Count < 2) { continue; }
                total++;
            }
            builder.Append("проёмы (");
            builder.Append(total.ToString(Culture));
            builder.Append("):");
            Flush(LabelColor);
            if (total == 0) {
                Add("   нет смежных отсеков", SilentColor);
                return;
            }
            foreach (Gap gap in hull.ConnectedGaps) {
                if (gap.Removed || gap.linkedTo.Count < 2) { continue; }
                if (shown >= MaxGapLines) {
                    builder.Append("   … ещё ");
                    builder.Append((total - shown).ToString(Culture));
                    Flush(SilentColor);
                    return;
                }
                shown++;
                Hull other = ReferenceEquals(gap.linkedTo[0], hull) ? gap.linkedTo[1] as Hull : gap.linkedTo[0] as Hull;
                builder.Append("   ");
                builder.Append(gap.ConnectedDoor != null ? "дверь " : "пробоина ");
                builder.Append("откр ");
                Number(gap.Open, 2);
                builder.Append("  сеч ");
                Number(gap.Size, 0);
                builder.Append("  сосед ");
                if (other == null) {
                    builder.Append("— (наружу)");
                } else {
                    Number(ColdField.GetSmoothed(other), 1);
                    builder.Append('°');
                }
                Flush(gap.Open <= 0.0f ? SilentColor : ValueColor);
            }
        }

        private static void AddAffliction(Character character) {
            float strength = ColdAffliction.Strength(character);
            builder.Append("аффликшен: ");
            Number(strength, 1);
            builder.Append(" / ");
            Number(ColdConfig.MaxStrength, 0);
            builder.Append("  (");
            builder.Append(StageName(strength));
            builder.Append(')');
            Flush(strength > ColdConfig.MaxStrength * 0.7f ? WarnColor : ValueColor);
        }

        private static string StageName(float strength) {
            if (ColdAffliction.Prefab == null) { return "нет префаба"; }
            string[] names = ColdConfig.DebugStageNames;
            int index = (int)ColdStages.GetStage(ColdConfig.MaxStrength <= 0.0f ? 0.0f : strength / ColdConfig.MaxStrength);
            if (index >= names.Length) { index = names.Length - 1; }
            return index < 0 ? "?" : names[index];
        }

        private static void AddArmor(Character character) {
            if (character == null || ColdAffliction.Prefab == null) {
                Add("резист брони: —", SilentColor);
                return;
            }
            float multiplier = ColdAffliction.Resistance(character, out string source);
            builder.Append("броня (");
            builder.Append(ColdConfig.TargetLimb.ToString());
            builder.Append("): ×");
            Number(multiplier, 2);
            builder.Append(", изоляция ");
            Number(ColdAffliction.Insulation(character), 1);
            builder.Append('°');
            Flush(multiplier < 1.0f ? HeaderColor : SilentColor);
            if (source != null) { Add("   ← " + source, SilentColor); }

            float temperature = ColdAffliction.TemperatureAround(character);
            float threshold = ColdConfig.Comfort - ColdAffliction.Insulation(character);
            builder.Append("порог мерзания ");
            Number(threshold, 1);
            builder.Append("°, сейчас ");
            Number(temperature, 1);
            builder.Append(temperature < threshold ? "° — мёрзнет" : "° — норма");
            Flush(temperature < threshold ? WarnColor : ValueColor);

            float wet = ColdAffliction.Wetness(character);
            float shiver = ColdAffliction.ShiverResistance(ColdAffliction.Normalized(character));
            builder.Append("мокрота ");
            Number(wet * 100.0f, 0);
            builder.Append("% (×");
            Number(1.0f + wet * (ColdConfig.WetMultiplier - 1.0f), 2);
            builder.Append("), дрожь греет −");
            Number(shiver * 100.0f, 0);
            builder.Append('%');
            Flush(shiver > 0.01f ? HeaderColor : wet > 0.01f ? WarnColor : SilentColor);
        }

        private static void AddNetwork(bool authoritative) {
            double now = Timing.TotalTime;
            if (GameMain.NetworkMember == null) {
                Add("сеть: одиночная игра, пакетов нет", SilentColor);
                return;
            }
            if (!authoritative) {
                builder.Append("сеть: ");
                if (double.IsNegativeInfinity(ColdClientNet.LastReceivedAt)) {
                    builder.Append("апдейтов ещё не было");
                    Flush(WarnColor);
                } else {
                    builder.Append("получено ");
                    Number((float)(now - ColdClientNet.LastReceivedAt), 1);
                    builder.Append(" с назад, ");
                    builder.Append(ColdClientNet.LastReceivedHulls.ToString(Culture));
                    builder.Append(" отс., всего ");
                    builder.Append(ColdClientNet.ReceivedMessages.ToString(Culture));
                    Flush(now - ColdClientNet.LastReceivedAt > 5.0 ? SilentColor : ValueColor);
                }
                Add(ColdClientNet.Synced ? "   полный стейт получен" : "   ожидание полного стейта", SilentColor);
                return;
            }
            Add("сеть: локальная симуляция авторитетна", LabelColor);
        }

        private static int Signature(Character character) {
            unchecked {
                int hash = 17;
                Hull hull = character?.CurrentHull;
                hash = hash * 31 + (hull?.ID ?? 0);
                if (hull != null) {
                    hash = hash * 31 + (int)(ColdField.Get(hull) * 10.0f);
                    hash = hash * 31 + (int)(ColdField.GetSmoothed(hull) * 10.0f);
                    foreach (Gap gap in hull.ConnectedGaps) {
                        if (gap.Removed || gap.linkedTo.Count < 2) { continue; }
                        hash = hash * 31 + (int)(gap.Open * 100.0f);
                        Hull other = ReferenceEquals(gap.linkedTo[0], hull)
                            ? gap.linkedTo[1] as Hull : gap.linkedTo[0] as Hull;
                        if (other != null) { hash = hash * 31 + (int)(ColdField.GetSmoothed(other) * 10.0f); }
                    }
                }
                hash = hash * 31 + (int)(ColdAffliction.Strength(character) * 10.0f);
                hash = hash * 31 + ColdField.Version;
                hash = hash * 31 + (MapOpen ? 1 : 0);
                hash = hash * 31 + ColdField.Count;
                hash = hash * 31 + ColdClientNet.ReceivedMessages;
                hash = hash * 31 + (int)(Timing.TotalTime - ColdClientNet.LastReceivedAt);
                return hash;
            }
        }

        private static void Measure() {
            panelWidth = 0.0f;
            GUIFont font = GUIStyle.SmallFont;
            if (font == null || !font.HasValue) { return; }
            for (int i = 0; i < lineCount; i++) {
                float width = font.MeasureString(lines[i]).X;
                if (width > panelWidth) { panelWidth = width; }
            }
        }

        public static void DrawPanel(SpriteBatch spriteBatch) {
            if (!Enabled || lineCount == 0) { return; }
            GUIFont font = GUIStyle.SmallFont;
            if (font == null || !font.HasValue) { return; }
            float scale = GUI.Scale;
            float lineHeight = font.MeasureString("0").Y;
            float padX = PadX * scale;
            float padY = PadY * scale;
            float boxWidth = panelWidth * scale + padX * 2.0f;
            float boxHeight = lineHeight * scale * lineCount + padY * 2.0f;
            var pos = new Vector2(GameMain.GraphicsWidth - boxWidth - MarginX * scale, MarginY * scale);

            ToastRenderer.DrawBox(spriteBatch, pos, new Vector2(boxWidth, boxHeight),
                Color.Black * 0.62f, new Color(90, 130, 170) * 0.5f, 0.0f, Math.Max(1.0f, scale));

            var cursor = new Vector2(pos.X + padX, pos.Y + padY);
            Vector2 textScale = Vector2.One * scale;
            for (int i = 0; i < lineCount; i++) {
                font.DrawString(spriteBatch, lines[i], cursor, colors[i], 0.0f, Vector2.Zero, textScale,
                    SpriteEffects.None, 0.0f);
                cursor.Y += lineHeight * scale;
            }
        }
    }
}
