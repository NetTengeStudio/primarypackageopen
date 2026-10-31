using Barotrauma;
using Barotrauma.Items.Components;
namespace PrimaryPackage.Economy {
    internal static partial class EconomyLogic {
        public const string CaptainJob = "captain";
        public static bool HasCaptainAccess(Character requester, out string error) {
            error = null;
            if (requester?.Info == null) { error = OrderText.Get("pp.order.error.no_requester", "Нет запросителя"); return false; }
            if (CheckServerAdminPermission(requester)) { return true; }
            var idCard = requester.Inventory?.GetItemInLimbSlot(InvSlotType.Card)?.GetComponent<IdCard>();
            if (idCard == null) { error = OrderText.Get("pp.order.error.wear_id_card", "Наденьте ID карту"); return false; }
            if (idCard.OwnerJobId != CaptainJob && !idCard.Item.HasTag("id_captain")) { error = OrderText.Get("pp.order.error.not_captain_card", "Карта не капитанская"); return false; }
            return true;
        }
        static partial void CheckServerAdminPermissionProjSpecific(Character requester, ref bool isAdmin);
        private static bool CheckServerAdminPermission(Character requester) {
            bool isAdmin = false;
            CheckServerAdminPermissionProjSpecific(requester, ref isAdmin);
            return isAdmin;
        }
    }
}
