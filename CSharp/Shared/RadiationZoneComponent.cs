using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Zones {
    public partial class RadiationZoneComponent : ZoneComponent {
        private const float DamageInterval = 1.0f;

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f, DecimalCount = 2), Serialize(3.0f, IsPropertySaveable.Yes, description: "Радиация в секунду персонажам внутри зоны.", alwaysUseInstanceValues: true)]
        public float RadiationPerSecond { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 120.0f, DecimalCount = 1), Serialize(0.0f, IsPropertySaveable.Yes, description: "Секунды внутри зоны до начала облучения.", alwaysUseInstanceValues: true)]
        public float GracePeriod { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 300.0f, DecimalCount = 1), Serialize(30.0f, IsPropertySaveable.Yes, description: "Частиц в секунду на всю зону.", alwaysUseInstanceValues: true)]
        public float ParticlesPerSecond { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.35f, IsPropertySaveable.Yes, description: "Прозрачность частиц.", alwaysUseInstanceValues: true)]
        public float ParticleAlpha { get; set; }

        [Editable, Serialize("210,170,90,255", IsPropertySaveable.Yes, description: "Цвет частиц.", alwaysUseInstanceValues: true)]
        public Color ParticleColor { get; set; }

        [Editable(MinValueFloat = 0.05f, MaxValueFloat = 5.0f, DecimalCount = 2), Serialize(0.8f, IsPropertySaveable.Yes, description: "Минимальный масштаб частиц.", alwaysUseInstanceValues: true)]
        public float ParticleScaleMin { get; set; }

        [Editable(MinValueFloat = 0.05f, MaxValueFloat = 5.0f, DecimalCount = 2), Serialize(1.2f, IsPropertySaveable.Yes, description: "Максимальный масштаб частиц.", alwaysUseInstanceValues: true)]
        public float ParticleScaleMax { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 200.0f, DecimalCount = 1), Serialize(10.0f, IsPropertySaveable.Yes, description: "Максимальная горизонтальная скорость частиц, пикс/сек.", alwaysUseInstanceValues: true)]
        public float ParticleDrift { get; set; }

        [Editable(MinValueFloat = -200.0f, MaxValueFloat = 200.0f, DecimalCount = 1), Serialize(-5.0f, IsPropertySaveable.Yes, description: "Минимальная вертикальная скорость частиц, пикс/сек (плюс, вверх).", alwaysUseInstanceValues: true)]
        public float ParticleRiseMin { get; set; }

        [Editable(MinValueFloat = -200.0f, MaxValueFloat = 200.0f, DecimalCount = 1), Serialize(5.0f, IsPropertySaveable.Yes, description: "Максимальная вертикальная скорость частиц, пикс/сек (плюс, вверх).", alwaysUseInstanceValues: true)]
        public float ParticleRiseMax { get; set; }

        [Editable, Serialize("", IsPropertySaveable.Yes, description: "Имя префаба частиц (пусто, стандартный pp_radiationdust).", alwaysUseInstanceValues: true)]
        public string ParticlePrefabOverride { get; set; }

        [Editable(MinValueFloat = 0.0f, MaxValueFloat = 1.0f, DecimalCount = 2), Serialize(0.8f, IsPropertySaveable.Yes, description: "Громкость счётчика Гейгера (0, тишина).", alwaysUseInstanceValues: true)]
        public float GeigerVolume { get; set; }

        private static readonly Affliction[] afflictionBuffer = new Affliction[1];

        private readonly Dictionary<Character, float> insideTime = new Dictionary<Character, float>();
        private readonly List<Character> staleBuffer = new List<Character>();
        private float damageTimer;

        public RadiationZoneComponent(Item item, ContentXElement element) : base(item, element) {
            InitProjSpecific(element);
        }

        partial void InitProjSpecific(ContentXElement element);
        partial void UpdateProjSpecific(float deltaTime);

        protected override void OnOccupantsRefreshed() {
            if (insideTime.Count > 0) {
                staleBuffer.Clear();
                foreach (var kv in insideTime) {
                    if (!occupants.Contains(kv.Key)) { staleBuffer.Add(kv.Key); }
                }
                foreach (Character c in staleBuffer) { insideTime.Remove(c); }
            }
            foreach (Character c in occupants) {
                insideTime[c] = insideTime.TryGetValue(c, out float t) ? t + DetectInterval : 0.0f;
            }
        }

        protected override void UpdateZone(float deltaTime) {
            if (occupants.Count > 0 && RadiationPerSecond > 0.0f &&
                (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)) {
                damageTimer += deltaTime;
                if (damageTimer >= DamageInterval) {
                    ApplyRadiation(damageTimer);
                    damageTimer = 0.0f;
                }
            } else {
                damageTimer = 0.0f;
            }
            UpdateProjSpecific(deltaTime);
        }

        private void ApplyRadiation(float elapsed) {
            foreach (Character c in occupants) {
                if (c.Removed || c.IsDead) { continue; }
                if (insideTime.TryGetValue(c, out float t) && t < GracePeriod) { continue; }
                Limb limb = PickLimb(c);
                if (limb == null) { continue; }
                afflictionBuffer[0] = AfflictionPrefab.RadiationSickness.Instantiate(RadiationPerSecond * elapsed);
                c.LastDamageSource = item;
                c.DamageLimb(limb.WorldPosition, limb, afflictionBuffer, stun: 0.0f, playSound: false, attackImpulse: Vector2.Zero);
            }
        }

        private static Limb PickLimb(Character c) {
            var limbs = c.AnimController?.Limbs;
            if (limbs == null || limbs.Length == 0) { return null; }
            Limb limb = limbs[Rand.Int(limbs.Length)];
            return limb.Removed ? c.AnimController.MainLimb : limb;
        }
    }
}
