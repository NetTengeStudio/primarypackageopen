using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Economy;
namespace PrimaryPackage.Messenger {
    public partial class PdaMessengerComponent {
        private const string NotifySoundFile = "Content/Items/PDA/Sounds/message_notify.ogg";
        private const string OpenSoundFile = "Content/Items/PDA/Sounds/pdapower.ogg";
        private const string SentSoundFile = "Content/Items/PDA/Sounds/message_sent.ogg";
        private static readonly Color ScreenBezelColor = new Color(24, 24, 25);
        private static readonly Color ScreenPanelColor = new Color(42, 42, 42);
        private static readonly Color ScreenInsetColor = new Color(30, 33, 38);
        private static readonly Color ScreenInsetOutline = new Color(58, 62, 68);
        private const float ScreenBezelThickness = 20f;
        private const float ScreenBezelRadius = 10f;
        private const float ScreenPanelRadius = 14f;
        private const float ContentSizeFrac = 0.90f;
        private static readonly Color TabSelectedColor = new Color(220, 220, 220);
        private static readonly Color[] AvatarPalette = {
            new Color(83, 74, 183), new Color(29, 158, 117), new Color(216, 90, 48),
            new Color(212, 83, 126), new Color(55, 138, 221), new Color(186, 117, 23),
        };
        private static readonly Color GroupAvatarColor = new Color(15, 110, 86);
        private static readonly Color UnknownAvatarColor = new Color(92, 94, 100);
        private static readonly Color BadgeColor = new Color(70, 160, 90);
        private const int RowHeightPx = 34, AvatarSizePx = 26, RowGapPx = 5, BadgeWidthPx = 30;
        private readonly Dictionary<string, string> directory = new Dictionary<string, string>();
        private readonly Dictionary<string, List<(bool fromMe, string text)>> histories = new Dictionary<string, List<(bool, string)>>();
        private readonly Dictionary<string, int> unread = new Dictionary<string, int>();
        private readonly Dictionary<string, double> lastActivity = new Dictionary<string, double>();
        private readonly Dictionary<string, int> groupSizes = new Dictionary<string, int>();
        private readonly List<string> displayedNumbers = new List<string>();
        private string currentPeer = "";
        private bool suppressSel;
        private GUITextBlock headerLabel;
        private GUIListBox contactListBox, conversationBox, srpBox;
        private GUITextBox inputBox, addNumberBox, addNameBox;
        private GUIFrame addForm, messagesPanel, srpPanel;
        private GUIButton tabMessagesBtn, tabSrpBtn;
        private GUITextBlock addFormFieldLabel, addFormNameLabel;
        private bool addFormGroupMode;
        private const double SlotFlashIntervalSec = 1.2;
        private const float IconTintStrength = 0.8f, SpriteTintStrength = 0.45f;
        private double nextSlotFlashTime;
        private Color baseSpriteColor, baseIconColor;
        private bool baseColorsCaptured, iconTinted;
        private readonly struct MsgAnim {
            public readonly GUITextBlock Block;
            public readonly double Start;
            public readonly bool FromMe;
            public readonly Color BaseColor;
            public MsgAnim(GUITextBlock block, double start, bool fromMe, Color baseColor) { Block = block; Start = start; FromMe = fromMe; BaseColor = baseColor; }
        }
        private readonly List<MsgAnim> messageAnims = new List<MsgAnim>();
        private const float MessageAnimDuration = 0.22f;
        private const int MessageSlideDistance = 40;
        private const int MessagePadding = 12;
        private float scrollTarget;
        private bool scrollTargetValid;
        private const float ScrollSmoothingRate = 18f;
        private const float WheelScrollScale = 500f;
        private readonly Dictionary<string, double> lastSendTime = new Dictionary<string, double>();
        private double sendRejectStart = -1;
        private const float SendRejectDuration = 0.3f;
        private const int SendRejectShakePx = 7;
        partial void InitProjSpecific() {
            BuildUI();
            if (GuiFrame != null) { GuiFrame.Visible = false; }
        }
        public override bool Select(Character character) {
            if (character == Character.Controlled) {
                if (GuiFrame != null) { GuiFrame.Visible = true; }
                PlayOpen();
                if (GameMain.Client != null) { item.CreateClientEvent(this, new OpenReq()); }
                ShowTab(0);
                RefreshHeader();
                RefreshContacts();
            }
            return base.Select(character);
        }
        private void UpdateNotificationBlink() {
            bool notify = unread.Count > 0
                && Character.Controlled != null
                && (GuiFrame == null || !GuiFrame.Visible)
                && item.GetRootInventoryOwner() == Character.Controlled;
            if (notify) {
                if (!baseColorsCaptured) {
                    baseSpriteColor = item.SpriteColor;
                    baseIconColor = item.InventoryIconColor;
                    baseColorsCaptured = true;
                }
                float pulse = (float)(0.5 - 0.5 * Math.Cos(Timing.TotalTime * (Math.PI * 2.0 / SlotFlashIntervalSec)));
                item.InventoryIconColor = Color.Lerp(baseIconColor, GUIStyle.Green, pulse * IconTintStrength);
                item.SpriteColor = Color.Lerp(baseSpriteColor, GUIStyle.Green, pulse * SpriteTintStrength);
                iconTinted = true;
                if (Timing.TotalTime >= nextSlotFlashTime) {
                    nextSlotFlashTime = Timing.TotalTime + SlotFlashIntervalSec;
                    FlashInventorySlot();
                }
            }
            else if (iconTinted) {
                item.SpriteColor = baseSpriteColor;
                item.InventoryIconColor = baseIconColor;
                iconTinted = false;
                baseColorsCaptured = false;
            }
        }
        private void FlashInventorySlot() {
            Item slotItem = item;
            for (int guard = 0; guard < 32 && slotItem.ParentInventory is ItemInventory itemInv && itemInv.Owner is Item container; guard++) { slotItem = container; }
            if (slotItem.ParentInventory is not CharacterInventory charInv || charInv.visualSlots == null) { return; }
            int index = charInv.FindIndex(slotItem);
            if (index < 0 || index >= charInv.visualSlots.Length || charInv.visualSlots[index] == null) { return; }
            charInv.visualSlots[index].ShowBorderHighlight(GUIStyle.Green, 0.15f, 0.65f, 0.25f);
        }
        private void CloseWindow() {
            if (GuiFrame != null) { GuiFrame.Visible = false; }
            if (addForm != null) { addForm.Visible = false; }
            if (Character.Controlled is {} controlled && controlled.SelectedItem == item) { controlled.SelectedItem = null; }
        }
        public override bool UpdateWhenInactive => true;
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            UpdateMessageAnimations();
            UpdateSendReject();
            UpdateConversationScroll(deltaTime);
            UpdateNotificationBlink();
            if (!OpenSignal) { return; }
            OpenSignal = false;
            if (Character.Controlled != null && item.GetRootInventoryOwner() == Character.Controlled) {
                if (GuiFrame != null && GuiFrame.Visible) { CloseWindow(); return; }
                Select(Character.Controlled);
                Character.Controlled.SelectedItem = item;
            }
        }
        public override void UpdateHUDComponentSpecific(Character character, float deltaTime, Camera cam) {
            if (character != Character.Controlled || GuiFrame == null || !GuiFrame.Visible) { return; }
            if (PrimaryPackage.Core.PlayView.Typing && !(inputBox != null && inputBox.Selected)) { return; }
            if (PlayerInput.KeyHit(Keys.Escape)) { CloseWindow(); return; }
            if (srpPanel != null && srpPanel.Visible) { return; }
            if (contactListBox == null || displayedNumbers.Count == 0) { return; }
            if (inputBox != null && inputBox.Selected) { return; }
            if (addForm != null && addForm.Visible) { return; }
            if (PlayerInput.KeyHit(Keys.Down)) { MoveSel(1); }
            else if (PlayerInput.KeyHit(Keys.Up)) { MoveSel(-1); }
        }
        private void MoveSel(int d) {
            int idx = displayedNumbers.IndexOf(currentPeer);
            int start = idx < 0 ? (d > 0 ? -1 : displayedNumbers.Count) : idx;
            int ni = Math.Clamp(start + d, 0, displayedNumbers.Count - 1);
            SelectContact(displayedNumbers[ni]);
        }
        private void BuildUI() {
            if (GuiFrame == null) { return; }
            GuiFrame.ClearChildren();
            new GUICustomComponent(new RectTransform(Vector2.One, GuiFrame.RectTransform), onDraw: (sb, comp) => {
                DrawRoundedRect(sb, comp.Rect, ScreenBezelColor, ScreenBezelRadius * GUI.Scale);
                int inset = (int)(ScreenBezelThickness * GUI.Scale);
                var panelRect = new Rectangle(comp.Rect.X + inset, comp.Rect.Y + inset,
                    Math.Max(0, comp.Rect.Width - inset * 2), Math.Max(0, comp.Rect.Height - inset * 2));
                DrawRoundedRect(sb, panelRect, ScreenPanelColor, ScreenPanelRadius * GUI.Scale);
            }) { CanBeFocused = false };
            var root = new GUILayoutGroup(new RectTransform(new Vector2(ContentSizeFrac, ContentSizeFrac), GuiFrame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.02f };
            var tabBar = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.08f), root.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
            tabMessagesBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), tabBar.RectTransform), OrderText.Get("pp.order.messenger.tab_messages", "Сообщения")) { SelectedColor = TabSelectedColor, OnClicked = (b, d) => { ShowTab(0); return true; } };
            tabSrpBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), tabBar.RectTransform), OrderText.Get("pp.order.messenger.tab_srp", "СРП")) { SelectedColor = TabSelectedColor, OnClicked = (b, d) => { ShowTab(1); return true; } };
            var content = new GUIFrame(new RectTransform(new Vector2(1f, 0.9f), root.RectTransform), style: null);
            messagesPanel = new GUIFrame(new RectTransform(Vector2.One, content.RectTransform), style: null);
            var msgRoot = new GUILayoutGroup(new RectTransform(Vector2.One, messagesPanel.RectTransform)) { Stretch = true, RelativeSpacing = 0.02f };
            headerLabel = new GUITextBlock(new RectTransform(new Vector2(1f, 0.09f), msgRoot.RectTransform), "", font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterLeft);
            var columns = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.89f), msgRoot.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.025f };
            var left = new GUILayoutGroup(new RectTransform(new Vector2(0.38f, 1f), columns.RectTransform)) { Stretch = true, RelativeSpacing = 0.015f };
            var contactBg = new GUIFrame(new RectTransform(new Vector2(1f, 0.76f), left.RectTransform), style: null, color: ScreenInsetColor) { OutlineColor = ScreenInsetOutline, OutlineThickness = 1f };
            contactListBox = new GUIListBox(new RectTransform(Vector2.One, contactBg.RectTransform), style: null) { OnSelected = (c, data) => { if (!suppressSel) { SelectContact(data as string); } return true; } };
            new GUIButton(new RectTransform(new Vector2(1f, 0.09f), left.RectTransform), OrderText.Get("pp.order.messenger.add_contact", "Добавить контакт")) { OnClicked = (b, d) => { OpenForm(groupMode: false, prefill: null); return true; } };
            new GUIButton(new RectTransform(new Vector2(1f, 0.09f), left.RectTransform), OrderText.Get("pp.order.messenger.create_group", "Создать группу")) { OnClicked = (b, d) => { OpenForm(groupMode: true, prefill: null); return true; } };
            new GUIFrame(new RectTransform(new Vector2(1f, 0.03f), left.RectTransform), style: null);
            var right = new GUILayoutGroup(new RectTransform(new Vector2(0.6f, 1f), columns.RectTransform)) { Stretch = true, RelativeSpacing = 0.015f };
            var convBg = new GUIFrame(new RectTransform(new Vector2(1f, 0.68f), right.RectTransform), style: null, color: ScreenInsetColor) { OutlineColor = ScreenInsetOutline, OutlineThickness = 1f };
            conversationBox = new GUIListBox(new RectTransform(Vector2.One, convBg.RectTransform), style: null) { AllowMouseWheelScroll = false };
            var inputRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.11f), right.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            inputBox = new GUITextBox(new RectTransform(new Vector2(0.76f, 1f), inputRow.RectTransform)) { MaxTextLength = MaxMessageLen, OverflowClip = true };
            inputBox.OnEnterPressed = (b, t) => { SendCurrent(); return true; };
            new GUIButton(new RectTransform(new Vector2(0.24f, 1f), inputRow.RectTransform), OrderText.Get("pp.order.messenger.send_button", "Отправить")) { OnClicked = (b, d) => { SendCurrent(); return true; } };
            new GUIButton(new RectTransform(new Vector2(1f, 0.09f), right.RectTransform), OrderText.Get("pp.order.messenger.rename_contact", "Переименовать контакт")) { OnClicked = (b, d) => { if (!string.IsNullOrEmpty(currentPeer)) { OpenForm(groupMode: false, prefill: currentPeer); } return true; } };
            new GUIFrame(new RectTransform(new Vector2(1f, 0.03f), right.RectTransform), style: null);
            srpPanel = new GUIFrame(new RectTransform(Vector2.One, content.RectTransform), style: null, color: ScreenInsetColor) { Visible = false, OutlineColor = ScreenInsetOutline, OutlineThickness = 1f };
            srpBox = new GUIListBox(new RectTransform(Vector2.One, srpPanel.RectTransform), style: null);
            addForm = new GUIFrame(new RectTransform(new Vector2(0.55f, 0.4f), GuiFrame.RectTransform, Anchor.Center), style: "GUIFrameListBox") { Visible = false };
            var col = new GUILayoutGroup(new RectTransform(new Vector2(0.88f, 0.86f), addForm.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.025f };
            addFormFieldLabel = new GUITextBlock(new RectTransform(new Vector2(1f, 0.14f), col.RectTransform), OrderText.Get("pp.order.messenger.field_number", "Номер КПК"), textAlignment: Alignment.CenterLeft);
            addNumberBox = new GUITextBox(new RectTransform(new Vector2(1f, 0.18f), col.RectTransform)) { MaxTextLength = NumberDigits };
            addNumberBox.OnEnterPressed = (b, t) => { SaveContact(); return true; };
            addFormNameLabel = new GUITextBlock(new RectTransform(new Vector2(1f, 0.14f), col.RectTransform), OrderText.Get("pp.order.messenger.field_name", "Имя контакта"), textAlignment: Alignment.CenterLeft);
            addNameBox = new GUITextBox(new RectTransform(new Vector2(1f, 0.18f), col.RectTransform)) { MaxTextLength = MaxNameLen };
            addNameBox.OnEnterPressed = (b, t) => { SaveContact(); return true; };
            var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.18f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.05f };
            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttons.RectTransform), OrderText.Get("pp.order.messenger.save_button", "Сохранить")) { OnClicked = (b, d) => { SaveContact(); return true; } };
            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttons.RectTransform), OrderText.Get("pp.order.messenger.cancel_button", "Отмена")) { OnClicked = (b, d) => { addForm.Visible = false; return true; } };
            RefreshSrp();
        }
        private static void DrawRoundedRect(SpriteBatch sb, Rectangle rect, Color color, float radius) {
            int r = (int)Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
            if (r <= 1) { GUI.DrawRectangle(sb, rect, color, isFilled: true); return; }
            GUI.DrawRectangle(sb, new Rectangle(rect.X, rect.Y + r, rect.Width, rect.Height - r * 2), color, isFilled: true);
            int steps = Math.Clamp(r, 6, 64);
            for (int i = 0; i < steps; i++) {
                int y0 = i * r / steps, y1 = (i + 1) * r / steps;
                int bandHeight = Math.Max(1, y1 - y0);
                float cy = r - (y0 + y1) * 0.5f;
                int dx = (int)(r - Math.Sqrt(Math.Max(0f, r * r - cy * cy)));
                int bandWidth = Math.Max(0, rect.Width - dx * 2);
                GUI.DrawRectangle(sb, new Rectangle(rect.X + dx, rect.Y + y0, bandWidth, bandHeight), color, isFilled: true);
                GUI.DrawRectangle(sb, new Rectangle(rect.X + dx, rect.Bottom - y1, bandWidth, bandHeight), color, isFilled: true);
            }
        }
        private void ShowTab(int tab) {
            if (messagesPanel != null) { messagesPanel.Visible = tab == 0; }
            if (srpPanel != null) { srpPanel.Visible = tab == 1; }
            if (tabMessagesBtn != null) { tabMessagesBtn.Selected = tab == 0; }
            if (tabSrpBtn != null) { tabSrpBtn.Selected = tab == 1; }
            if (tab == 1) { if (addForm != null) { addForm.Visible = false; } RefreshSrp(); }
        }
        private void RefreshSrp() {
            if (srpBox == null) { return; }
            srpBox.Content.ClearChildren();
            string text = string.IsNullOrEmpty(SrpTag) ? "" : TextManager.Get(SrpTag).Fallback(SrpTag.Replace("\\n", "\n")).Value;
            if (string.IsNullOrEmpty(text)) { text = OrderText.Get("pp.order.messenger.srp_unavailable", "СРП недоступен."); }
            var block = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), srpBox.Content.RectTransform), text, font: GUIStyle.MonospacedFont, wrap: true, textAlignment: Alignment.TopLeft) { CanBeFocused = false };
            for (int pass = 0; pass < 4; pass++) {
                srpBox.RecalculateChildren();
                int height = (int)Math.Ceiling(block.TextSize.Y) + 6;
                if (block.RectTransform.MinSize.Y == height) { break; }
                block.RectTransform.MinSize = new Point(0, height);
            }
            srpBox.RecalculateChildren();
            srpBox.ScrollBar.BarScroll = 0f;
        }
        private void OpenForm(bool groupMode, string prefill) {
            if (addForm == null) { return; }
            bool show = !addForm.Visible || addFormGroupMode != groupMode;
            addForm.Visible = show;
            if (!show) { return; }
            addFormGroupMode = groupMode;
            addFormFieldLabel.Text = groupMode ? OrderText.Get("pp.order.messenger.field_numbers_csv", "Номера через запятую") : OrderText.Get("pp.order.messenger.field_number", "Номер КПК");
            addFormNameLabel.Text = groupMode ? OrderText.Get("pp.order.messenger.field_group_name", "Название группы") : OrderText.Get("pp.order.messenger.field_name", "Имя контакта");
            addNumberBox.MaxTextLength = groupMode ? (NumberDigits + 2) * MaxGroupMembers : NumberDigits;
            addNumberBox.Text = groupMode ? "" : prefill ?? "";
            addNameBox.Text = !groupMode && prefill != null ? DisplayName(prefill) : "";
        }
        private void RefreshHeader() {
            if (headerLabel == null) { return; }
            string num = NormalizeNumber(DeviceNumber);
            headerLabel.Text = string.IsNullOrEmpty(num)
                ? OrderText.Get("pp.order.messenger.header_offline", "Сообщения устройство не в сети")
                : OrderText.Format("pp.order.messenger.header_number", "Ваш номер " + num, ("[num]", num));
        }
        private string DisplayName(string number) {
            foreach (var c in GetContacts()) { if (c.Number == number) { return c.Name; } }
            return directory.TryGetValue(number, out var name) && !string.IsNullOrEmpty(name) ? name : "";
        }
        private void RefreshContacts() {
            if (contactListBox == null) { return; }
            contactListBox.Content.ClearChildren();
            displayedNumbers.Clear();
            var contactNames = new Dictionary<string, string>();
            var seen = new HashSet<string>();
            void Add(IEnumerable<string> nums) { foreach (var n in nums) { if (seen.Add(n)) { displayedNumbers.Add(n); } } }
            foreach (var c in GetContacts()) {
                if (!contactNames.ContainsKey(c.Number)) { contactNames[c.Number] = c.Name; }
                if (seen.Add(c.Number)) { displayedNumbers.Add(c.Number); }
            }
            Add(directory.Keys.Where(IsGroupNumber));
            Add(histories.Keys);
            Add(unread.Keys);
            string ResolveName(string number) =>
                contactNames.TryGetValue(number, out var cn) ? cn
                : directory.TryGetValue(number, out var dn) ? dn : "";
            displayedNumbers.Sort((a, b) => {
                int ua = unread.ContainsKey(a) ? 1 : 0, ub = unread.ContainsKey(b) ? 1 : 0;
                if (ua != ub) { return ub - ua; }
                double la = lastActivity.GetValueOrDefault(a), lb = lastActivity.GetValueOrDefault(b);
                if (la != lb) { return lb.CompareTo(la); }
                int byName = string.Compare(ResolveName(a), ResolveName(b), StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.CompareOrdinal(a, b);
            });
            foreach (string number in displayedNumbers) {
                string name = ResolveName(number);
                bool group = IsGroupNumber(number);
                bool unknown = string.IsNullOrEmpty(name);
                string label = unknown ? "??? [" + number + "]" : name + " [" + number + "]";
                if (group && groupSizes.TryGetValue(number, out int size)) { label += "  (" + size + ")"; }
                bool hasUnread = unread.TryGetValue(number, out int cnt) && cnt > 0;
                var row = new GUITextBlock(new RectTransform(new Vector2(1f, 0.12f), contactListBox.Content.RectTransform),
                    label, textAlignment: Alignment.CenterLeft) { UserData = number, AutoScaleHorizontal = true };
                row.RectTransform.MinSize = new Point(0, RowHeightPx);
                row.Padding = new Vector4(AvatarSizePx + RowGapPx * 2, 0, hasUnread ? BadgeWidthPx + RowGapPx : RowGapPx, 0);
                if (hasUnread) { row.TextColor = Color.White; }
                Color avColor = group ? GroupAvatarColor : unknown ? UnknownAvatarColor : AvatarColorFor(number);
                new GUICustomComponent(new RectTransform(new Point(AvatarSizePx, AvatarSizePx), row.RectTransform, Anchor.CenterLeft)
                    { AbsoluteOffset = new Point(RowGapPx, 0) },
                    onDraw: (sb, comp) => DrawAvatar(sb, comp.Rect, avColor, group, unknown)) { CanBeFocused = false };
                if (hasUnread) {
                    int badgeCount = cnt;
                    new GUICustomComponent(new RectTransform(new Point(BadgeWidthPx, 18), row.RectTransform, Anchor.CenterRight)
                        { AbsoluteOffset = new Point(RowGapPx, 0) },
                        onDraw: (sb, comp) => DrawBadge(sb, comp.Rect, badgeCount)) { CanBeFocused = false };
                }
            }
            int cur = string.IsNullOrEmpty(currentPeer) ? -1 : displayedNumbers.IndexOf(currentPeer);
            if (cur >= 0) { suppressSel = true; contactListBox.Select(cur); suppressSel = false; }
        }
        private static Color AvatarColorFor(string number) {
            int h = 0;
            foreach (char ch in number) { h = h * 31 + ch; }
            return AvatarPalette[((h % AvatarPalette.Length) + AvatarPalette.Length) % AvatarPalette.Length];
        }
        private static void DrawAvatar(SpriteBatch sb, Rectangle rect, Color disc, bool group, bool unknown) {
            int d = Math.Min(rect.Width, rect.Height);
            var box = new Rectangle(rect.Center.X - d / 2, rect.Center.Y - d / 2, d, d);
            DrawRoundedRect(sb, box, disc, d / 2f);
            Color fg = Color.White;
            if (unknown) {
                DrawAvatarChar(sb, box, "?", fg);
            }
            else if (group) {
                DrawPersonHead(sb, box, fg, cyFrac: 0.34f, cxOffFrac: -0.17f, rFrac: 0.12f);
                DrawPersonHead(sb, box, fg, cyFrac: 0.34f, cxOffFrac: 0.17f, rFrac: 0.12f);
                DrawShoulders(sb, box, fg, halfWFrac: 0.37f, topFrac: 0.58f, botFrac: 0.94f, radFrac: 0.16f);
            }
            else {
                DrawPersonHead(sb, box, fg, cyFrac: 0.33f, cxOffFrac: 0f, rFrac: 0.17f);
                DrawShoulders(sb, box, fg, halfWFrac: 0.31f, topFrac: 0.57f, botFrac: 0.94f, radFrac: 0.20f);
            }
        }
        private static void DrawPersonHead(SpriteBatch sb, Rectangle box, Color c, float cyFrac, float cxOffFrac, float rFrac) {
            int r = Math.Max(1, (int)(box.Width * rFrac));
            int cx = box.Center.X + (int)(box.Width * cxOffFrac);
            int cy = box.Y + (int)(box.Width * cyFrac);
            DrawRoundedRect(sb, new Rectangle(cx - r, cy - r, r * 2, r * 2), c, r);
        }
        private static void DrawShoulders(SpriteBatch sb, Rectangle box, Color c, float halfWFrac, float topFrac, float botFrac, float radFrac) {
            int hw = (int)(box.Width * halfWFrac);
            int top = box.Y + (int)(box.Width * topFrac);
            int bot = box.Y + (int)(box.Width * botFrac);
            DrawRoundedRect(sb, new Rectangle(box.Center.X - hw, top, hw * 2, Math.Max(1, bot - top)), c, box.Width * radFrac);
        }
        private static void DrawAvatarChar(SpriteBatch sb, Rectangle box, string s, Color c) {
            GUIFont font = GUIStyle.SubHeadingFont;
            if (font == null) { return; }
            Vector2 baseSize = font.MeasureString(s);
            if (baseSize.Y <= 0f) { return; }
            float scale = box.Width * 0.5f / baseSize.Y;
            Vector2 sz = baseSize * scale;
            font.DrawString(sb, s, new Vector2(box.Center.X - sz.X / 2f, box.Center.Y - sz.Y / 2f),
                c, rotation: 0f, origin: Vector2.Zero, scale: scale, spriteEffects: SpriteEffects.None, layerDepth: 0f);
        }
        private static void DrawBadge(SpriteBatch sb, Rectangle rect, int count) {
            string s = count > 99 ? "99+" : count.ToString();
            DrawRoundedRect(sb, rect, BadgeColor, rect.Height / 2f);
            GUIFont font = GUIStyle.SubHeadingFont;
            if (font == null) { return; }
            Vector2 baseSize = font.MeasureString(s);
            if (baseSize.Y <= 0f || baseSize.X <= 0f) { return; }
            float scale = Math.Min(rect.Height * 0.6f / baseSize.Y, (rect.Width - 8) / baseSize.X);
            Vector2 sz = baseSize * scale;
            font.DrawString(sb, s, new Vector2(rect.Center.X - sz.X / 2f, rect.Center.Y - sz.Y / 2f),
                Color.White, rotation: 0f, origin: Vector2.Zero, scale: scale, spriteEffects: SpriteEffects.None, layerDepth: 0f);
        }
        private void SelectContact(string number) {
            if (string.IsNullOrEmpty(number)) { return; }
            currentPeer = number;
            unread.Remove(number);
            if (GameMain.Client != null) { item.CreateClientEvent(this, new HistReq(number)); }
            RefreshConversation();
            RefreshContacts();
        }
        private static Color BubbleColor(bool fromMe) => fromMe ? Color.White : new Color(180, 220, 255);
        private static readonly Color EmptyHintColor = new Color(120, 120, 120);
        private string FormatLine(bool fromMe, string stored) {
            int sep = stored.IndexOf(FieldSep);
            if (sep < 0) { return stored; }
            if (fromMe) { return stored.Substring(sep + 1); }
            string name = stored.Substring(0, sep);
            return string.IsNullOrEmpty(name) ? stored.Substring(sep + 1) : name + ": " + stored.Substring(sep + 1);
        }
        private void RefreshConversation(bool animateNewest = false) {
            if (conversationBox == null) { return; }
            conversationBox.Content.ClearChildren();
            messageAnims.Clear();
            if (string.IsNullOrEmpty(currentPeer) || !histories.TryGetValue(currentPeer, out var list) || list.Count == 0) {
                string hint = string.IsNullOrEmpty(currentPeer)
                    ? OrderText.Get("pp.order.messenger.hint_select_contact", "Выберите контакт слева")
                    : OrderText.Get("pp.order.messenger.hint_no_messages", "Пока нет сообщений");
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.25f), conversationBox.Content.RectTransform), hint,
                    textAlignment: Alignment.Center, wrap: true) { TextColor = EmptyHintColor, CanBeFocused = false };
                return;
            }
            var blocks = new List<GUITextBlock>(list.Count);
            foreach (var (fromMe, text) in list) {
                var wrapper = new GUIFrame(new RectTransform(new Vector2(1f, 0f), conversationBox.Content.RectTransform), style: null) { CanBeFocused = false };
                var block = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), wrapper.RectTransform), FormatLine(fromMe, text), wrap: true, textAlignment: fromMe ? Alignment.CenterRight : Alignment.CenterLeft) { TextColor = BubbleColor(fromMe), CanBeFocused = false };
                blocks.Add(block);
            }
            for (int pass = 0; pass < 4; pass++) {
                conversationBox.RecalculateChildren();
                bool changed = false;
                foreach (var block in blocks) {
                    int height = (int)Math.Ceiling(block.TextSize.Y) + MessagePadding;
                    if (block.RectTransform.MinSize.Y != height) { changed = true; }
                    block.RectTransform.MinSize = new Point(0, height);
                    block.RectTransform.Parent.MinSize = new Point(0, height);
                }
                if (!changed) { break; }
            }
            conversationBox.RecalculateChildren();
            ScrollConversationToBottom();
            if (animateNewest && blocks.Count > 0) {
                bool newestFromMe = list[list.Count - 1].fromMe;
                GUITextBlock newestBlock = blocks[blocks.Count - 1];
                Color newestColor = BubbleColor(newestFromMe);
                newestBlock.RectTransform.AbsoluteOffset = new Point(newestFromMe ? MessageSlideDistance : -MessageSlideDistance, 0);
                newestBlock.TextColor = newestColor * 0f;
                messageAnims.Add(new MsgAnim(newestBlock, Timing.TotalTime, newestFromMe, newestColor));
            }
        }
        private void UpdateMessageAnimations() {
            if (messageAnims.Count == 0) { return; }
            double now = Timing.TotalTime;
            for (int i = messageAnims.Count - 1; i >= 0; i--) {
                var anim = messageAnims[i];
                if (anim.Block?.RectTransform == null) { messageAnims.RemoveAt(i); continue; }
                float t = (float)Math.Clamp((now - anim.Start) / MessageAnimDuration, 0.0, 1.0);
                float eased = 1f - (1f - t) * (1f - t) * (1f - t);
                int startOffset = anim.FromMe ? MessageSlideDistance : -MessageSlideDistance;
                anim.Block.RectTransform.AbsoluteOffset = new Point((int)((1f - eased) * startOffset), 0);
                anim.Block.TextColor = anim.BaseColor * eased;
                if (t >= 1f) {
                    anim.Block.RectTransform.AbsoluteOffset = Point.Zero;
                    anim.Block.TextColor = anim.BaseColor;
                    messageAnims.RemoveAt(i);
                }
            }
        }
        private void ScrollConversationToBottom() {
            scrollTarget = 1f;
            scrollTargetValid = true;
        }
        private void UpdateConversationScroll(float deltaTime) {
            if (conversationBox?.ScrollBar == null || GuiFrame == null || !GuiFrame.Visible || messagesPanel == null || !messagesPanel.Visible) {
                scrollTargetValid = false;
                return;
            }
            if (!scrollTargetValid) { scrollTarget = conversationBox.ScrollBar.BarScroll; scrollTargetValid = true; }
            if (PlayerInput.ScrollWheelSpeed != 0 && conversationBox.Rect.Contains(PlayerInput.MousePosition.ToPoint())) {
                scrollTarget -= (PlayerInput.ScrollWheelSpeed / WheelScrollScale) * conversationBox.ScrollBar.UnclampedBarSize;
                scrollTarget = MathHelper.Clamp(scrollTarget, 0f, 1f);
            }
            float current = conversationBox.ScrollBar.BarScroll;
            if (MathUtils.NearlyEqual(current, scrollTarget, 0.0005f)) { return; }
            float lerpAmount = 1f - (float)Math.Exp(-ScrollSmoothingRate * deltaTime);
            conversationBox.ScrollBar.BarScroll = MathHelper.Lerp(current, scrollTarget, lerpAmount);
            conversationBox.RecalculateChildren();
        }
        private void SendCurrent() {
            if (inputBox == null || string.IsNullOrEmpty(currentPeer)) { return; }
            string text = Sanitize(inputBox.Text, MaxMessageLen);
            if (string.IsNullOrEmpty(text) || GameMain.Client == null) { inputBox.Text = ""; return; }
            double now = Timing.TotalTime;
            if (lastSendTime.TryGetValue(currentPeer, out double last) && now - last < SendCooldownSec) {
                sendRejectStart = now;
                return;
            }
            lastSendTime[currentPeer] = now;
            inputBox.Text = "";
            item.CreateClientEvent(this, new SendReq(currentPeer, text));
            PlaySent();
        }
        private void UpdateSendReject() {
            if (sendRejectStart < 0 || inputBox?.RectTransform == null) { return; }
            float t = (float)Math.Clamp((Timing.TotalTime - sendRejectStart) / SendRejectDuration, 0.0, 1.0);
            if (t >= 1f) {
                inputBox.RectTransform.AbsoluteOffset = Point.Zero;
                sendRejectStart = -1;
                return;
            }
            int dx = (int)(Math.Sin(t * MathHelper.TwoPi * 3f) * SendRejectShakePx * (1f - t));
            inputBox.RectTransform.AbsoluteOffset = new Point(dx, 0);
        }
        private void SaveContact() {
            string name = Sanitize(addNameBox?.Text, MaxNameLen);
            if (GameMain.Client == null) { return; }
            if (addFormGroupMode) {
                string members = addNumberBox?.Text ?? "";
                if (string.IsNullOrEmpty(NormalizeNumber(members))) { return; }
                item.CreateClientEvent(this, new GroupReq(name, members));
            }
            else {
                string num = NormalizeNumber(addNumberBox?.Text);
                if (string.IsNullOrEmpty(num)) { return; }
                item.CreateClientEvent(this, new ContactReq(num, name));
            }
            addForm.Visible = false;
        }
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData) {
            if (TryExtractEventData<SendReq>(extraData, out var s)) { msg.WriteByte((byte)1); msg.WriteString(s.To); msg.WriteString(s.Text); }
            else if (TryExtractEventData<ContactReq>(extraData, out var c)) { msg.WriteByte((byte)2); msg.WriteString(c.Num); msg.WriteString(c.Name); }
            else if (TryExtractEventData<OpenReq>(extraData, out _)) { msg.WriteByte((byte)3); }
            else if (TryExtractEventData<HistReq>(extraData, out var h)) { msg.WriteByte((byte)4); msg.WriteString(h.Peer); }
            else if (TryExtractEventData<GroupReq>(extraData, out var g)) { msg.WriteByte((byte)5); msg.WriteString(g.Name); msg.WriteString(g.Members); }
        }
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            byte op = msg.ReadByte();
            switch (op) {
                case 1: {
                    byte role = msg.ReadByte();
                    if (role == 2) { return; }
                    msg.ReadString();
                    string peer = msg.ReadString(), text = msg.ReadString();
                    RouteDeliver(peer, role == 1, text);
                    break;
                }
                case 2: {
                    if (!msg.ReadBoolean()) { return; }
                    DeviceNumber = msg.ReadString();
                    ContactsData = msg.ReadString();
                    RefreshHeader();
                    RefreshContacts();
                    break;
                }
                case 3: {
                    if (!msg.ReadBoolean()) { return; }
                    int n = msg.ReadUInt16();
                    directory.Clear();
                    groupSizes.Clear();
                    for (int i = 0; i < n; i++) {
                        string num = msg.ReadString(), name = msg.ReadString();
                        int cnt = msg.ReadUInt16();
                        directory[num] = name;
                        if (cnt > 0) { groupSizes[num] = cnt; }
                    }
                    RefreshContacts();
                    break;
                }
                case 4: {
                    if (!msg.ReadBoolean()) { return; }
                    string peer = msg.ReadString();
                    int n = msg.ReadUInt16();
                    var list = new List<(bool, string)>(n);
                    for (int i = 0; i < n; i++) { bool fromMe = msg.ReadBoolean(); list.Add((fromMe, msg.ReadString())); }
                    histories[peer] = list;
                    if (currentPeer == peer) { RefreshConversation(); }
                    RefreshContacts();
                    break;
                }
            }
        }
        private void RouteDeliver(string peer, bool fromMe, string text) {
            if (!histories.TryGetValue(peer, out var list)) { histories[peer] = list = new List<(bool, string)>(); }
            list.Add((fromMe, text));
            if (list.Count > MaxHistoryPerPair) { list.RemoveRange(0, list.Count - MaxHistoryPerPair); }
            lastActivity[peer] = Timing.TotalTime;
            if (!fromMe) {
                PlayNotify();
                if (currentPeer == peer && GuiFrame is { Visible: true }) { unread.Remove(peer); }
                else { unread[peer] = unread.GetValueOrDefault(peer) + 1; nextSlotFlashTime = 0; }
            }
            if (currentPeer == peer) { RefreshConversation(animateNewest: true); }
            RefreshContacts();
        }
        private void PlayIfLoaded(string file, float volume, float range) {
            string dir = PrimaryPackage.Core.ModAssets.Root;
            if (string.IsNullOrEmpty(dir)) { return; }
            try {
                string p = Path.Combine(dir, file);
                if (!File.Exists(p)) { return; }
                Sound sound = PrimaryPackage.Core.SoundStore.Absolute(p);
                if (sound != null) { SoundPlayer.PlaySound(sound, item.WorldPosition, volume: volume, range: range); }
            }
            catch (Exception e) { DebugConsole.ThrowError("pda sound does not load", e); }
        }
        private void PlayNotify() => PlayIfLoaded(NotifySoundFile, 0.8f, 450f);
        private void PlaySent() => PlayIfLoaded(SentSoundFile, 0.5f, 400f);
        private void PlayOpen() => PlayIfLoaded(OpenSoundFile, 0.6f, 400f);
    }
}
