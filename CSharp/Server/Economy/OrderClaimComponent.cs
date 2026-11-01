using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderClaimComponent {
        private const double AutoPrintInterval = 0.5;
        private const double AutoHintRepeatSeconds = 5.0;
        private const float HintRange = 500f;
        private double nextAutoCheck;
        private double lastAutoHintTime;
        private string lastAutoHint;
        public override void Update(float deltaTime, Camera cam) {
            double now = Timing.TotalTime;
            if (now < nextAutoCheck) { return; }
            nextAutoCheck = now + AutoPrintInterval;
            Inventory inv = item.OwnInventory;
            if (inv == null) { return; }
            CheckComponent firstNonClaim = null;
            foreach (Item contained in inv.AllItems) {
                CheckComponent check = contained?.GetComponent<CheckComponent>();
                if (check == null) { continue; }
                if (!check.IsClaim) { firstNonClaim ??= check; continue; }
                if (TryPrint(check, out OrderSet set, out string error)) {
                    HintSender.SendNear(item.WorldPosition, HintRange,
                        OrderText.Format("pp.order.check.attached",
                            $"Наряд №{OrderNumber} «{set.DisplayName}» привязан к ящику.",
                            ("[num]", OrderNumber.ToString()),
                            ("[name]", set.DisplayName)),
                        HintToastNet.SeveritySuccess);
                }
                else if (error != null) {
                    AutoHint(now, error);
                }
                return;
            }
            if (firstNonClaim != null) {
                TryPrint(firstNonClaim, out _, out string wrongKindError);
                if (wrongKindError != null) { AutoHint(now, wrongKindError); }
            }
        }
        public bool TryPrint(CheckComponent check, out OrderSet set, out string error) {
            set = null;
            error = null;
            if (check == null || check.Item.Removed || check.Item.IsInRemoveQueue) { return false; }
            if (!check.IsClaim || check.ClaimId <= 0) {
                error = check.IsReceipt
                    ? OrderText.Format("pp.order.claim.error.wrong_check",
                        $"Это денежная квитанция на {check.Amount:N0} кр. а не чек наряд на ящик её нанести нельзя. Обналичьте её в обменнике.",
                        ("[amount]", check.Amount.ToString("N0")))
                    : OrderText.Get("pp.order.claim.error.not_claim_check", "Это не чек наряд распечатать на ящик нечего.");
                return false;
            }
            if (IsMarked) {
                if (IsDelivery) {
                    error = OrderText.Format("pp.order.claim.error.already_attached",
                        $"К этому ящику уже привязан наряд №{OrderNumber}.",
                        ("[num]", OrderNumber.ToString()));
                    return false;
                }
                Unmark();
            }
            if (!OrderRegistry.TryPeekDeliveryClaim(check.ClaimId, out OrderRegistry.DeliveryClaim claim)
                || claim.OrderSetId != (check.OrderSetId ?? "").ToIdentifier()) {
                error = OrderText.Format("pp.order.claim.error.not_found",
                    $"Наряд №{check.OrderNumber} не найден или уже выполнен.",
                    ("[num]", check.OrderNumber.ToString()));
                return false;
            }
            if (!OrderSets.TryGet(claim.OrderSetId, out set)) {
                error = OrderText.Get("pp.order.claim.error.set_missing", "Комплект этого наряда больше не существует.");
                return false;
            }
            OrderSetId = claim.OrderSetId.Value;
            ClaimId = claim.Id;
            OrderNumber = claim.OrderNumber;
            ClaimDirection = DirDelivery;
            claim.State = OrderState.InProgress;
            PushSync();
            Entity.Spawner?.AddEntityToRemoveQueue(check.Item);
            return true;
        }
        public void Unmark() {
            if (!IsMarked) { return; }
            OrderSetId = "";
            ClaimId = 0;
            OrderNumber = 0;
            ClaimDirection = DirDelivery;
            PushSync();
        }
        public static void UnmarkByClaimId(int claimId) {
            foreach (Item candidate in Item.ItemList) {
                OrderClaimComponent oc = candidate?.GetComponent<OrderClaimComponent>();
                if (oc != null && oc.IsDelivery && oc.ClaimId == claimId) { oc.Unmark(); }
            }
        }
        public static void UnmarkAll() {
            foreach (Item candidate in Item.ItemList) {
                OrderClaimComponent oc = candidate?.GetComponent<OrderClaimComponent>();
                if (oc != null && oc.IsMarked) { oc.Unmark(); }
            }
        }
        private void AutoHint(double now, string text) {
            if (text == lastAutoHint && now < lastAutoHintTime + AutoHintRepeatSeconds) { return; }
            lastAutoHint = text;
            lastAutoHintTime = now;
            HintSender.SendNear(item.WorldPosition, HintRange, text, HintToastNet.SeverityError);
        }
        public void PushSync() => item.CreateServerEvent(this, new SyncEventData());
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteString(OrderSetId ?? "");
            msg.WriteInt32(ClaimId);
            msg.WriteString(ClaimDirection ?? DirDelivery);
            msg.WriteInt32(OrderNumber);
        }
    }
}
