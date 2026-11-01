using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
namespace PrimaryPackage.Economy {
    public sealed class CheckPrintInteractionServerPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.ModNet.Receive(CheckComponent.PrintNetId, OnPrintRequest, 0.5);
        }
        public void Release() {}
        private static void OnPrintRequest(IReadMessage msg, Client sender) {
            ushort checkId = msg.ReadUInt16();
            ushort crateId = msg.ReadUInt16();
            Character character = sender?.Character;
            if (character == null || character.Removed) { return; }
            if (Entity.FindEntityByID(checkId) is not Item checkItem
                || checkItem.GetComponent<CheckComponent>() is not {} check) { return; }
            if (Entity.FindEntityByID(crateId) is not Item crate
                || crate.GetComponent<OrderClaimComponent>() is not {} crateClaim) { return; }
            bool holdsCheck = character.Inventory != null && character.Inventory.AllItems.Contains(checkItem);
            if (!holdsCheck) {
                HintSender.Error(sender, OrderText.Get("pp.order.check.must_hold", "Чек должен быть у вас в руках."));
                return;
            }
            if (!character.CanInteractWith(crate)) {
                HintSender.Error(sender, OrderText.Get("pp.order.check.too_far", "Слишком далеко от ящика."));
                return;
            }
            if (!crateClaim.TryPrint(check, out OrderSet set, out string error)) {
                if (error != null) {
                    HintSender.Error(sender, error);
                }
                return;
            }
            HintSender.Success(sender, OrderText.Format("pp.order.check.attached",
                $"Наряд №{crateClaim.OrderNumber} «{set.DisplayName}» привязан к ящику.",
                ("[num]", crateClaim.OrderNumber.ToString()),
                ("[name]", set.DisplayName)));
        }
    }
}
