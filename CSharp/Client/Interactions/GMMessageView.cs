using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;
using TextInput = PrimaryPackage.GUI.TextInput;

namespace PrimaryPackage.Interactions;

internal sealed class GMColorPalette : PrimaryPackage.GUI.GUIComponent {
    private float hue;
    internal event Action<Color> Changed;
    public override bool Interactive => true;
    internal GMColorPalette() {
        Height = 155;
        Activated += _ => Pick(GUIRuntime.Instance.Input.Pointer);
        Dragged += (_, pointer, _) => Pick(pointer);
    }
    private void Pick(Vector2 point) {
        float x = Math.Clamp((point.X - Bounds.X) / Math.Max(1, Bounds.Width), 0, 1);
        float y = Math.Clamp((point.Y - Bounds.Y) / Math.Max(1, Bounds.Height), 0, 1);
        if (y > .82f) { hue = x; Invalidate(GUIDirty.Render); }
        else { Changed?.Invoke(Hsv(hue, x, 1 - y / .8f)); }
    }
    internal override void Navigate(int direction) { hue = (hue + direction / 36f + 1) % 1; Invalidate(GUIDirty.Render); Changed?.Invoke(Hsv(hue, 1, 1)); }
    internal override void Paint(PrimaryPackage.GUI.GUIRenderer renderer, float opacity) {
        for (int y = 0; y < 16; y++) {
            for (int x = 0; x < 24; x++) {
                int left = Bounds.X + Bounds.Width * x / 24, right = Bounds.X + Bounds.Width * (x + 1) / 24;
                int top = Bounds.Y + (int)(Bounds.Height * .8f * y / 16), bottom = Bounds.Y + (int)(Bounds.Height * .8f * (y + 1) / 16);
                renderer.Fill(new Rectangle(left, top, right - left, bottom - top), Hsv(hue, x / 23f, 1 - y / 15f), opacity);
            }
        }
        for (int x = 0; x < 36; x++) { renderer.Fill(new Rectangle(Bounds.X + Bounds.Width * x / 36, Bounds.Y + (int)(Bounds.Height * .85f), Bounds.Width / 36 + 1, (int)(Bounds.Height * .15f)), Hsv(x / 36f, 1, 1), opacity); }
        renderer.Border(Bounds, Hovered || Pressed ? GUITheme.Accent : GUITheme.Border, opacity);
    }
    private static Color Hsv(float h, float s, float v) {
        float position = h * 6; int sector = (int)position; float fraction = position - sector;
        float p = v * (1 - s), q = v * (1 - s * fraction), t = v * (1 - s * (1 - fraction));
        return (sector % 6) switch { 0 => new Color(v, t, p), 1 => new Color(q, v, p), 2 => new Color(p, v, t), 3 => new Color(p, q, v), 4 => new Color(t, p, v), _ => new Color(v, p, q) };
    }
    public override void Dispose() { Changed = null; base.Dispose(); }
}

internal sealed partial class InteractionViews {
    private GUIWindow gmMessageWindow;
    private AtomicStore<List<uint>> gmColorsStore;
    private List<uint> gmColors;
    private Text gmMessageStatus;
    private VirtualList gmMessageHistory;
    private Guid gmHistorySelection;
    internal void RefreshAnnouncementFormats() {
        if (gmMessageWindow is not { Disposed: false, Closing: false }) { return; }
        if (client.Allows(InteractionAction.SendGMMessage)) { OpenGMMessage(); }
        else { gmMessageWindow.Close(); }
    }
    internal void ToggleGMMessage() {
        if (gmMessageWindow is { Disposed: false, Closing: false }) { gmMessageWindow.Close(); return; }
        OpenGMMessage();
    }
    private void OpenGMMessage() {
        if (!client.Allows(InteractionAction.SendGMMessage)) { Error("Нет разрешения отправлять GM-сообщения"); return; }
        try { client.EnsureAnnouncementRecovery(); }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException) { Error(error.Message); return; }
        gmMessageWindow?.Dispose(); gmMessageWindow = OpenCentered("Сообщение игрокам · F5", 610, 780);
        gmMessageWindow.ClosingRequested += _ => client.SaveAnnouncements(true);
        GMMessageDraft draft = client.AnnouncementState.Draft;
        var content = gmMessageWindow.Add(new ScrollView { FillHeight = true });
        var peers = client.Peers.ToArray();
        string PeerName(string account) => Array.Find(peers, p => p.Account == account)?.Name ?? "Отключённый игрок · " + account;
        var targets = new List<string> { "" }; var names = new List<string> { "Всем игрокам" };
        foreach (var peer in peers) { targets.Add(peer.Account); names.Add(peer.Name); }
        if (!targets.Contains(draft.Target)) { targets.Add(draft.Target); names.Add(PeerName(draft.Target)); }
        content.Add(new Text("Получатель"));
        var recipient = content.Add(new Dropdown(names.ToArray())); recipient.Select(targets.IndexOf(draft.Target));
        recipient.SelectionChanged += i => { draft.Target = targets[i]; client.ChangeAnnouncementDraft(); };
        byte[] allFormats = { GMAnnouncement.PlainMsg, 3, 11, 10, 7, GMAnnouncement.Toast };
        string[] allLabels = { "Обычный msg · серверный чат без отправителя", "Серверный чат · с отправителем", "Сообщение в игре · тип 11", "Серверное окно · тип 10", "Окно сообщения · тип 7", "Сценарная надпись внизу" };
        var available = new List<byte>(allFormats.Length); var formatLabels = new List<string>(allFormats.Length);
        for (int i = 0; i < allFormats.Length; i++) {
            bool allowed = client.AllowsAnnouncement(allFormats[i]);
            if (allowed || draft.Format == allFormats[i]) { available.Add(allFormats[i]); formatLabels.Add((allowed ? "" : "Недоступно · ") + allLabels[i]); }
        }
        byte[] formats = available.ToArray();
        var format = content.Add(new Dropdown(formatLabels.ToArray()));
        format.Select(Array.IndexOf(formats, draft.Format));
        var senderSection = content.Add(new Panel { Height = 130 });
        senderSection.Add(new Text("Отправитель"));
        var sender = senderSection.Add(new Dropdown(new[] { "Без имени", "GM", "Имя сервера", "Моё имя", "Выбранный игрок", "Другое имя" }));
        sender.Select((int)draft.Sender);
        var senderAccounts = new List<string> { "" }; var senderNames = new List<string> { "Выберите игрока" };
        foreach (var peer in peers) { senderAccounts.Add(peer.Account); senderNames.Add(peer.Name); }
        if (!senderAccounts.Contains(draft.SenderAccount)) { senderAccounts.Add(draft.SenderAccount); senderNames.Add(PeerName(draft.SenderAccount)); }
        var senderPlayer = senderSection.Add(new Dropdown(senderNames.ToArray())); senderPlayer.Select(senderAccounts.IndexOf(draft.SenderAccount));
        var customSender = senderSection.Add(new TextInput(draft.CustomSender, 64) { Placeholder = "Имя отправителя" });
        var senderHint = senderSection.Add(new Text("В окнах сообщений отправитель показывается первой строкой") { Wrap = true, Height = 36 });
        void UpdateSender() { senderSection.Visible = draft.Format is not (GMAnnouncement.PlainMsg or GMAnnouncement.Toast); senderPlayer.Visible = draft.Sender == GMMessageSender.Player; customSender.Visible = draft.Sender == GMMessageSender.Custom; senderHint.Visible = draft.Format is 7 or 10 or 11; }
        sender.SelectionChanged += i => { draft.Sender = (GMMessageSender)i; UpdateSender(); client.ChangeAnnouncementDraft(); };
        senderPlayer.SelectionChanged += i => { draft.SenderAccount = senderAccounts[i]; client.ChangeAnnouncementDraft(); };
        customSender.Changed += input => { draft.CustomSender = input.Text; client.ChangeAnnouncementDraft(); };
        content.Add(new Text("Текст · до 200 символов, нижняя надпись — до 512") { Wrap = true, Height = 36 });
        var text = content.Add(new TextInput(draft.Text, 512) { Placeholder = "Текст сообщения" });
        var preview = content.Add(new Text(draft.Text.Length > 0 ? draft.Text : "Предварительный просмотр цвета") { Wrap = true, Height = 45 });
        var rgba = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 28 });
        string[] values = { draft.R, draft.G, draft.B, draft.A }, labels = { "R", "G", "B", "A" };
        TextInput[] channels = new TextInput[4];
        for (int i = 0; i < channels.Length; i++) { channels[i] = rgba.Add(new TextInput(values[i], 3) { Width = 125, Placeholder = labels[i] }); }
        Color ReadColor() {
            Span<byte> components = stackalloc byte[4];
            for (int i = 0; i < channels.Length; i++) { if (!byte.TryParse(channels[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out components[i])) { throw new ArgumentException("RGBA: введите целые числа от 0 до 255"); } }
            return new Color(components[0], components[1], components[2], components[3]);
        }
        void CaptureColor() { draft.R = channels[0].Text; draft.G = channels[1].Text; draft.B = channels[2].Text; draft.A = channels[3].Text; client.ChangeAnnouncementDraft(); }
        void PreviewColor() {
            if (byte.TryParse(draft.R, NumberStyles.None, CultureInfo.InvariantCulture, out byte r) && byte.TryParse(draft.G, NumberStyles.None, CultureInfo.InvariantCulture, out byte g) &&
                byte.TryParse(draft.B, NumberStyles.None, CultureInfo.InvariantCulture, out byte b) && byte.TryParse(draft.A, NumberStyles.None, CultureInfo.InvariantCulture, out byte a)) {
                preview.Color = new Color(r, g, b, a); preview.Invalidate(GUIDirty.Render);
            }
        }
        void SetColor(Color color) {
            channels[0].Text = color.R.ToString(CultureInfo.InvariantCulture); channels[1].Text = color.G.ToString(CultureInfo.InvariantCulture);
            channels[2].Text = color.B.ToString(CultureInfo.InvariantCulture); channels[3].Text = color.A.ToString(CultureInfo.InvariantCulture);
            CaptureColor(); PreviewColor();
        }
        foreach (var channel in channels) { channel.Changed += _ => { CaptureColor(); PreviewColor(); }; }
        text.Changed += input => { draft.Text = input.Text; preview.Text = input.Text.Length > 0 ? input.Text : "Предварительный просмотр цвета"; client.ChangeAnnouncementDraft(); };
        PreviewColor();
        var palette = content.Add(new GMColorPalette()); palette.Changed += color => SceneTry(() => {
            if (!byte.TryParse(channels[3].Text, NumberStyles.None, CultureInfo.InvariantCulture, out byte alpha)) { throw new ArgumentException("A: введите прозрачность от 0 до 255"); }
            color.A = alpha; SetColor(color);
        });
        content.Add(new Text("Оттенок внизу · насыщенность и яркость сверху · RGBA: 0–255") { Wrap = true, Height = 36 });
        if (gmColorsStore == null) {
            var store = new AtomicStore<List<uint>>(Core.ModAssets.Resolve("Data/Interactions/gm-colors.client.json"), colors => { if (colors == null || colors.Count > 16) { throw new System.IO.InvalidDataException("Недопустимая палитра"); } });
            try {
                gmColors = store.Load(() => new List<uint>(), error => Core.ModLog.Error("Палитра восстановлена: " + error.Message)); gmColorsStore = store;
            } catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) {
                gmMessageWindow.Close(); Error("Не удалось прочитать сохранённые цвета: " + error.Message); return;
            }
        }
        var saved = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        void RebuildSaved() {
            saved.Clear();
            for (int i = 0; i < gmColors.Count; i++) { uint packed = gmColors[i]; saved.Add(new Button((i + 1).ToString(), _ => SetColor(new Color(packed))) { Width = 27 }); }
        }
        RebuildSaved(); content.Add(new Button("Сохранить текущий цвет", _ => SceneTry(() => {
            uint packed = ReadColor().PackedValue;
            if (gmColors.Contains(packed)) { return; }
            var next = new List<uint>(gmColors); if (next.Count == 16) { next.RemoveAt(0); } next.Add(packed);
            gmColorsStore.Save(next); gmColors = next; RebuildSaved();
        })));
        var times = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 28 });
        var hold = times.Add(new TextInput(draft.Hold, 12) { Width = 170, Placeholder = "Длительность" });
        var fadeIn = times.Add(new TextInput(draft.FadeIn, 12) { Width = 170, Placeholder = "Появление" });
        var fadeOut = times.Add(new TextInput(draft.FadeOut, 12) { Width = 170, Placeholder = "Исчезновение" });
        hold.Changed += input => { draft.Hold = input.Text; client.ChangeAnnouncementDraft(); };
        fadeIn.Changed += input => { draft.FadeIn = input.Text; client.ChangeAnnouncementDraft(); };
        fadeOut.Changed += input => { draft.FadeOut = input.Text; client.ChangeAnnouncementDraft(); };
        var timingHint = content.Add(new Text("Длительность · появление · исчезновение, с") { Wrap = true, Height = 36 });
        void UpdateFormat() { bool toast = draft.Format == GMAnnouncement.Toast; times.Visible = timingHint.Visible = toast; UpdateSender(); }
        format.SelectionChanged += i => { draft.Format = formats[i]; UpdateFormat(); client.ChangeAnnouncementDraft(); }; UpdateFormat();
        content.Add(new Text("Подтверждённые сообщения · выберите для повтора") { Color = GUITheme.Accent });
        gmMessageHistory = content.Add(new VirtualList(i => {
            var entry = client.AnnouncementState.History[client.AnnouncementState.History.Count - 1 - i];
            return DateTimeOffset.FromUnixTimeMilliseconds(entry.Receipt.Timestamp).ToLocalTime().ToString("HH:mm") + " · " + (entry.Receipt.Sender.Length > 0 ? entry.Receipt.Sender + ": " : "") + entry.Message.Text;
        }, 110) { Count = client.AnnouncementState.History.Count });
        gmMessageHistory.SelectionChanged += i => gmHistorySelection = client.AnnouncementState.History[client.AnnouncementState.History.Count - 1 - i].Message.Id;
        var historyActions = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        historyActions.Add(new Button("В черновик", _ => SceneTry(() => {
            client.AnnouncementState.Draft = GMMessageDraft.From(SelectedGMMessage()); client.ChangeAnnouncementDraft(); OpenGMMessage();
        })));
        historyActions.Add(new Button("Повторить выбранное", _ => SceneTry(() => {
            var value = SelectedGMMessage().Copy(); value.Id = Guid.NewGuid(); client.Announce(value, false);
        })));
        gmMessageStatus = gmMessageWindow.Add(new Text(client.AnnouncementStatus) { Wrap = true, Height = 42 });
        var actions = gmMessageWindow.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        actions.Add(new Button("Предпросмотр", _ => SceneTry(() => client.Announce(draft.Create(), true))));
        actions.Add(new Button("Отправить", _ => SceneTry(() => client.Announce(draft.Create(), false))));
        GMAnnouncementsChanged();
    }
    private GMAnnouncement SelectedGMMessage() {
        foreach (var entry in client.AnnouncementState.History) { if (entry.Message.Id == gmHistorySelection) { return entry.Message; } }
        throw new InvalidOperationException("Выберите сообщение из истории");
    }
    private void GMAnnouncementsChanged() {
        if (gmMessageStatus is { Disposed: false }) { gmMessageStatus.Text = client.AnnouncementStatus; }
        if (gmMessageHistory is { Disposed: false }) {
            var history = client.AnnouncementState.History;
            gmMessageHistory.Count = history.Count;
            int selected = 0;
            for (int i = history.Count - 1; i >= 0; i--) { if (history[i].Message.Id == gmHistorySelection) { selected = history.Count - 1 - i; break; } }
            if (history.Count > 0) { gmMessageHistory.Select(selected); }
        }
    }
}
