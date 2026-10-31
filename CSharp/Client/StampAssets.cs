using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Documents {
    public static class StampAssets {
        private static readonly Dictionary<Identifier, string> StampTextureFiles = new Dictionary<Identifier, string> {
            { StampDefinitions.CoalitionStamp, "Content/Items/Stamp/paper_stamp_cap.png" },
            { StampDefinitions.ApprovedStamp, "Content/Items/Stamp/paper_stamp_ok.png" },
            { StampDefinitions.DeniedStamp, "Content/Items/Stamp/paper_stamp_deny.png" },
            { StampDefinitions.EngineerStamp, "Content/Items/Stamp/paper_stamp_engineer.png" },
            { StampDefinitions.MedicalDoctorStamp, "Content/Items/Stamp/paper_stamp_medicaldoctor.png" },
            { StampDefinitions.MechanicStamp, "Content/Items/Stamp/paper_stamp_mechanic.png" },
            { StampDefinitions.SecurityOfficerStamp, "Content/Items/Stamp/paper_stamp_securityofficer.png" }
        };
        public static Sprite GetStampSprite(Item item, Identifier stampId) {
            if (stampId.IsEmpty) { return null; }
            if (!StampTextureFiles.TryGetValue(stampId, out string relativePath)) { return null; }
            return PrimaryPackage.Core.TextureStore.Art(relativePath, new Vector2(0.5f, 0.5f));
        }
    }
}
