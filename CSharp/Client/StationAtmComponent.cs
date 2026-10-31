using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    public partial class StationAtmComponent {
        private GUITextBlock balanceText;
        private GUITextBox amountBox, commentBox;
        private GUITextBlock statusText;
        private GUIListBox recentList;
        private int lastBalance = int.MinValue;
        private int lastHistoryCount = -1;
        partial void InitProjSpecific() => BuildUI();
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            if (GameMain.Client != null && !EconomyClientState.FullSyncRequested) {
                EconomyClientState.FullSyncRequested = true;
                item.CreateClientEvent(this, new FullSyncRequestEventData());
            }
        }
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            if (StationEconomyState.Balance != lastBalance || StationEconomyState.History.Count != lastHistoryCount)
                RefreshDisplay();
        }
        private void BuildUI() {
            if (GuiFrame == null) return;
            GuiFrame.ClearChildren();
            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.85f), GuiFrame.RectTransform, Anchor.Center))
                { Stretch = true, RelativeSpacing = 0.015f };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform),
                OrderText.Get("pp.order.atm.title", "Банкомат Станции"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);
            balanceText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.09f), content.RectTransform), "",
                font: GUIStyle.LargeFont, textAlignment: Alignment.Center);
            var amountRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform), isHorizontal: true)
                { Stretch = true, RelativeSpacing = 0.02f };
            new GUITextBlock(new RectTransform(new Vector2(0.4f, 1f), amountRow.RectTransform), OrderText.Get("pp.order.atm.amount_label", "Сумма"), textAlignment: Alignment.CenterLeft);
            amountBox = new GUITextBox(new RectTransform(new Vector2(0.6f, 1f), amountRow.RectTransform));
            commentBox = new GUITextBox(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform))
                { OverflowClip = true, MaxTextLength = MaxWithdrawComment };
            statusText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), content.RectTransform), "",
                textAlignment: Alignment.Center, wrap: true);
            new GUIButton(new RectTransform(new Vector2(1f, 0.07f), content.RectTransform), OrderText.Get("pp.order.atm.withdraw_button", "Снять наличные"))
                { OnClicked = (b, d) => { DoWithdraw(); return true; } };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.035f), content.RectTransform),
                OrderText.Get("pp.order.atm.subtitle_cashin", "Обналичивание чека извлечение конверта"), font: GUIStyle.Font, textAlignment: Alignment.Center);
            var cashRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), content.RectTransform), isHorizontal: true)
                { Stretch = true, RelativeSpacing = 0.02f };
            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), cashRow.RectTransform), OrderText.Get("pp.order.atm.check_to_envelope", "Чек → конверт"))
                { ToolTip = OrderText.Get("pp.order.atm.check_to_envelope_tooltip", "Выдать физический конверт с суммой чека"), OnClicked = (b, d) => { DoCashIn(CashInModeEnvelope); return true; } };
            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), cashRow.RectTransform), OrderText.Get("pp.order.atm.check_to_station", "Чек → касса"))
                { ToolTip = OrderText.Get("pp.order.atm.check_to_station_tooltip", "Внести сумму чека в общий баланс станции"), OnClicked = (b, d) => { DoCashIn(CashInModeStation); return true; } };
            new GUIButton(new RectTransform(new Vector2(1f, 0.06f), content.RectTransform), OrderText.Get("pp.order.atm.envelope_to_wallet", "Извлечь конверт → в кошелёк"))
                { ToolTip = OrderText.Get("pp.order.atm.envelope_to_wallet_tooltip", "Зачислить сумму конверта в ваш кошелёк тратимые кредиты кампании"), OnClicked = (b, d) => { DoRedeemEnvelope(); return true; } };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.03f), content.RectTransform),
                OrderText.Get("pp.order.atm.recent_ops", "Последние операции"), font: GUIStyle.Font, textAlignment: Alignment.Center);
            recentList = new GUIListBox(new RectTransform(new Vector2(1f, 0.15f), content.RectTransform));
            RefreshDisplay();
        }
        public override bool Select(Character character) {
            if (character == Character.Controlled) { RefreshDisplay(); EconomyUiSounds.PlayOpen(item.WorldPosition); }
            return base.Select(character);
        }
        private void DoWithdraw() {
            EconomyUiSounds.PlayClick(item.WorldPosition);
            if (statusText != null) statusText.Text = "";
            if (!EconomyLogic.HasCaptainAccess(Character.Controlled, out string err))
                { ShowStatus(err ?? OrderText.Get("pp.order.atm.error.denied", "Отказано"), Color.Red); return; }
            if (!int.TryParse((amountBox?.Text ?? "").Trim(), out int amount) || amount <= 0)
                { ShowStatus(OrderText.Get("pp.order.atm.status.enter_valid_amount", "Введите корректную сумму"), Color.Red); return; }
            string comment = (commentBox?.Text ?? "").Trim();
            if (GameMain.NetworkMember == null) {
                if (amount > StationEconomyState.Balance)
                    { ShowStatus(OrderText.Get("pp.order.atm.error.insufficient_station_balance", "Недостаточно средств на балансе станции"), Color.Red); return; }
                string who = Character.Controlled?.Name ?? OrderText.Get("pp.order.atm.unknown_who", "неизвестно");
                string autoComment = string.IsNullOrEmpty(comment)
                    ? OrderText.Format("pp.order.atm.comment.withdraw", $"Снятие наличных {who}", ("[who]", who))
                    : OrderText.Format("pp.order.atm.comment.withdraw_with_note",
                        $"Снятие наличных {who} {comment}", ("[who]", who), ("[note]", comment));
                StationEconomyState.ApplyTransaction(null, -amount, autoComment);
                bool receiptSpawned = Character.Controlled is { Removed: false } withdrawer
                    && CheckMint.SpawnReceiptCheck(withdrawer, amount, who, System.DateTime.Now.ToString("dd.MM.yyyy"),
                        onFailure: () => StationEconomyState.ApplyTransaction(null, amount, OrderText.Format("pp.order.atm.comment.refund_check_failed",
                            $"Возврат не удалось выдать чек {who}", ("[who]", who))));
                ShowStatus(OrderText.Get("pp.order.atm.status.op_done", "Операция выполнена"), Color.LightGreen);
                HintToast.Show(receiptSpawned
                    ? OrderText.Format("pp.order.atm.success.issued_with_check", $"Выдано {amount:N0} кр. Чек в вашем инвентаре", ("[amount]", amount.ToString("N0")))
                    : OrderText.Format("pp.order.atm.success.issued", $"Выдано {amount:N0} кр.", ("[amount]", amount.ToString("N0"))), Color.LightGreen);
                RefreshDisplay();
                return;
            }
            item.CreateClientEvent(this, new WithdrawRequestEventData(amount, comment));
            ShowStatus(OrderText.Get("pp.order.atm.status.request_sent", "Запрос отправлен"), Color.LightGray);
        }
        private void DoCashIn(byte mode) {
            EconomyUiSounds.PlayClick(item.WorldPosition);
            if (statusText != null) statusText.Text = "";
            Item checkItem = FindAnyCheck();
            if (checkItem == null && mode == CashInModeStation) { checkItem = FindAnyEnvelope(); }
            if (checkItem == null) {
                ShowStatus(OrderText.Get("pp.order.atm.status.no_check", "Нет чека в руках или в слоте обменника Снимите наличные получите денежную квитанцию"), Color.Red);
                return;
            }
            if (GameMain.NetworkMember == null) {
                string who = Character.Controlled?.Name ?? OrderText.Get("pp.order.atm.unknown_who", "неизвестно");
                if (mode == CashInModeStation && checkItem.GetComponent<CashEnvelopeComponent>() is { Amount: > 0 } envDeposit) {
                    int envAmount = envDeposit.Amount;
                    StationEconomyState.ApplyTransaction(null, envAmount, OrderText.Format("pp.order.atm.comment.deposit_envelope",
                        $"Внесение конверта в кассу {who}", ("[who]", who)));
                    checkItem.Remove();
                    ShowStatus(OrderText.Get("pp.order.atm.status.check_deposited", "Чек внесён в кассу"), Color.LightGreen);
                    HintToast.Show(OrderText.Format("pp.order.atm.success.envelope_deposited",
                        $"Конверт на {envAmount:N0} кр. внесён в кассу станции", ("[amount]", envAmount.ToString("N0"))), Color.LightGreen);
                    RefreshDisplay();
                    return;
                }
                CheckComponent chk = checkItem.GetComponent<CheckComponent>();
                if (chk is not { IsReceipt: true, Amount: > 0 })
                { ShowStatus(OrderText.Get("pp.order.atm.error.not_receipt", "Это не денежная квитанция обналичить нельзя"), Color.Red); return; }
                int amount = chk.Amount;
                if (mode == CashInModeStation) {
                    StationEconomyState.ApplyTransaction(null, amount, OrderText.Format("pp.order.atm.comment.deposit",
                        $"Внесение чека в кассу {who}", ("[who]", who)));
                    checkItem.Remove();
                    ShowStatus(OrderText.Get("pp.order.atm.status.check_deposited", "Чек внесён в кассу"), Color.LightGreen);
                    HintToast.Show(OrderText.Format("pp.order.atm.success.check_deposited",
                        $"Чек на {amount:N0} кр. внесён в кассу станции", ("[amount]", amount.ToString("N0"))), Color.LightGreen);
                    RefreshDisplay();
                    return;
                }
                bool ok = Character.Controlled is { Removed: false } me && CheckMint.SpawnCashEnvelope(me, amount, who,
                    onFailure: () => StationEconomyState.ApplyTransaction(null, amount, OrderText.Format("pp.order.atm.comment.refund_envelope_failed",
                        $"Возврат не удалось выдать конверт {who}", ("[who]", who))));
                if (ok) checkItem.Remove();
                ShowStatus(ok ? OrderText.Get("pp.order.atm.status.check_cashed", "Чек обналичен") : OrderText.Get("pp.order.atm.status.envelope_failed", "Не удалось выдать конверт"), ok ? Color.LightGreen : Color.Red);
                if (ok) HintToast.Show(OrderText.Format("pp.order.atm.success.envelope_received",
                    $"Конверт на {amount:N0} кр. у вас", ("[amount]", amount.ToString("N0"))), Color.LightGreen);
                return;
            }
            item.CreateClientEvent(this, new CashInRequestEventData(checkItem.ID, mode));
            ShowStatus(OrderText.Get("pp.order.atm.status.request_sent", "Запрос отправлен"), Color.LightGray);
        }
        private Item FindAnyCheck() {
            Item fallback = null;
            foreach (Item it in EnumerateReachable()) {
                if (!CheckComponent.IsCheckItem(it)) { continue; }
                if (it.GetComponent<CheckComponent>() is { IsReceipt: true }) { return it; }
                fallback ??= it;
            }
            return fallback;
        }
        private IEnumerable<Item> EnumerateReachable() {
            Character me = Character.Controlled;
            if (me?.Inventory != null) {
                foreach (Item it in me.Inventory.AllItems) { yield return it; }
            }
            if (item.OwnInventory != null) {
                foreach (Item it in item.OwnInventory.AllItems) { yield return it; }
            }
        }
        private void DoRedeemEnvelope() {
            EconomyUiSounds.PlayClick(item.WorldPosition);
            if (statusText != null) statusText.Text = "";
            Item env = FindAnyEnvelope();
            if (env == null)
            { ShowStatus(OrderText.Get("pp.order.atm.status.no_envelope", "Нет конверта с наличными в руках или в слоте обменника"), Color.Red); return; }
            if (GameMain.NetworkMember == null) {
                int amount = env.GetComponent<CashEnvelopeComponent>()?.Amount ?? 0;
                if (amount <= 0) { ShowStatus(OrderText.Get("pp.order.atm.error.not_envelope", "Это не конверт с наличными"), Color.Red); return; }
                CampaignMode campaign = GameMain.GameSession?.Campaign;
                if (campaign == null) { ShowStatus(OrderText.Get("pp.order.atm.error.wallet_campaign_only", "Кошелёк доступен только в режиме кампании"), Color.Red); return; }
                campaign.GetWallet().Give(amount);
                env.Remove();
                ShowStatus(OrderText.Get("pp.order.atm.status.wallet_credited", "Зачислено в кошелёк"), Color.LightGreen);
                HintToast.Show(OrderText.Format("pp.order.atm.success.wallet_credited",
                    $"В кошелёк зачислено {amount:N0} кр.", ("[amount]", amount.ToString("N0"))), Color.LightGreen);
                return;
            }
            item.CreateClientEvent(this, new CashInRequestEventData(env.ID, CashInModeEnvelopeToWallet));
            ShowStatus(OrderText.Get("pp.order.atm.status.request_sent", "Запрос отправлен"), Color.LightGray);
        }
        private Item FindAnyEnvelope() {
            foreach (Item it in EnumerateReachable()) {
                if (CashEnvelopeComponent.IsEnvelopeItem(it)) { return it; }
            }
            return null;
        }
        private void ShowStatus(string text, Color color) {
            if (statusText == null) return;
            statusText.Text = text;
            statusText.TextColor = color;
        }
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData) {
            if (TryExtractEventData<WithdrawRequestEventData>(extraData, out var req)) {
                msg.WriteByte((byte)0);
                msg.WriteInt32(req.Amount);
                msg.WriteString(req.Comment ?? "");
            }
            else if (TryExtractEventData<CashInRequestEventData>(extraData, out var cash)) {
                msg.WriteByte((byte)2);
                msg.WriteUInt16(cash.ItemId);
                msg.WriteByte(cash.Mode);
            }
            else {
                msg.WriteByte((byte)1);
            }
        }
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            EconomyNetIO.ReadState(msg, out int balance, out var history);
            StationEconomyState.ReplaceFromSync(balance, history);
            RefreshDisplay();
        }
        private void RefreshDisplay() {
            lastBalance = StationEconomyState.Balance;
            lastHistoryCount = StationEconomyState.History.Count;
            if (balanceText != null) {
                balanceText.Text = OrderText.Format("pp.order.balance.balance_label",
                    $"Баланс {lastBalance:N0} кр.", ("[amount]", lastBalance.ToString("N0")));
                balanceText.TextColor = lastBalance < 0 ? Color.Red : Color.LightGreen;
            }
            if (recentList == null) return;
            recentList.Content.ClearChildren();
            foreach (LedgerEntry entry in StationEconomyState.History.AsEnumerable().Reverse().Take(10)) {
                bool income = entry.Amount >= 0;
                string kindLabel = income ? OrderText.Get("pp.order.ledger.income", "ПРИХОД") : OrderText.Get("pp.order.ledger.expense", "РАСХОД");
                string signedAmount = $"{(income ? "+" : "-")}{System.Math.Abs(entry.Amount):N0}";
                string line = OrderText.Format("pp.order.ledger.row",
                    $"[{entry.Date}] | {kindLabel} | {signedAmount} кр. | {entry.Comment}",
                    ("[date]", entry.Date), ("[kind]", kindLabel), ("[amount]", signedAmount), ("[comment]", entry.Comment));
                var row = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), recentList.Content.RectTransform),
                    line, textColor: income ? Color.LightGreen : Color.OrangeRed, wrap: true)
                    { CanBeFocused = false };
                GuiRowFit.FitWrappedRow(recentList, row);
            }
        }
    }
}
