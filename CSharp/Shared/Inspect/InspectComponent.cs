using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public enum InspectMode { Scripted, SelfInspect }

    public partial class InspectComponent : ItemComponent, IInspectSettings {
        private InspectMode mode;

        [Editable, Serialize(InspectMode.Scripted, IsPropertySaveable.Yes, description: "Режим осмотра: скриптовой (текст внизу экрана) или SI (текст на предмете).", alwaysUseInstanceValues: true)]
        public InspectMode Mode {
            get => mode;
            set {
                if (mode == value) { return; }
                mode = value;
                ModeChangedProjSpecific();
            }
        }

        partial void ModeChangedProjSpecific();

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Показывать подсказку осмотра при наведении.", alwaysUseInstanceValues: true)]
        public bool PromptEnabled { get; set; }

        [Editable, Serialize("pp_inspect.prompt", IsPropertySaveable.Yes, description: "Текст подсказки. Поддерживает ключи локализации и [InputType.Use].", alwaysUseInstanceValues: true)]
        public string PromptText { get; set; }

        [Editable, Serialize("120,200,255,255", IsPropertySaveable.Yes, description: "Цвет подсказки.", alwaysUseInstanceValues: true)]
        public Color PromptColor { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1000.0f, DecimalCount = 0), Serialize(180.0f, IsPropertySaveable.Yes, description: "Длительность появления подсказки, мс.", alwaysUseInstanceValues: true)]
        public float PromptFadeIn { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1000.0f, DecimalCount = 0), Serialize(140.0f, IsPropertySaveable.Yes, description: "Длительность исчезания подсказки, мс.", alwaysUseInstanceValues: true)]
        public float PromptFadeOut { get; set; }

        [Editable, Serialize(ZoneEasing.Торможение, IsPropertySaveable.Yes, description: "Кривая появления подсказки.", alwaysUseInstanceValues: true)]
        public ZoneEasing PromptEasingIn { get; set; }

        [Editable, Serialize(ZoneEasing.Разгон, IsPropertySaveable.Yes, description: "Кривая исчезания подсказки.", alwaysUseInstanceValues: true)]
        public ZoneEasing PromptEasingOut { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 64.0f, DecimalCount = 0), Serialize(10.0f, IsPropertySaveable.Yes, description: "Всплытие подсказки при появлении, пикс.", alwaysUseInstanceValues: true)]
        public float PromptRise { get; set; }

        [Editable(MinValueFloat = -256.0f, MaxValueFloat = 256.0f, DecimalCount = 0), Serialize(0.0f, IsPropertySaveable.Yes, description: "Дополнительный сдвиг подсказки по вертикали, пикс.", alwaysUseInstanceValues: true)]
        public float PromptOffsetY { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Рост подсказки из 94% до 100% при появлении.", alwaysUseInstanceValues: true)]
        public bool PromptScaleIn { get; set; }

        [Editable(MinValueFloat = 50.0f, MaxValueFloat = 1000.0f, DecimalCount = 0), Serialize(220.0f, IsPropertySaveable.Yes, description: "Дистанция, на которой предмет откликается на осмотр.", alwaysUseInstanceValues: true)]
        public float PromptRange { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 5.0f, DecimalCount = 2), Serialize(0.6f, IsPropertySaveable.Yes, description: "Длительность удержания клавиши осмотра, сек (0 — запуск по нажатию).", alwaysUseInstanceValues: true)]
        public float HoldSeconds { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Предлагать осмотр только до первого прочтения текста.", alwaysUseInstanceValues: true)]
        public bool InspectOnce { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 3), Serialize(0.12f, IsPropertySaveable.Yes, description: "Порог освещённости: ниже — вместо текста показывается сообщение о темноте (0 — правило выключено).", alwaysUseInstanceValues: true)]
        public float DarkThreshold { get; set; }

        [Editable, Serialize("pp_inspect.dark", IsPropertySaveable.Yes, description: "Текст при недостаточном освещении. Поддерживает ключи локализации.", alwaysUseInstanceValues: true)]
        public string DarkText { get; set; }

        [Editable, Serialize("Введите текст осмотра", IsPropertySaveable.Yes, description: "Текст осмотра. Поддерживает \\n и ключи локализации.", alwaysUseInstanceValues: true)]
        public string Text { get; set; }

        [Editable, Serialize("255,255,255,255", IsPropertySaveable.Yes, description: "Цвет текста.", alwaysUseInstanceValues: true)]
        public Color TextColor { get; set; }

        [Editable(MinValueFloat = 0.5f, MaxValueFloat = 3.0f, DecimalCount = 2), Serialize(1.0f, IsPropertySaveable.Yes, description: "Масштаб текста.", alwaysUseInstanceValues: true)]
        public float TextScale { get; set; }

        [Editable, Serialize("style:Font", IsPropertySaveable.Yes, description: "Шрифт текста: style:<системный> или file:<файл из Fonts/>.", alwaysUseInstanceValues: true)]
        public string FontName { get; set; }

        [Editable(MinValueFloat = 10.0f, MaxValueFloat = 80.0f, DecimalCount = 0), Serialize(35.0f, IsPropertySaveable.Yes, description: "Скриптовой режим: максимальная ширина текста до переноса, % ширины экрана.", alwaysUseInstanceValues: true)]
        public float MaxWidthPercent { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 400.0f, DecimalCount = 0), Serialize(25.0f, IsPropertySaveable.Yes, description: "Скриптовой режим: отступ текста вверх от панели инвентаря, пикс.", alwaysUseInstanceValues: true)]
        public float VerticalOffset { get; set; }

        [Editable, Serialize(ZoneEasing.Торможение, IsPropertySaveable.Yes, description: "Кривая плавности появления текста.", alwaysUseInstanceValues: true)]
        public ZoneEasing Easing { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2), Serialize(0.0f, IsPropertySaveable.Yes, description: "Пауза перед началом появления, сек.", alwaysUseInstanceValues: true)]
        public float StartDelay { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2), Serialize(0.3f, IsPropertySaveable.Yes, description: "Длительность проявления (альфа 0→1), сек.", alwaysUseInstanceValues: true)]
        public float FadeInTime { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 60.0f, DecimalCount = 1), Serialize(4.0f, IsPropertySaveable.Yes, description: "Время показа текста после печати, сек.", alwaysUseInstanceValues: true)]
        public float HoldTime { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10.0f, DecimalCount = 2), Serialize(0.6f, IsPropertySaveable.Yes, description: "Длительность исчезания, сек.", alwaysUseInstanceValues: true)]
        public float FadeOutTime { get; set; }

        [Editable, Serialize(SlideFrom.Снизу, IsPropertySaveable.Yes, description: "Выезд текста при появлении.", alwaysUseInstanceValues: true)]
        public SlideFrom Slide { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 400.0f, DecimalCount = 0), Serialize(20.0f, IsPropertySaveable.Yes, description: "Дистанция выезда, пикс.", alwaysUseInstanceValues: true)]
        public float SlideOffset { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Рост текста из 80% до 100% при появлении.", alwaysUseInstanceValues: true)]
        public bool ScaleIn { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Печать текста по буквам.", alwaysUseInstanceValues: true)]
        public bool Typewriter { get; set; }

        [Editable(MinValueFloat = 0.01f, MaxValueFloat = 0.3f, DecimalCount = 3), Serialize(0.03f, IsPropertySaveable.Yes, description: "Интервал печати одного символа, сек.", alwaysUseInstanceValues: true)]
        public float CharInterval { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Повторное нажатие мгновенно допечатывает текст.", alwaysUseInstanceValues: true)]
        public bool AllowSkip { get; set; }

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

        [Editable(MinValueFloat = 64.0f, MaxValueFloat = 2000.0f, DecimalCount = 0), Serialize(400.0f, IsPropertySaveable.Yes, description: "SI: максимальная ширина текста до переноса, мировых единиц.", alwaysUseInstanceValues: true)]
        public float WorldMaxWidth { get; set; }

        [Editable(MinValueFloat = -1000.0f, MaxValueFloat = 1000.0f, DecimalCount = 0), Serialize(0.0f, IsPropertySaveable.Yes, description: "SI: сдвиг текста по X от центра предмета, мировых единиц.", alwaysUseInstanceValues: true)]
        public float WorldOffsetX { get; set; }

        [Editable(MinValueFloat = -1000.0f, MaxValueFloat = 1000.0f, DecimalCount = 0), Serialize(60.0f, IsPropertySaveable.Yes, description: "SI: сдвиг текста вверх от центра предмета, мировых единиц.", alwaysUseInstanceValues: true)]
        public float WorldOffsetY { get; set; }

        [Editable(MinValueFloat = 0.1f, MaxValueFloat = 2.0f, DecimalCount = 2), Serialize(0.5f, IsPropertySaveable.Yes, description: "SI: базовый масштаб мирового текста.", alwaysUseInstanceValues: true)]
        public float WorldScale { get; set; }

        [Editable(MinValueFloat = 100.0f, MaxValueFloat = 4000.0f, DecimalCount = 0), Serialize(900.0f, IsPropertySaveable.Yes, description: "SI: дистанция, дальше которой текст не виден.", alwaysUseInstanceValues: true)]
        public float MaxViewDistance { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "SI: скрывать текст, если предмет не в прямой видимости.", alwaysUseInstanceValues: true)]
        public bool RequireLineOfSight { get; set; }

        [Editable(MinValueFloat = -0.5f, MaxValueFloat = 0.5f, DecimalCount = 3), Serialize(0.05f, IsPropertySaveable.Yes, description: "SI: сдвиг глубины отрисовки относительно предмета.", alwaysUseInstanceValues: true)]
        public float DepthOffset { get; set; }

        public InspectComponent(Item item, ContentXElement element) : base(item, element) {
            canBePicked = element.GetAttributeBool("canbepicked", true);
            if (element.GetAttributeString("pickkey", null) == null) { PickKey = InputType.Use; }
            FontName = FontMigration.Apply(element, FontName);
        }

        public override bool Pick(Character picker) {
            PickProjSpecific(picker);
            return true;
        }

        partial void PickProjSpecific(Character picker);
    }
}
