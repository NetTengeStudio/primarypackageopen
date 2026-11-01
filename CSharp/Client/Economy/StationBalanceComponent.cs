using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    public partial class StationBalanceComponent {
        private GUITextBlock balanceText;
        private GUIListBox historyList;
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
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData) {}
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            EconomyNetIO.ReadState(msg, out int balance, out var history);
            StationEconomyState.ReplaceFromSync(balance, history);
            RefreshDisplay();
        }
        private void BuildUI() {
            if (GuiFrame == null) return;
            GuiFrame.ClearChildren();
            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.92f), GuiFrame.RectTransform, Anchor.Center))
                { Stretch = true, RelativeSpacing = 0.02f };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform),
                OrderText.Get("pp.order.balance.title", "Терминал Баланса Станции"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);
            balanceText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.09f), content.RectTransform), "",
                font: GUIStyle.LargeFont, textAlignment: Alignment.Center);
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.04f), content.RectTransform),
                OrderText.Get("pp.order.balance.history_label", "Журнал операций"), font: GUIStyle.Font, textAlignment: Alignment.Center);
            historyList = new GUIListBox(new RectTransform(new Vector2(1f, 0.70f), content.RectTransform));
            RefreshDisplay();
        }
        public override bool Select(Character character) {
            if (character == Character.Controlled) { RefreshDisplay(); EconomyUiSounds.PlayOpen(item.WorldPosition); }
            return base.Select(character);
        }
        private void RefreshDisplay() {
            lastBalance = StationEconomyState.Balance;
            lastHistoryCount = StationEconomyState.History.Count;
            if (balanceText != null) {
                balanceText.Text = OrderText.Format("pp.order.balance.balance_label",
                    $"Баланс: {lastBalance:N0} кр.", ("[amount]", lastBalance.ToString("N0")));
                balanceText.TextColor = lastBalance < 0 ? Color.Red : Color.LightGreen;
            }
            if (historyList == null) return;
            historyList.Content.ClearChildren();
            foreach (LedgerEntry entry in StationEconomyState.History.AsEnumerable().Reverse()) {
                bool income = entry.Amount >= 0;
                string kindLabel = income ? OrderText.Get("pp.order.ledger.income", "ПРИХОД") : OrderText.Get("pp.order.ledger.expense", "РАСХОД");
                string signedAmount = $"{(income ? "+" : "-")}{System.Math.Abs(entry.Amount):N0}";
                string line = OrderText.Format("pp.order.ledger.row",
                    $"[{entry.Date}] | {kindLabel} | {signedAmount} кр. | {entry.Comment}",
                    ("[date]", entry.Date), ("[kind]", kindLabel), ("[amount]", signedAmount), ("[comment]", entry.Comment));
                var row = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), historyList.Content.RectTransform),
                    line, textColor: income ? Color.LightGreen : Color.OrangeRed, wrap: true)
                    { CanBeFocused = false };
                GuiRowFit.FitWrappedRow(historyList, row);
            }
        }
    }
}
