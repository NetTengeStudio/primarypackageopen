using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class StationAtmComponent {
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            if (GameMain.Server != null) EconomyNetwork.Register(item);
        }
        public void ServerEventRead(IReadMessage msg, Client c) {
            byte kind = msg.ReadByte();
            if (kind == 0) { HandleWithdraw(c, msg); return; }
            if (kind == 2) { HandleCashIn(c, msg); return; }
            if (item.CanClientAccess(c)) PushFullSync();
        }
        private void HandleCashIn(Client c, IReadMessage msg) {
            ushort itemId = msg.ReadUInt16();
            byte mode = msg.ReadByte();
            if (!item.CanClientAccess(c)) { return; }
            Character ch = c?.Character;
            if (ch is not { Removed: false })
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.no_character", "Нет персонажа для операции.")); return; }
            if (Entity.FindEntityByID(itemId) is not Item target || target.Removed || target.IsInRemoveQueue)
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.item_not_found", "Предмет не найден.")); return; }
            bool ownedByChar = ch.Inventory != null && ch.Inventory.Contains(target);
            bool inAtmSlot = item.OwnInventory != null && item.OwnInventory.Contains(target);
            if (!ownedByChar && !inAtmSlot)
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.take_item", "Возьмите предмет в руки или положите его в слот обменника.")); return; }
            string who = ch.Info?.Name ?? c?.Name ?? OrderText.Get("pp.order.atm.unknown_who", "неизвестно");
            if (mode == CashInModeEnvelopeToWallet) {
                CashEnvelopeComponent env = target.GetComponent<CashEnvelopeComponent>();
                if (env == null || env.Amount <= 0)
                { HintSender.Error(c, OrderText.Get("pp.order.atm.error.not_envelope", "Это не конверт с наличными.")); return; }
                CampaignMode campaign = GameMain.GameSession?.Campaign;
                if (campaign == null)
                { HintSender.Error(c, OrderText.Get("pp.order.atm.error.wallet_campaign_only", "Кошелёк доступен только в режиме кампании.")); return; }
                int env_amount = env.Amount;
                campaign.GetWallet(c).Give(env_amount);
                Entity.Spawner.AddEntityToRemoveQueue(target);
                HintSender.Success(c, OrderText.Format("pp.order.atm.success.wallet_credited",
                    $"В кошелёк зачислено {env_amount:N0} кр.", ("[amount]", env_amount.ToString("N0"))));
                return;
            }
            if (mode == CashInModeStation && target.GetComponent<CashEnvelopeComponent>() is { Amount: > 0 } envDeposit) {
                int envAmount = envDeposit.Amount;
                StationEconomyState.ApplyTransaction(null, envAmount, OrderText.Format("pp.order.atm.comment.deposit_envelope",
                    $"Внесение конверта в кассу {who}", ("[who]", who)));
                Entity.Spawner.AddEntityToRemoveQueue(target);
                EconomyNetwork.BroadcastAll();
                HintSender.Success(c, OrderText.Format("pp.order.atm.success.envelope_deposited",
                    $"Конверт на {envAmount:N0} кр. внесён в кассу станции", ("[amount]", envAmount.ToString("N0"))));
                return;
            }
            if (!CheckComponent.IsCheckItem(target))
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.need_receipt_or_envelope", "Нужна денежная квитанция или конверт.")); return; }
            CheckComponent check = target.GetComponent<CheckComponent>();
            if (check == null || !check.IsReceipt || check.Amount <= 0)
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.not_receipt", "Это не денежная квитанция обналичить нельзя")); return; }
            int amount = check.Amount;
            if (mode == CashInModeStation) {
                StationEconomyState.ApplyTransaction(null, amount, OrderText.Format("pp.order.atm.comment.deposit",
                    $"Внесение чека в кассу {who}", ("[who]", who)));
                Entity.Spawner.AddEntityToRemoveQueue(target);
                EconomyNetwork.BroadcastAll();
                HintSender.Success(c, OrderText.Format("pp.order.atm.success.check_deposited",
                    $"Чек на {amount:N0} кр. внесён в кассу станции", ("[amount]", amount.ToString("N0"))));
                return;
            }
            if (!CheckMint.SpawnCashEnvelope(ch, amount, who, onFailure: () => {
                StationEconomyState.ApplyTransaction(null, amount, OrderText.Format("pp.order.atm.comment.refund_envelope_failed",
                    $"Возврат не удалось выдать конверт {who}", ("[who]", who)));
                EconomyNetwork.BroadcastAll();
                HintSender.Error(c, OrderText.Get("pp.order.atm.error.envelope_failed_returned", "Не удалось выдать конверт сумма возвращена в кассу станции"));
            }))
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.envelope_failed", "Не удалось выдать конверт.")); return; }
            Entity.Spawner.AddEntityToRemoveQueue(target);
            HintSender.Success(c, OrderText.Format("pp.order.atm.success.check_cashed",
                $"Чек обналичен конверт на {amount:N0} кр. у вас", ("[amount]", amount.ToString("N0"))));
        }
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
            => EconomyNetIO.WriteState(msg);
        public void PushFullSync() => item.CreateServerEvent(this, new FullSyncEventData());
        private void HandleWithdraw(Client c, IReadMessage msg) {
            int amount = msg.ReadInt32();
            string comment = msg.ReadString();
            if (!item.CanClientAccess(c)) return;
            if (comment != null) {
                comment = comment.Replace('\n', ' ').Replace('\r', ' ');
                if (comment.Length > MaxWithdrawComment) { comment = comment.Substring(0, MaxWithdrawComment); }
            }
            if (!EconomyLogic.HasCaptainAccess(c?.Character, out string err))
            { HintSender.Error(c, err ?? OrderText.Get("pp.order.atm.error.denied", "Отказано")); return; }
            if (amount <= 0)
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.invalid_amount", "Некорректная сумма")); return; }
            if (!EconomyLimits.CheckAmountLimit(amount, out string limitErr))
            { HintSender.Error(c, limitErr); return; }
            string who = c?.Character?.Info?.Name ?? c?.Name ?? OrderText.Get("pp.order.atm.unknown_who", "неизвестно");
            if (!EconomyLimits.CheckCooldown(c, out string cooldownErr))
            { HintSender.Error(c, cooldownErr); return; }
            if (amount > StationEconomyState.Balance)
            { HintSender.Error(c, OrderText.Get("pp.order.atm.error.insufficient_station_balance", "Недостаточно средств на балансе станции")); return; }
            string finalComment = string.IsNullOrWhiteSpace(comment)
                ? OrderText.Format("pp.order.atm.comment.withdraw", $"Снятие наличных {who}", ("[who]", who))
                : OrderText.Format("pp.order.atm.comment.withdraw_with_note",
                    $"Снятие наличных {who} {comment.Trim()}", ("[who]", who), ("[note]", comment.Trim()));
            StationEconomyState.ApplyTransaction(null, -amount, finalComment);
            EconomyLimits.RecordWithdraw(c);
            EconomyNetwork.BroadcastAll();
            string date = System.DateTime.Now.ToString("dd.MM.yyyy");
            bool receiptSpawned = c?.Character is { Removed: false } withdrawer
                && CheckMint.SpawnReceiptCheck(withdrawer, amount, who, date, onFailure: () => {
                    StationEconomyState.ApplyTransaction(null, amount, OrderText.Format("pp.order.atm.comment.refund_check_failed",
                        $"Возврат не удалось выдать чек {who}", ("[who]", who)));
                    EconomyNetwork.BroadcastAll();
                    HintSender.Error(c, OrderText.Get("pp.order.atm.error.check_failed_returned", "Не удалось выдать чек сумма возвращена на баланс станции"));
                });
            HintSender.Success(c, receiptSpawned
                ? OrderText.Format("pp.order.atm.success.issued_with_check", $"Выдано {amount:N0} кр. Чек в вашем инвентаре", ("[amount]", amount.ToString("N0")))
                : OrderText.Format("pp.order.atm.success.issued", $"Выдано {amount:N0} кр.", ("[amount]", amount.ToString("N0"))));
        }
    }
}
