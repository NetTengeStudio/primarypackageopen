using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Economy;
namespace PrimaryPackage.Documents {
    public sealed class StampInteractionServerPlugin : PrimaryPackage.Core.IModule {
        private static bool subsystemEnabled;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            subsystemEnabled = StampDefinitions.DetectSubsystemEnabled();
            if (!subsystemEnabled) { return; }
            harmony.Patch(AccessTools.Method(typeof(Holdable), nameof(Holdable.Use)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StampDefinitions), nameof(StampDefinitions.HoldableUsePrefix))));
            INet.Net.Receive(StampDefinitions.ApplyNetId, OnStampRequest, 0.25);
        }
        public void Release() {
        }
        private static void OnStampRequest(IReadMessage msg, Client sender) {
            ushort documentId = msg.ReadUInt16();
            msg.ReadIdentifier();
            Vector2 offset = StampPlacement.ClampOffset(new Vector2(msg.ReadSingle(), msg.ReadSingle()));
            float rot = MathHelper.Clamp(msg.ReadSingle(), -0.5f, 0.5f);
            if (Entity.FindEntityByID(documentId) is not Item document) { return; }
            DocumentMarkComponent mark = document.GetComponent<DocumentMarkComponent>();
            if (mark == null || mark.GetStamps().Count >= 5) { return; }
            Character character = sender?.Character;
            if (character == null || character.Removed) { return; }
            bool holdsDocument = character.Inventory != null && character.Inventory.Contains(document);
            if (!character.CanInteractWith(document) && !holdsDocument) { return; }
            Item heldStamp = character.HeldItems.FirstOrDefault(it => StampDefinitions.IsStampItem(it));
            if (heldStamp == null) { return; }
            Identifier stampId = StampDefinitions.GetStampIdFromItem(heldStamp);
            if (mark.GetStamps().Any(s => s.StampId == stampId)) {
                HintSender.Error(sender, OrderText.Get("pp.order.stamp.already_applied", "Этот штамп уже стоит на документе."));
                return;
            }
            mark.ApplyStamp(stampId, offset, rot);
            heldStamp.Use(0.1f, character);
        }
    }
}
