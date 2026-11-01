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
    private GUIWindow director;
    private VirtualList activeEffectsList;
    private void EffectsChanged() { if (activeEffectsList is { Disposed: false }) { activeEffectsList.Count = client.Effects.Count; } }
    private readonly List<DirectorEvent> sequence = new();
    public sealed class SavedScene {
        public string Name { get; set; } = "";
        public List<DirectorEvent> Events { get; set; } = new();
    }
    private AtomicStore<List<SavedScene>> sceneStore;
    private List<SavedScene> savedScenes;
    private static void ValidateScenes(List<SavedScene> values) {
        if (values == null || values.Count > 32) { throw new System.IO.InvalidDataException("Invalid director scene store"); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in values) {
            if (scene == null || !InteractionProtocol.ValidText(scene.Name, 64) || !names.Add(scene.Name) || scene.Events == null || scene.Events.Count is < 1 or > 16) { throw new System.IO.InvalidDataException("Invalid director scene"); }
            foreach (var entry in scene.Events) { DirectorEvent.Validate(entry); }
        }
    }
    internal void BindRecovery(InteractionRecovery recovery) {
        drafts.Clear(); seenMessages.Clear(); headerCache.Clear();
        foreach (var entry in recovery.Data.Drafts) { drafts.Add(entry.Key, entry.Value); }
        foreach (string id in recovery.Data.Seen) { seenMessages.Add(id); }
    }
    private void SaveInteractionView() {
        if (composer is { Disposed: false }) { drafts[DraftKey] = composer.Text; }
        try { client.Recovery?.SaveViews(drafts, seenMessages); } catch (System.IO.IOException error) { Core.ModLog.Error("Не удалось сохранить черновики: " + error.Message); }
    }
    public void ToggleInteraction() {
        if (messageWindow is { Disposed: false, Closing: false }) { messageWindow.Close(); return; }
        OpenMessages(null, "");
    }
    public void OpenMessages(string target, string name) {
        hub?.Close();
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
    private void Confirm(string title, Action action) {
        var window = OpenCentered(title, 480, 150);
        window.Add(new Text("Действие необратимо. Проверьте выбранный объект.") { Wrap = true, Height = 48 });
        var buttons = window.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        buttons.Add(new Button("Отмена", _ => window.Close()));
        buttons.Add(new Button("Подтвердить", _ => { try { action(); window.Close(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Error(error.Message); } }));
    }
    private void OpenDirector() {
        if (sceneStore == null) {
            sceneStore = new AtomicStore<List<SavedScene>>(Core.ModAssets.Resolve("Data/Interactions/director-scenes.client.json"), ValidateScenes);
            try { savedScenes = sceneStore.Load(() => new List<SavedScene>(), error => Core.ModLog.Error("Director scenes recovered: " + error.Message)); }
            catch (Exception error) when (error is System.IO.IOException or ArgumentException or System.Text.Json.JsonException) { sceneStore = null; Error("Не удалось прочитать сценарии: " + error.Message); return; }
        }
        hub?.Close(); director?.Dispose();
        director = OpenCentered("Horror Director", 900, 640);
        var tabs = director.Add(new Tabs { FillHeight = true });
        var effects = tabs.AddTab("Персональные эффекты");
        var scenario = tabs.AddTab("Сценарный текст");
        var monsters = tabs.AddTab("Существа");
        var sequences = tabs.AddTab("Последовательности");
        var activeEffects = tabs.AddTab("Активные");
        activeEffects.Add(new Text("Снимок событий сервера · обновите после действий другого GM") { Color = GUITheme.Muted });
        activeEffectsList = activeEffects.Add(new VirtualList(i => {
            var value = client.Effects[i];
            string targetName = value.Target.Length == 0 ? "все игроки" : client.Peers.Find(p => p.Account == value.Target)?.Name ?? "игрок отключился";
            return value.Name + " / " + targetName + " / " + value.Remaining.ToString("F1") + " с";
        }, 300) { FillHeight = true, Count = client.Effects.Count });
        activeEffects.Add(new Button("Обновить состояние", _ => client.RequestEffects()));
        activeEffects.Add(new Button("Остановить выбранное", _ => {
            int index = activeEffectsList.SelectedIndex;
            if ((uint)index < (uint)client.Effects.Count) { client.Direct(new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.StopOne, Resource = client.Effects[index].Id }); }
        }));
        client.RequestEffects();
        sequences.Add(new Text("События запускаются с указанными задержками от общего момента старта"));
        var queued = sequences.Add(new VirtualList(i => DirectorName(sequence[i].Kind) + " / " + sequence[i].Delay.ToString("F1") + " с / " + (client.Peers.Find(p => p.Account == sequence[i].Target)?.Name ?? "все игроки"), 180) { FillHeight = true, Count = sequence.Count });
        sequences.Add(new Button("Убрать выбранное событие", _ => { if ((uint)queued.SelectedIndex < (uint)sequence.Count) { sequence.RemoveAt(queued.SelectedIndex); queued.Count = sequence.Count; } }));
        sequences.Add(new Button("Запустить последовательность", _ => { try { foreach (var value in sequence) { value.Id = Guid.NewGuid().ToString("N"); } client.DirectSequence(sequence); } catch (ArgumentException error) { Error(error.Message); } }));
        var sceneName = sequences.Add(new TextInput("", 64) { Placeholder = "Название сценария" });
        var presets = sequences.Add(new VirtualList(i => savedScenes[i].Name, 110) { Count = savedScenes.Count });
        presets.SelectionChanged += i => { sequence.Clear(); foreach (var value in savedScenes[i].Events) { sequence.Add(CloneEvent(value)); } queued.Count = sequence.Count; sceneName.Text = savedScenes[i].Name; };
        sequences.Add(new Button("Сохранить сценарий", _ => {
            try {
                var next = new List<SavedScene>(savedScenes); next.RemoveAll(s => s.Name == sceneName.Text);
                next.Add(new SavedScene { Name = sceneName.Text, Events = sequence.Select(CloneEvent).ToList() });
                sceneStore.Save(next); savedScenes = next; presets.Count = savedScenes.Count;
            } catch (Exception error) when (error is ArgumentException or System.IO.IOException) { Error(error.Message); }
        }));
        var creatures = CharacterPrefab.Prefabs.Where(p => p.Identifier != CharacterPrefab.HumanSpeciesName).OrderBy(p => p.Name).ToArray();
        string visualSpecies = creatures.Length > 0 ? creatures[0].Identifier.Value : "";
        string target = "";
        var peers = client.Peers.ToArray();
        var recipient = effects.Add(new Dropdown(peers.Select(p => p.Name).ToArray()));
        if (peers.Length > 0) { target = peers[0].Account; }
        recipient.SelectionChanged += i => target = peers[i].Account;
        var catalog = DirectorCatalog.Sounds.Values.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToArray();
        var matches = new List<int>();
        string resource = "";
        effects.Add(new Text("Каталог звуков · поиск по существу или названию"));
        var search = effects.Add(new TextInput("", 80));
        var sounds = effects.Add(new VirtualList(i => catalog[matches[i]].Name, 210) { FillHeight = true });
        void Filter() { matches.Clear(); for (int i = 0; i < catalog.Length; i++) { if (catalog[i].Name.IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0) { matches.Add(i); } } sounds.Count = matches.Count; }
        sounds.SelectionChanged += i => resource = catalog[matches[i]].Key;
        search.Changed += _ => Filter(); Filter();
        var parameters = effects.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 48 });
        TextInput Field(string title, string value) { var column = parameters.Add(new Panel()); column.Add(new Text(title)); return column.Add(new TextInput(value, 8)); }
        var gain = Field("Громкость 0–0.7", "0.35");
        var duration = Field("Длительность, с", "5");
        var interval = Field("Повтор через, с", "2");
        var delay = Field("Задержка, с", "0");
        effects.Add(new Text("Изображение существа для скримера"));
        var visualChoice = effects.Add(new Dropdown(creatures.Select(p => TextManager.Get("EntityName." + p.Identifier.Value).Fallback(p.Name).Value).ToArray()));
        visualChoice.SelectionChanged += i => visualSpecies = creatures[i].Identifier.Value;
        var visual = effects.Add(new TextInput("", 128) { Placeholder = "Необязательный текст скримера" });
        effects.Add(new Text("Текст затемнения для визуального воздействия; без стробирования") { Color = GUITheme.Muted });
        DirectorEvent Event(DirectorKind kind) => new() { Id = Guid.NewGuid().ToString("N"), Kind = kind, Target = target, Resource = resource, Text = visual.Text, Visual = visualSpecies, Gain = Number(gain), Duration = Number(duration), Interval = Number(interval), Delay = Number(delay) };
        void Run(DirectorKind kind, bool preview) { try { client.Direct(Event(kind), preview); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Error(error.Message); } }
        var controls = effects.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        controls.Add(new Button("Прослушать", _ => Run(DirectorKind.Sound, true)));
        controls.Add(new Button("Звук игроку", _ => Run(DirectorKind.Sound, false)));
        controls.Add(new Button("Скример игроку", _ => Run(DirectorKind.Scare, false)));
        var sequenceKind = effects.Add(new Dropdown(new[] { "В последовательность: звук", "В последовательность: скример" }));
        effects.Add(new Button("Добавить выбранный эффект в последовательность", _ => { try { if (sequence.Count >= 16) { throw new InvalidOperationException("В последовательности уже 16 событий"); } var value = Event(sequenceKind.SelectedIndex == 0 ? DirectorKind.Sound : DirectorKind.Scare); DirectorEvent.Validate(value); sequence.Add(value); queued.Count = sequence.Count; } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Error(error.Message); } }));
        scenario.Add(new Text("Сообщение всем игрокам внизу экрана"));
        var caption = scenario.Add(new TextInput("", 512));
        scenario.Add(new Text("Время отображения, секунды"));
        var seconds = scenario.Add(new TextInput("8", 8));
        scenario.Add(new Button("Показать всем", _ => { try { client.Direct(new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.Caption, Text = caption.Text, Duration = Number(seconds) }); } catch (ArgumentException error) { Error(error.Message); } }));
        var captionDelay = scenario.Add(new TextInput("0", 8) { Placeholder = "Задержка, секунды" });
        scenario.Add(new Text("Задержка текста в последовательности, секунды"));
        scenario.Add(new Button("Добавить текст в последовательность", _ => {
            try {
                if (sequence.Count >= 16) { throw new InvalidOperationException("В последовательности уже 16 событий"); }
                var value = new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.Caption, Text = caption.Text, Duration = Number(seconds), Delay = Number(captionDelay) };
                DirectorEvent.Validate(value); sequence.Add(value); queued.Count = sequence.Count;
            } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Error(error.Message); }
        }));
        monsters.Add(new Text("Реальные существа из включённых пакетов"));
        var creatureSearch = monsters.Add(new TextInput("", 80));
        var creatureMatches = new List<CharacterPrefab>();
        CharacterPrefab selected = null;
        var creaturePreview = monsters.Add(new Panel { Height = 160 });
        var creatureList = monsters.Add(new VirtualList(i => TextManager.Get("EntityName." + creatureMatches[i].Identifier.Value).Fallback(creatureMatches[i].Name).Value, 320) { FillHeight = true });
        void FilterCreatures() { creatureMatches.Clear(); foreach (var prefab in creatures) { if (prefab.Name.IndexOf(creatureSearch.Text, StringComparison.OrdinalIgnoreCase) >= 0) { creatureMatches.Add(prefab); } } creatureList.Count = creatureMatches.Count; }
        creatureSearch.Changed += _ => FilterCreatures(); creatureList.SelectionChanged += i => {
            selected = creatureMatches[i]; creaturePreview.Clear();
            try { creaturePreview.Add(new CreaturePortrait(selected)); } catch (Exception error) when (error is ArgumentException or System.IO.IOException or InvalidOperationException) { creaturePreview.Add(new Text(error.Message) { Height = 80, Wrap = true }); }
        }; FilterCreatures();
        var behavior = monsters.Add(new Dropdown(new[] { "Естественное поведение", "ИИ выключен до команды GM" }));
        monsters.Add(new Text("Для появления выберите точку в игровом мире") { Color = GUITheme.Muted });
        monsters.Add(new Button("Создать выбранное существо", _ => { if (selected != null) { var creature = selected; var operation = behavior.SelectedIndex == 0 ? EntityOperation.SpawnCreature : EntityOperation.SpawnDormantCreature; director.Close(); client.PickWorld(point => Confirm("Создать " + creature.Name + "?", () => client.Act(0, operation, creature.Identifier.Value, point))); } }));
        director.Add(new Button("АВАРИЙНЫЙ ОСТАНОВ · звук, затемнения, текст, мерцание", _ => client.Direct(new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.Stop })));
    }
    private static string DirectorName(DirectorKind kind) => kind switch { DirectorKind.Sound => "Звук", DirectorKind.Scare => "Скример", DirectorKind.Caption => "Сценарный текст", _ => "Остановка" };
    private static float Number(TextInput input) => float.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : throw new ArgumentException("Введите конечное число: " + input.Text);
    private static DirectorEvent CloneEvent(DirectorEvent value) => new() { Id = Guid.NewGuid().ToString("N"), Kind = value.Kind, Target = value.Target, Resource = value.Resource, Text = value.Text, Visual = value.Visual, Gain = value.Gain, Duration = value.Duration, Interval = value.Interval, Delay = value.Delay };
}
