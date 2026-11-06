using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
namespace PrimaryPackage.Economy {
    public sealed class CheckPrintInteractionClientPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.TryInteract)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CheckPrintInteractionClientPlugin), nameof(TryInteractPrefix))));
        }
        public void Release() {
        }
        public static bool TryInteractPrefix(Item __instance, ref bool __result, Character user) {
            if (user == null || user != Character.Controlled) { return true; }
            Item heldCheck = user.HeldItems.FirstOrDefault(CheckComponent.IsCheckItem);
            if (!CheckComponent.IsOrderCrateItem(__instance)) {
                if (heldCheck != null && user.IsKeyHit(InputType.Select)
                    && __instance.GetComponent<ItemContainer>() != null) {
                    HintToast.ShowSeverity(OrderText.Get("pp.order.checkprint.wrong_target", "Чек-наряд можно привязать только к ящику наряда."),
                        HintToastNet.SeverityError);
                }
                return true;
            }
            if (heldCheck == null) {
                if (user.IsKeyHit(InputType.Select)
                    && user.Inventory != null && user.Inventory.AllItems.Any(CheckComponent.IsCheckItem)) {
                    HintToast.ShowSeverity(OrderText.Get("pp.order.checkprint.hold_check_hint", "Возьмите чек в руки, чтобы нанести его на ящик, или просто положите чек внутрь ящика."),
                        HintToastNet.SeverityInfo);
                }
                return true;
            }
            if (!user.IsKeyHit(InputType.Select)) { return true; }
            if (GameMain.Client != null) {
                ushort checkId = heldCheck.ID;
                ushort targetId = __instance.ID;
                INet.Net.Send(CheckComponent.PrintNetId, msg => {
                    msg.WriteUInt16(checkId);
                    msg.WriteUInt16(targetId);
                });
            }
            else {
                HintToast.Show(OrderText.Get("pp.order.checkprint.mp_only", "Система заказов доступна только в сетевой игре."));
            }
            __result = true;
            return false;
        }
    }
}
