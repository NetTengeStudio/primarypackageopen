using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Documents {
    public partial class DocumentMarkComponent : ItemComponent, IServerSerializable {
        public readonly struct StampEntry {
            public readonly Identifier StampId;
            public readonly Vector2 Offset;
            public readonly float Rotation;
            public StampEntry(Identifier stampId, Vector2 offset, float rotation) {
                StampId = stampId;
                Offset = offset;
                Rotation = rotation;
            }
        }
        [Editable, Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string StampsData { get; set; } = "";
        public bool IsStamped => !string.IsNullOrEmpty(StampsData);
        public DocumentMarkComponent(Item item, ContentXElement element) : base(item, element) {
            IsActive = true;
        }
        private static bool ParseFloat(string s, out float v) => float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);
        public List<StampEntry> GetStamps() {
            var list = new List<StampEntry>();
            if (string.IsNullOrEmpty(StampsData)) { return list; }
            foreach (string part in StampsData.Split(';')) {
                if (string.IsNullOrEmpty(part)) { continue; }
                string[] subParts = part.Split(',');
                if (subParts.Length < 4) { continue; }
                if (ParseFloat(subParts[1], out float ox) && ParseFloat(subParts[2], out float oy) && ParseFloat(subParts[3], out float rot)) {
                    list.Add(new StampEntry(subParts[0].ToIdentifier(), new Vector2(ox, oy), rot));
                }
            }
            return list;
        }
        public void ApplyStamp(Identifier stampId, Vector2 offset, float rotation) {
            List<StampEntry> stamps = GetStamps();
            if (stamps.Count >= 5 || stampId.IsEmpty || stamps.Any(s => s.StampId == stampId)) { return; }
            string newEntry = FormattableString.Invariant($"{stampId},{offset.X},{offset.Y},{rotation}");
            StampsData = string.IsNullOrEmpty(StampsData) ? newEntry : StampsData + ";" + newEntry;
#if SERVER
            item.CreateServerEvent(this);
#endif
        }
    }
}
