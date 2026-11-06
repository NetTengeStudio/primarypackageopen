using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;
using TextInput = PrimaryPackage.GUI.TextInput;

namespace PrimaryPackage.Interactions;

internal sealed partial class InteractionViews {
    private bool messagesDirty;
    private readonly Dictionary<(string From, string Id), (MessageStatus Status, string Header, string Key)> headerCache = new();
    private Transcript transcript;
    private VirtualList peersList;
    private Text conversationTitle, connectionStatus;
    private TextInput composer, peerSearch;
    private Panel channelNavigation, composerActions;
    private MessageKind channel;
    private readonly List<(string Account, string Name, int Unread)> conversations = new();
    private readonly Dictionary<string, string> drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenMessages = new(StringComparer.Ordinal);
    private readonly List<(string Header, string Body)> transcriptRows = new();
    internal void BindRecovery(InteractionRecovery recovery, bool preserveCurrent = false) {
        if (preserveCurrent && recovery != null) {
            int merged = drafts.Count;
            foreach (string key in recovery.Data.Drafts.Keys) { if (!drafts.ContainsKey(key) && ++merged > 256) { throw new System.IO.InvalidDataException("Не удалось объединить черновики: превышен предел 256. Существующие данные сохранены"); } }
            merged = seenMessages.Count;
            foreach (string id in recovery.Data.Seen) { if (!seenMessages.Contains(id) && ++merged > 10000) { throw new System.IO.InvalidDataException("Не удалось объединить квитанции: превышен предел 10000. Существующие данные сохранены"); } }
        }
        if (!preserveCurrent) { drafts.Clear(); seenMessages.Clear(); }
        headerCache.Clear();
        if (recovery == null) { return; }
        foreach (var entry in recovery.Data.Drafts) { drafts.TryAdd(entry.Key, entry.Value); }
        foreach (string id in recovery.Data.Seen) { seenMessages.Add(id); }
    }
    private void SaveInteractionView() {
        if (composer is { Disposed: false }) { drafts[DraftKey] = composer.Text; }
        try { client.Recovery?.SaveViews(drafts, seenMessages); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { Core.ModLog.Error("Не удалось сохранить черновики: " + error.Message); }
    }
    public void ToggleInteraction() {
        if (messageWindow is { Disposed: false, Closing: false }) { messageWindow.Close(); return; }
        OpenMessages(null, "");
    }
    public void OpenMessages(string target, string name) {
        if (messageWindow is { Disposed: false }) {
            messageWindow.Reopen();
            GUIRuntime.Instance.Windows.BringToFront(messageWindow);
            if (target != null) { SetConversation(MessageKind.Private, target, name); }
            return;
        }
        messageWindow?.Dispose();
        messageWindow = OpenCentered("Взаимодействие", 940, 620);
        messageWindow.ClosingRequested += _ => SaveInteractionView();
        var navigation = messageWindow.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        channelNavigation = navigation;
        navigation.Add(new Button("Личные", _ => SetConversation(MessageKind.Private, "", "Выберите собеседника")));
        navigation.Add(new Button("Обращения к GM", _ => SetConversation(MessageKind.Support, client.Allows(InteractionAction.ViewGMFeed) ? "" : client.Account, "Обращение к игровым мастерам")));
        if (client.Allows(InteractionAction.ViewGMFeed)) { navigation.Add(new Button("RP-события", _ => SetConversation(MessageKind.Roleplay, "", "RP-события · все игроки"))); }
        var body = messageWindow.Add(new Panel { Flow = GUIFlow.Horizontal, FillHeight = true });
        var sidebar = body.Add(new Panel { Width = 240, FillHeight = true });
        sidebar.Add(new Text("Игроки и переписки") { Color = GUITheme.Accent });
        peerSearch = sidebar.Add(new TextInput("", 80) { Placeholder = "Найти игрока" });
        peerSearch.Changed += _ => RefreshConversations();
        peersList = sidebar.Add(new VirtualList(i => conversations[i].Name + (conversations[i].Unread > 0 ? "  (" + conversations[i].Unread + ")" : "")) { FillHeight = true });
        peersList.SelectionChanged += i => SetConversation(channel, conversations[i].Account, conversations[i].Name);
        var chat = body.Add(new Panel { FillHeight = true });
        conversationTitle = chat.Add(new Text("Выберите собеседника") { Color = GUITheme.Accent });
        transcript = chat.Add(new Transcript());
        composer = chat.Add(new TextInput("", InteractionProtocol.MaxMessageLength) { Height = 32, Placeholder = "Написать сообщение" });
        composer.Changed += input => { if (input.Text.Length == 0) { drafts.Remove(DraftKey); } else { drafts[DraftKey] = input.Text; } };
        composer.Submitted += _ => SendComposed();
        var actions = chat.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        composerActions = actions;
        actions.Add(new Button("Отправить", _ => SendComposed()));
        if (client.Allows(InteractionAction.ViewGMFeed)) {
            actions.Add(new Button("Уточните детали", _ => { composer.Text = "Опишите, пожалуйста, что произошло и где вы находитесь."; GUIRuntime.Instance.Input.SetFocus(composer); }));
            actions.Add(new Button("Принято", _ => { composer.Text = "Принято. Разбираюсь в ситуации."; GUIRuntime.Instance.Input.SetFocus(composer); }));
        }
        connectionStatus = messageWindow.Add(new Text(client.Connected ? "Enter: отправить · F2: закрыть" : "Подключение к серверу…") { Color = GUITheme.Muted });
        SetConversation(target != null ? MessageKind.Private : channel, target ?? conversation, name.Length > 0 ? name : "Выберите собеседника", saveDraft: false);
    }
    private string DraftKey => ((byte)channel).ToString(CultureInfo.InvariantCulture) + ":" + conversation;
    private void SetConversation(MessageKind kind, string account, string name, bool saveDraft = true) {
        if (saveDraft) { SaveInteractionView(); }
        channel = kind; conversation = account;
        if (conversationTitle == null || conversationTitle.Disposed) { return; }
        conversationTitle.Text = kind == MessageKind.Roleplay ? "RP-события · все игроки" : kind == MessageKind.Support ? "Обращение · " + name : name;
        composer.Text = drafts.TryGetValue(DraftKey, out string draft) ? draft : "";
        composer.Visible = kind != MessageKind.Roleplay; composerActions.Visible = composer.Visible;
        for (int i = 0; i < channelNavigation.Children.Count; i++) { var button = (Button)channelNavigation.Children[i]; button.Selected = i == (int)kind; button.Invalidate(GUIDirty.Style); }
        composer.Enabled = client.Connected && (kind == MessageKind.Support ? conversation.Length > 0 : conversation.Length > 0 && conversation != client.Account);
        RefreshMessages();
        transcript.Follow();
    }
    private void RefreshConversations() {
        if (peersList == null || peersList.Disposed) { return; }
        var peers = new Dictionary<string, (string Name, int Unread)>(StringComparer.Ordinal);
        if (channel == MessageKind.Private) { foreach (var peer in client.Peers) { if (peer.Account != client.Account && peer.Account.Length > 0) { peers[peer.Account] = (peer.Name, 0); } } }
        else if (channel == MessageKind.Support && !client.Allows(InteractionAction.ViewGMFeed)) { peers[client.Account] = ("Моё обращение", 0); }
        foreach (var entry in client.Messages) {
            if (entry.Kind != channel) { continue; }
            string other = channel != MessageKind.Private ? entry.Thread : entry.From == client.Account ? entry.To : entry.From;
            string display = channel != MessageKind.Private ? entry.From == entry.Thread ? entry.SenderName : entry.RecipientName : entry.From == client.Account ? entry.RecipientName : entry.SenderName;
            peers.TryGetValue(other, out var previous);
            int unread = previous.Unread + (entry.From != client.Account && !seenMessages.Contains(entry.From + ":" + entry.Id) && (channel != MessageKind.Private || entry.Status < MessageStatus.Read) ? 1 : 0);
            peers[other] = (previous.Name ?? display, unread);
        }
        conversations.Clear();
        string search = peerSearch?.Text ?? "";
        foreach (var peer in peers) { if (peer.Value.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0) { conversations.Add((peer.Key, peer.Value.Name, peer.Value.Unread)); } }
        conversations.Sort((a, b) => { int order = b.Unread.CompareTo(a.Unread); return order == 0 ? string.Compare(a.Name, b.Name, StringComparison.CurrentCulture) : order; });
        peersList.Count = conversations.Count;
    }
    private void SendComposed() {
        if (channel == MessageKind.Roleplay || composer == null || !composer.Enabled) { return; }
        try {
            if (channel == MessageKind.Private) { client.Queue(conversation, conversationTitle.Text, composer.Text); }
            else { client.QueueMessage(conversation == client.Account ? "gm" : conversation, "Игровые мастера", composer.Text, MessageKind.Support, conversation); }
            composer.Text = ""; drafts.Remove(DraftKey);
            transcript.Follow();
        } catch (InvalidOperationException error) { Error(error.Message); }
    }
    private static string Status(MessageStatus status) => status switch { MessageStatus.Queued => "в очереди", MessageStatus.Stored => "сервер подтвердил", MessageStatus.Delivered => "доставлено", MessageStatus.Read => "прочитано", _ => "" };
    private void MessagesChanged() => messagesDirty = true;
    private void RefreshMessages() {
        messagesDirty = false;
        if (messageWindow == null || messageWindow.Disposed || messageWindow.Closing || transcript == null) { return; }
        history.Clear();
        foreach (var entry in client.Messages) {
            if (entry.Kind == channel && (channel == MessageKind.Private ? entry.From == conversation || entry.To == conversation : entry.Thread == conversation || channel == MessageKind.Roleplay && conversation.Length == 0)) { history.Add(entry); }
        }
        history.Sort((a, b) => {
            if (a.Sequence == 0 || b.Sequence == 0) { return a.Sequence == b.Sequence ? a.Timestamp.CompareTo(b.Timestamp) : a.Sequence == 0 ? 1 : -1; }
            return a.Sequence.CompareTo(b.Sequence);
        });
        transcriptRows.Clear();
        foreach (var entry in history) {
            var key = (entry.From, entry.Id);
            if (!headerCache.TryGetValue(key, out var cached) || cached.Status != entry.Status) {
                if (headerCache.Count >= 2000) { headerCache.Clear(); }
                cached = (entry.Status, DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("HH:mm") + "  " + entry.SenderName + " · " + Status(entry.Status), entry.From + ":" + entry.Id);
                headerCache[key] = cached;
            }
            transcriptRows.Add((cached.Header, entry.Text));
            seenMessages.Add(cached.Key);
            if (channel == MessageKind.Private) { client.Read(entry); }
        }
        transcript.Replace(transcriptRows);
        RefreshConversations();
        connectionStatus.Text = client.Connected ? "Enter: отправить · F2: закрыть" : "Подключение к серверу…";
    }
}
