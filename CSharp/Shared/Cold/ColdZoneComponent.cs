using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Cold {
    public partial class ColdZoneComponent : ZoneComponent, IColdArea, IServerSerializable {
        private static readonly List<ColdZoneComponent> instances = new List<ColdZoneComponent>();

        [Editable(MinValueFloat = -45.0f, MaxValueFloat = 60.0f, DecimalCount = 1), Serialize(-12.0f, IsPropertySaveable.Yes, description: "Температура, к которой зона тянет накрытые отсеки.", alwaysUseInstanceValues: true)]
        public float TargetTemperature { get; set; }

        [Editable(MinValueFloat = 0.01f, MaxValueFloat = 5.0f, DecimalCount = 2), Serialize(0.35f, IsPropertySaveable.Yes, description: "Скорость выравнивания, доля разницы в секунду.", alwaysUseInstanceValues: true)]
        public float Rate { get; set; }

        public ColdZoneComponent(Item item, ContentXElement element) : base(item, element) {
            ColdConfig.EnsureLoaded();
        }

        public override void OnItemLoaded() {
            base.OnItemLoaded();
            if (!instances.Contains(this)) { instances.Add(this); }
            ColdField.Register(this);
        }

        public override void RemoveComponentSpecific() {
            instances.Remove(this);
            ColdField.Unregister(this);
            base.RemoveComponentSpecific();
        }

        public void ApplyColdArea(float deltaTime) {
            for (int i = 0; i < hulls.Count; i++) {
                ColdField.Nudge(hulls[i], TargetTemperature, Rate, deltaTime);
            }
        }

        public static bool TryGetOpenTemperature(Character character, out float temperature) {
            temperature = 0.0f;
            bool found = false;
            for (int i = 0; i < instances.Count; i++) {
                ColdZoneComponent zone = instances[i];
                if (zone.item == null || zone.item.Removed) { continue; }
                if (!Submarine.RectContains(zone.item.WorldRect, character.WorldPosition)) { continue; }
                if (!found || zone.TargetTemperature < temperature) { temperature = zone.TargetTemperature; }
                found = true;
            }
            return found;
        }

        protected override void UpdateZone(float deltaTime) => UpdateProjSpecific(deltaTime);

        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteSingle(TargetTemperature);
            msg.WriteSingle(Rate);
            msg.WritePadBits();
        }

        public void ClientEventRead(IReadMessage msg, float sendingTime) {
            TargetTemperature = msg.ReadSingle();
            Rate = msg.ReadSingle();
            msg.ReadPadBits();
        }

        partial void UpdateProjSpecific(float deltaTime);
    }
}
