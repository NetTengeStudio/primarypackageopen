using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderBoardComponent {
        public void ServerEventRead(IReadMessage msg, Client c) {
            string orderSetId = msg.ReadString();
            byte direction = msg.ReadByte();
            if (!item.CanClientAccess(c)) { return; }
            HandleRequest(c, orderSetId, direction);
        }
        private void HandleRequest(Client c, string orderSetId, byte direction) {
            if (!OrderSets.TryGet(orderSetId.ToIdentifier(), out OrderSet set)) {
                HintSender.Error(c, OrderText.Get("pp.order.board.error.unknown_set", "Неизвестный комплект заказа."));
                return;
            }
            Character character = c?.Character;
            string who = character?.Info?.Name ?? c?.Name ?? OrderText.Get("pp.order.atm.unknown_who", "неизвестно");
            if (!EconomyLimits.CheckOrderCooldown(c, out string cooldownErr)) {
                HintSender.Error(c, cooldownErr);
                return;
            }
            if (direction == DirectionIncoming) {
                HandleIncoming(c, set, who);
                return;
            }
            if (character == null || character.Removed) {
                HintSender.Error(c, OrderText.Get("pp.order.board.error.character_only", "Оформить наряд может только персонаж в игре"));
                return;
            }
            if (OrderRegistry.OutstandingClaimCount(who) >= OrderRegistry.MaxOutstandingClaimsPerRequester) {
                HintSender.Error(c, OrderText.Format("pp.order.board.error.too_many_claims",
                    $"У вас уже {OrderRegistry.MaxOutstandingClaimsPerRequester} невыполненных нарядов завершите их, прежде чем брать новые",
                    ("[max]", OrderRegistry.MaxOutstandingClaimsPerRequester.ToString())));
                return;
            }
            OrderRegistry.DeliveryClaim claim = OrderRegistry.AddDeliveryClaim(set.Id, who);
            if (!CheckMint.SpawnClaimCheck(character, set, claim.Id, claim.OrderNumber, onFailure: () => {
                OrderRegistry.TryConsumeDeliveryClaim(claim.Id);
                HintSender.Error(c, OrderText.Format("pp.order.board.error.check_failed_cancelled",
                    $"Наряд №{claim.OrderNumber} не удалось выдать чек, наряд отменён",
                    ("[num]", claim.OrderNumber.ToString())));
            })) {
                OrderRegistry.TryConsumeDeliveryClaim(claim.Id);
                HintSender.Error(c, OrderText.Get("pp.order.board.error.check_unavailable", "Не удалось выдать чек обратитесь к ГМ (предмет pp_check недоступен)"));
                return;
            }
            EconomyLimits.RecordOrder(c);
            HintSender.Success(c, OrderText.Format("pp.order.board.delivery_created",
                $"Наряд №{claim.OrderNumber} «{set.DisplayName}» оформлен, чек у вас. Привяжите его к ящику для доставки",
                ("[num]", claim.OrderNumber.ToString()),
                ("[name]", set.DisplayName)));
        }
        private static void HandleIncoming(Client c, OrderSet set, string who) {
            if (!EconomyLogic.HasCaptainAccess(c?.Character, out string accessErr)) {
                HintSender.Error(c, accessErr ?? OrderText.Get("pp.order.board.error.captain_only", "Закупка на баланс станции доступна только капитану"));
                return;
            }
            if (OrderRegistry.ManualApprovalRequired) {
                int id = OrderRegistry.AddPendingIncoming(set.Id, who);
                EconomyLimits.RecordOrder(c);
                HintSender.Success(c, OrderText.Format("pp.order.board.success.pending_created",
                    $"Заявка на закупку №{id} ({set.DisplayName}) создана. Ожидает одобрения ГМ",
                    ("[num]", id.ToString()), ("[name]", set.DisplayName)));
                return;
            }
            if (StationEconomyState.Balance < set.BuyPrice) {
                HintSender.Error(c,
                    OrderText.Format("pp.order.board.error.insufficient_funds",
                        $"Недостаточно средств закупка стоит {set.BuyPrice:N0} кр., на балансе {StationEconomyState.Balance:N0} кр.",
                        ("[reward]", set.BuyPrice.ToString("N0")), ("[balance]", StationEconomyState.Balance.ToString("N0"))));
                return;
            }
            int orderId = OrderRegistry.AddPendingIncoming(set.Id, who);
            OrderRegistry.TryApprove(orderId, set.BuyPrice);
            StationEconomyState.ApplyTransaction(null, -set.BuyPrice, OrderText.Format("pp.order.board.comment.approved",
                $"Закупка №{orderId} «{set.DisplayName}» списание при одобрении",
                ("[num]", orderId.ToString()), ("[name]", set.DisplayName)));
            EconomyNetwork.BroadcastAll();
            EconomyLimits.RecordOrder(c);
            HintSender.Success(c,
                OrderText.Format("pp.order.board.success.auto_approved",
                    $"Закупка №{orderId} ({set.DisplayName}) одобрена автоматически, списано {set.BuyPrice:N0} кр. Груз прибудет в свободную стыковочную зону",
                    ("[num]", orderId.ToString()), ("[name]", set.DisplayName), ("[amount]", set.BuyPrice.ToString("N0"))));
        }
    }
}
