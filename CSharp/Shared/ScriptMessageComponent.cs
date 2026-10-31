using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Zones {
    public enum ZoneEasing { Линейно, Разгон, Торможение, Плавно, Отскок, Упруго }
    public enum SlideFrom { Нет, Снизу, Сверху, Слева, Справа }

    public partial class ScriptMessageComponent : ZoneComponent, PrimaryPackage.Inspect.ITypewriterConfig {
        [Editable, Serialize("Введите текст скрипта", IsPropertySaveable.Yes, description: "Текст сообщения. Поддерживает \\n и ключи локализации.", alwaysUseInstanceValues: true)]
        public string Text { get; set; }

        [Editable, Serialize("255,255,255,255", IsPropertySaveable.Yes, description: "Цвет текста.", alwaysUseInstanceValues: true)]
        public Color TextColor { get; set; }

        [Editable(MinValueFloat = 0.5f, MaxValueFloat = 3.0f, DecimalCount = 2), Serialize(1.0f, IsPropertySaveable.Yes, description: "Масштаб текста.", alwaysUseInstanceValues: true)]
        public float TextScale { get; set; }

        [Editable, Serialize("style:Font", IsPropertySaveable.Yes, description: "Шрифт текста: style:<системный> или file:<файл из Fonts/>.", alwaysUseInstanceValues: true)]
        public string FontName { get; set; }

        [Editable(MinValueFloat = 10.0f, MaxValueFloat = 80.0f, DecimalCount = 0), Serialize(35.0f, IsPropertySaveable.Yes, description: "Максимальная ширина текста до переноса, % ширины экрана.", alwaysUseInstanceValues: true)]
        public float MaxWidthPercent { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 400.0f, DecimalCount = 0), Serialize(25.0f, IsPropertySaveable.Yes, description: "Отступ текста вверх от панели инвентаря, пикс.", alwaysUseInstanceValues: true)]
        public float VerticalOffset { get; set; }

        [Editable, Serialize(ZoneEasing.Торможение, IsPropertySaveable.Yes, description: "Кривая плавности анимаций появления.", alwaysUseInstanceValues: true)]
        public ZoneEasing Easing { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2), Serialize(0.0f, IsPropertySaveable.Yes, description: "Пауза перед началом появления, сек.", alwaysUseInstanceValues: true)]
        public float StartDelay { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2), Serialize(0.5f, IsPropertySaveable.Yes, description: "Длительность проявления (альфа 0→1), сек.", alwaysUseInstanceValues: true)]
        public float FadeInTime { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 60.0f, DecimalCount = 1), Serialize(4.0f, IsPropertySaveable.Yes, description: "Время показа текста, сек.", alwaysUseInstanceValues: true)]
        public float HoldTime { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2), Serialize(0.8f, IsPropertySaveable.Yes, description: "Длительность исчезания, сек.", alwaysUseInstanceValues: true)]
        public float FadeOutTime { get; set; }

        [Editable, Serialize(SlideFrom.Нет, IsPropertySaveable.Yes, description: "Выезд текста при появлении (отдельно от проявления).", alwaysUseInstanceValues: true)]
        public SlideFrom Slide { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 400.0f, DecimalCount = 0), Serialize(30.0f, IsPropertySaveable.Yes, description: "Дистанция выезда, пикс.", alwaysUseInstanceValues: true)]
        public float SlideOffset { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Рост текста из 80% до 100% при появлении.", alwaysUseInstanceValues: true)]
        public bool ScaleIn { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Печать текста по буквам.", alwaysUseInstanceValues: true)]
        public bool Typewriter { get; set; }

        [Editable(MinValueFloat = 0.01f, MaxValueFloat = 0.3f, DecimalCount = 3), Serialize(0.04f, IsPropertySaveable.Yes, description: "Интервал печати одного символа, сек.", alwaysUseInstanceValues: true)]
        public float CharInterval { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.5f, IsPropertySaveable.Yes, description: "Громкость звука печати (0, без звука).", alwaysUseInstanceValues: true)]
        public float TypeSoundVolume { get; set; }

        [Editable, Serialize("%ModDir%/Content/Sounds/UI/pp_type.ogg", IsPropertySaveable.Yes, description: "Путь к звуку печати символа.", alwaysUseInstanceValues: true)]
        public string TypeSoundFile { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Тень текста.", alwaysUseInstanceValues: true)]
        public bool ShadowEnabled { get; set; }

        [Editable(MinValueFloat = -8.0f, MaxValueFloat = 8.0f, DecimalCount = 1), Serialize(2.0f, IsPropertySaveable.Yes, description: "Смещение тени по X, пикс.", alwaysUseInstanceValues: true)]
        public float ShadowOffsetX { get; set; }

        [Editable(MinValueFloat = -8.0f, MaxValueFloat = 8.0f, DecimalCount = 1), Serialize(2.0f, IsPropertySaveable.Yes, description: "Смещение тени по Y, пикс.", alwaysUseInstanceValues: true)]
        public float ShadowOffsetY { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 8.0f, DecimalCount = 1), Serialize(2.0f, IsPropertySaveable.Yes, description: "Размытие тени, пикс (0, резкая).", alwaysUseInstanceValues: true)]
        public float ShadowBlur { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.6f, IsPropertySaveable.Yes, description: "Непрозрачность тени.", alwaysUseInstanceValues: true)]
        public float ShadowAlpha { get; set; }

        [Editable, Serialize("0,0,0,255", IsPropertySaveable.Yes, description: "Цвет тени.", alwaysUseInstanceValues: true)]
        public Color ShadowColor { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Обводка букв.", alwaysUseInstanceValues: true)]
        public bool OutlineEnabled { get; set; }

        [Editable, Serialize("0,0,0,255", IsPropertySaveable.Yes, description: "Цвет обводки букв.", alwaysUseInstanceValues: true)]
        public Color OutlineColor { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Полупрозрачная подложка под текстом.", alwaysUseInstanceValues: true)]
        public bool BackgroundEnabled { get; set; }

        [Editable, Serialize("10,10,12,255", IsPropertySaveable.Yes, description: "Цвет подложки.", alwaysUseInstanceValues: true)]
        public Color BackgroundColor { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.55f, IsPropertySaveable.Yes, description: "Непрозрачность подложки.", alwaysUseInstanceValues: true)]
        public float BackgroundAlpha { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 60.0f, DecimalCount = 0), Serialize(16.0f, IsPropertySaveable.Yes, description: "Внутренние отступы подложки, пикс.", alwaysUseInstanceValues: true)]
        public float BackgroundPadding { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Сработать один раз за раунд для каждого персонажа.", alwaysUseInstanceValues: true)]
        public bool TriggerOnce { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 600.0f, DecimalCount = 1), Serialize(3.0f, IsPropertySaveable.Yes, description: "Пауза перед повторным срабатыванием для того же персонажа, сек.", alwaysUseInstanceValues: true)]
        public float Cooldown { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Срабатывать только на игроков (иначе, и на ботов).", alwaysUseInstanceValues: true)]
        public bool PlayersOnly { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.7f, IsPropertySaveable.Yes, description: "Громкость звука срабатывания (0, без звука).", alwaysUseInstanceValues: true)]
        public float SoundVolume { get; set; }

        [Editable, Serialize("%ModDir%/Content/Sounds/UI/twobeep.ogg", IsPropertySaveable.Yes, description: "Путь к звуку срабатывания (пусто, без звука).", alwaysUseInstanceValues: true)]
        public string SoundFile { get; set; }

        private readonly HashSet<Character> inside = new HashSet<Character>();
        private readonly List<Character> enterBuffer = new List<Character>();
        private readonly Dictionary<Character, double> lastTrigger = new Dictionary<Character, double>();
        private readonly HashSet<Character> fired = new HashSet<Character>();
        private readonly List<Character> pruneBuffer = new List<Character>();

        public ScriptMessageComponent(Item item, ContentXElement element) : base(item, element) {
            MigrateLegacyAnimation(element);
            FontName = PrimaryPackage.Inspect.FontMigration.Apply(element, FontName);
        }

        private void MigrateLegacyAnimation(ContentXElement element) {
            string legacy = element.GetAttributeString("animation", null);
            if (string.IsNullOrEmpty(legacy)) { return; }
            switch (legacy.ToLowerInvariant()) {
                case "снизу": Slide = SlideFrom.Снизу; break;
                case "сверху": Slide = SlideFrom.Сверху; break;
                case "увеличение": ScaleIn = true; break;
                case "печать": Typewriter = true; break;
            }
        }

        partial void TriggerProjSpecific(Character character);

        protected override void OnOccupantsRefreshed() {
            enterBuffer.Clear();
            foreach (Character c in occupants) {
                if (!inside.Contains(c)) { enterBuffer.Add(c); }
            }
            inside.Clear();
            foreach (Character c in occupants) { inside.Add(c); }
            PruneStale();
            foreach (Character c in enterBuffer) { TryTrigger(c); }
        }

        private void PruneStale() {
            if (lastTrigger.Count > 0) {
                pruneBuffer.Clear();
                foreach (var kv in lastTrigger) {
                    if (kv.Key.Removed) { pruneBuffer.Add(kv.Key); }
                }
                foreach (Character c in pruneBuffer) { lastTrigger.Remove(c); }
            }
            if (fired.Count > 0) { fired.RemoveWhere(static c => c.Removed); }
        }

        private void TryTrigger(Character c) {
            if (PlayersOnly ? !c.IsPlayer : !(c.IsPlayer || c.IsBot)) { return; }
            if (fired.Contains(c)) { return; }
            double now = Timing.TotalTime;
            if (lastTrigger.TryGetValue(c, out double last) && now - last < Cooldown) { return; }
            lastTrigger[c] = now;
            if (TriggerOnce) { fired.Add(c); }
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient) {
                item.SendSignal("1", "signal_out");
            }
            TriggerProjSpecific(c);
        }

        public string ResolveText() => PrimaryPackage.Inspect.TypewriterText.Resolve(Text);
    }
}
