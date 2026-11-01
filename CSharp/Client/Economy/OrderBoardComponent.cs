using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    public partial class OrderBoardComponent {
        private GUIListBox presetList;
        private Identifier selectedPreset;
        private byte selectedDirection = DirectionDelivery;
        private GUIButton directionButton;
        private GUITextBlock statusText;
        private double statusClearAt = -1;
        partial void InitProjSpecific() => BuildUI();
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            if (statusClearAt > 0 && Timing.TotalTime >= statusClearAt) {
                statusClearAt = -1;
                if (statusText != null) { statusText.Text = ""; }
            }
        }
        private void BuildUI() {
            if (GuiFrame == null) { return; }
            GuiFrame.ClearChildren();
            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), GuiFrame.RectTransform, Anchor.Center))
                { Stretch = true, RelativeSpacing = 0.02f };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform),
                OrderText.Get("pp.order.board.title", "Доска Заказов Станции"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                OrderText.Get("pp.order.board.choose_set", "Выберите комплект"), font: GUIStyle.Font, textAlignment: Alignment.CenterLeft);
            presetList = new GUIListBox(new RectTransform(new Vector2(1f, 0.55f), content.RectTransform));
            RebuildPresetList();
            directionButton = new GUIButton(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform), "")
                { OnClicked = (b, d) => { ToggleDirection(); return true; } };
            UpdateDirectionLabel();
            new GUIButton(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform), OrderText.Get("pp.order.board.submit_button", "Оформить"))
                { OnClicked = (b, d) => { DoRequest(); return true; } };
            statusText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), content.RectTransform), "",
                textAlignment: Alignment.Center, wrap: true);
        }
        private void RebuildPresetList() {
            if (presetList == null) { return; }
            presetList.Content.ClearChildren();
            foreach (OrderSet set in OrderSets.All) {
                var block = new GUITextBlock(new RectTransform(new Vector2(1f, 0.15f), presetList.Content.RectTransform),
                    OrderText.Format("pp.order.board.preset_label2",
                        $"{set.DisplayName}  (доставка +{set.Reward:N0} / закупка −{set.BuyPrice:N0} кр.)",
                        ("[name]", set.DisplayName), ("[reward]", set.Reward.ToString("N0")), ("[buy]", set.BuyPrice.ToString("N0"))), wrap: true)
                    { UserData = set.Id, CanBeFocused = true, ToolTip = CheckMint.FormatOrderContents(set) };
                block.RectTransform.MinSize = new Point(0, (int)(GUIStyle.Font.MeasureString(block.Text).Y * 1.4f));
            }
            presetList.OnSelected = (child, userData) => {
                if (userData is Identifier id) { selectedPreset = id; }
                return true;
            };
        }
        private void ToggleDirection() {
            EconomyUiSounds.PlayClick(item.WorldPosition);
            selectedDirection = selectedDirection == DirectionDelivery ? DirectionIncoming : DirectionDelivery;
            UpdateDirectionLabel();
        }
        private void UpdateDirectionLabel() {
            if (directionButton != null) {
                directionButton.Text = selectedDirection == DirectionIncoming
                    ? OrderText.Get("pp.order.board.direction_incoming", "Направление Закупка (станция экипаж)")
                    : OrderText.Get("pp.order.board.direction_delivery", "Направление Доставка (экипаж станция)");
            }
        }
        public override bool Select(Character character) {
            RebuildPresetList();
            if (statusText != null) { statusText.Text = ""; }
            statusClearAt = -1;
            if (character == Character.Controlled) { EconomyUiSounds.PlayOpen(item.WorldPosition); }
            return base.Select(character);
        }
        private void DoRequest() {
            EconomyUiSounds.PlayClick(item.WorldPosition);
            if (selectedPreset.IsEmpty) { ShowStatus(OrderText.Get("pp.order.board.select_set_first", "Выберите комплект заказа"), Color.Red); return; }
            if (GameMain.NetworkMember == null) {
                ShowStatus(OrderText.Get("pp.order.board.mp_only", "Система заказов доступна только в сетевой игре"), Color.Yellow);
                return;
            }
            item.CreateClientEvent(this, new OrderRequestEventData(selectedPreset.Value, selectedDirection));
            ShowStatus(OrderText.Get("pp.order.board.request_sent", "Запрос отправлен. Смотрите оповещение над панелью."), Color.LightGray);
            statusClearAt = Timing.TotalTime + 6.0;
        }
        private void ShowStatus(string text, Color color) {
            if (statusText == null) { return; }
            statusText.Text = text;
            statusText.TextColor = color;
        }
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData) {
            if (TryExtractEventData<OrderRequestEventData>(extraData, out var req)) {
                msg.WriteString(req.OrderSetId ?? "");
                msg.WriteByte(req.Direction);
            }
        }
    }
}
