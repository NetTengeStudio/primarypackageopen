using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Zones {
    public partial class FogZoneComponent : ZoneComponent {
        [Editable(MinValueFloat = 0.05f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.4f, IsPropertySaveable.Yes, description: "Плотность тумана (количество облаков на площадь зоны).", alwaysUseInstanceValues: true)]
        public float Density { get; set; }

        [Editable, Serialize("170,175,180,255", IsPropertySaveable.Yes, description: "Цвет тумана.", alwaysUseInstanceValues: true)]
        public Color FogColor { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.65f, IsPropertySaveable.Yes, description: "Непрозрачность тумана.", alwaysUseInstanceValues: true)]
        public float FogAlpha { get; set; }

        [Editable(MinValueFloat = 0.3f, MaxValueFloat = 2.5f, DecimalCount = 2), Serialize(1.0f, IsPropertySaveable.Yes, description: "Масштаб облаков тумана.", alwaysUseInstanceValues: true)]
        public float CloudScale { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 40.0f, DecimalCount = 1), Serialize(8.0f, IsPropertySaveable.Yes, description: "Скорость дрейфа облаков, пикс/сек.", alwaysUseInstanceValues: true)]
        public float DriftSpeed { get; set; }

        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Вечные облака: постоянное число облаков без появления и исчезновения, бесконечный дрейф с обёрткой по краям зоны.", alwaysUseInstanceValues: true)]
        public bool PersistentClouds { get; set; }

        public FogZoneComponent(Item item, ContentXElement element) : base(item, element) {
            InitProjSpecific(element);
        }

        partial void InitProjSpecific(ContentXElement element);
        partial void UpdateProjSpecific(float deltaTime);

        protected override void UpdateZone(float deltaTime) => UpdateProjSpecific(deltaTime);
    }
}
