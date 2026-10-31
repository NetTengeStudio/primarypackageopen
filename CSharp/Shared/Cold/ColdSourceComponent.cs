using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Cold {
    public partial class ColdSourceComponent : ItemComponent, IColdSource {
        private const float ResolveInterval = 1.0f;

        [Editable(MinValueFloat = -45.0f, MaxValueFloat = 60.0f, DecimalCount = 1), Serialize(-25.0f, IsPropertySaveable.Yes, description: "Температура, к которой источник тянет привязанный отсек.", alwaysUseInstanceValues: true)]
        public float TargetTemperature { get; set; }

        [Editable(MinValueFloat = 0.005f, MaxValueFloat = 5.0f, DecimalCount = 3), Serialize(0.25f, IsPropertySaveable.Yes, description: "Сила источника: доля разницы температур в секунду.", alwaysUseInstanceValues: true)]
        public float Rate { get; set; }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Источник работает.", alwaysUseInstanceValues: true)]
        public bool Enabled { get; set; }

        [Serialize("0,0", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public Vector2 BindOffset { get; set; }

        private Hull bound;
        private float resolveTimer;

        public Hull ColdHull => Enabled ? bound : null;

        public float ColdTarget => TargetTemperature;

        public float ColdRate => Rate;

        public Vector2 BindPosition => item.WorldPosition + new Vector2(BindOffset.X, BindOffset.Y);

        public ColdSourceComponent(Item item, ContentXElement element) : base(item, element) {
            IsActive = true;
            ColdConfig.EnsureLoaded();
        }

        public override void OnItemLoaded() {
            base.OnItemLoaded();
            ColdField.Register(this);
            Resolve();
        }

        public override void OnMapLoaded() {
            base.OnMapLoaded();
            Resolve();
        }

        public override void Update(float deltaTime, Camera cam) {
            resolveTimer -= deltaTime;
            if (resolveTimer <= 0.0f) {
                resolveTimer = ResolveInterval;
                Resolve();
            }
            UpdateProjSpecific(deltaTime);
        }

        public override void RemoveComponentSpecific() {
            ColdField.Unregister(this);
            base.RemoveComponentSpecific();
        }

        public void Rebind(Hull hull) {
            BindOffset = hull == null ? Vector2.Zero : hull.WorldPosition - item.WorldPosition;
            bound = hull;
        }

        private void Resolve() {
            if (bound != null && !bound.Removed && Submarine.RectContains(bound.WorldRect, BindPosition)) { return; }
            bound = Hull.FindHull(BindPosition, item.CurrentHull) ?? item.CurrentHull;
        }

        partial void UpdateProjSpecific(float deltaTime);
    }
}
