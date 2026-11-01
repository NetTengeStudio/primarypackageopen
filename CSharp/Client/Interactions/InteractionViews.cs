using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;
using TextInput = PrimaryPackage.GUI.TextInput;

namespace PrimaryPackage.Interactions;

internal sealed partial class InteractionViews : IDisposable {
    private readonly InteractionClient client;
    private GUIWindow context, hub, queryWindow, messageWindow, permissionWindow, creator;
    private Character contextCharacter;
    private Camera contextCamera;
    private VirtualList resultList;
    private Text resultDetail;
    private string conversation = "";
    private readonly List<PrivateMessage> history = new();
    private readonly List<(GUIWindow Window, double Until)> notifications = new();
    private readonly AtomicStore<List<CharacterTemplate>> templateStore;
    private List<CharacterTemplate> templates;
    private readonly List<Text> permissionModes = new();
    private string permissionAccount = "";
    private PermissionAuditEntry[] permissionAudit = Array.Empty<PermissionAuditEntry>();
    private VirtualList auditList;
    private GUIWindow errorWindow;
    private Text errorText;
    private static Vector2 Pointer => GUIRuntime.Instance.Input.Pointer;
    public InteractionViews(InteractionClient client) {
        this.client = client;
        client.ResultsChanged += ResultsChanged;
        client.MessagesChanged += MessagesChanged;
        client.PermissionsChanged += PermissionsChanged;
        client.EffectsChanged += EffectsChanged;
        templateStore = new AtomicStore<List<CharacterTemplate>>(ModAssets.Resolve("Data/Interactions/character-templates.client.json"), ValidateTemplates);
        templates = templateStore.Load(() => new List<CharacterTemplate>(), error => ModLog.Error("Character templates recovered: " + error.Message));
    }
    private static void ValidateTemplates(List<CharacterTemplate> values) {
        if (values == null || values.Count > 128) { throw new InvalidDataException("Invalid character template store"); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) {
            if (value == null || !InteractionProtocol.ValidText(value.Name, 40) || !InteractionProtocol.ValidText(value.Job, 128) || !names.Add(value.Name) ||
                (uint)value.Head > ushort.MaxValue || (uint)value.Hair > ushort.MaxValue || (uint)value.Beard > ushort.MaxValue || (uint)value.Moustache > ushort.MaxValue ||
                (uint)value.Attachment > ushort.MaxValue || (uint)value.SkinColor > ushort.MaxValue || (uint)value.HairColor > ushort.MaxValue || (uint)value.FacialHairColor > ushort.MaxValue) { throw new InvalidDataException("Invalid character template"); }
        }
    }
    public void OpenHub() {
        hub?.Dispose();
        hub = OpenCentered("Рабочее пространство GM", 620, 360);
        Vector2 world = GameMain.GameScreen.Cam.ScreenToWorld(Pointer);
        hub.Add(new Text("Мир") { Color = GUITheme.Accent });
        var worldActions = hub.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 40 });
        worldActions.Add(new Button("Персонажи", _ => OpenQuery(true, world)) { Enabled = client.Allows(InteractionAction.QueryCharacters) });
        worldActions.Add(new Button("Предметы", _ => OpenQuery(false, world)) { Enabled = client.Allows(InteractionAction.QueryItems) });
        hub.Add(new Text("Персонажи и события") { Color = GUITheme.Accent });
        hub.Add(new Button("Character Studio", _ => OpenStudio()) { Enabled = client.Allows(InteractionAction.SpawnCharacter) });
        hub.Add(new Button("Horror Director", _ => OpenDirector()) { Enabled = client.Allows(InteractionAction.DirectEvents) });
        hub.Add(new Button("Взаимодействие · F2", _ => OpenMessages(null, "")));
        hub.Add(new Button("Полномочия", _ => OpenPermissions()) { Enabled = client.Multiplayer && client.Allows(InteractionAction.ManagePermissions) });
        hub.Add(new Text("Alt + ЛКМ: мерцание лампы · Alt + ПКМ: это окно") { Color = GUITheme.Muted });
    }
    private static GUIWindow OpenCentered(string title, float width, float height) {
        var layout = GUIRuntime.Instance.Layout;
        return GUIRuntime.Instance.Windows.Open(title, new Vector2(Math.Max(0, (layout.Viewport.X - width * layout.Scale) / 2), Math.Max(0, (layout.Viewport.Y - height * layout.Scale) / 2)), width, height);
    }
    public void OpenCharacter(Character character, Vector2 pointer) {
        context?.Close();
        contextCharacter = character;
        contextCamera = GameMain.GameScreen.Cam;
        context = GUIRuntime.Instance.Windows.OpenContext(character.Name, pointer, 300);
        context.Add(new Text(character.Info?.Job?.Prefab.Name.Value ?? character.SpeciesName.Value));
        context.Add(new Text("ID " + character.ID + " · " + (character.IsDead ? "Мёртв" : "Жив")));
        if (character.Info != null) { context.Add(new CharacterPreview(character.Info)); }
        InteractionClient.Peer peer = null;
        foreach (var candidate in client.Peers) { if (candidate.Character == character.ID) { peer = candidate; break; } }
        if (peer != null) {
            var recipient = peer;
            context.Add(new Button("Личное сообщение", _ => { context.Close(); OpenMessages(recipient.Account, recipient.Name); }) { Enabled = client.Allows(InteractionAction.SendPrivateMessage) });
            if (client.Allows(InteractionAction.ManagePermissions)) { context.Add(new Button("Разрешения игрока", _ => { context.Close(); OpenPermissions(recipient.Account); })); }
        }
        if (client.Allows(InteractionAction.ManageEntities) && character.AIController != null) {
            context.Add(new Button("Включить ИИ", _ => client.Act(character.ID, EntityOperation.AIEnabled, "on")));
            context.Add(new Button("Приостановить ИИ", _ => client.Act(character.ID, EntityOperation.AIEnabled, "off")));
        }
        if (client.Allows(InteractionAction.ManageEntities)) { context.Add(new Button("Разорвать персонажа", _ => Confirm("Разорвать " + character.Name + "?", () => client.Act(character.ID, EntityOperation.Gib)))); }
        if (client.Allows(InteractionAction.SpawnCharacter)) { context.Add(new Button("Создать персонажа здесь", _ => { context.Close(); OpenCreator(pointer); })); }
    }
    private void OpenQuery(bool characters, Vector2 world) {
        hub?.Close(); queryWindow?.Close();
        queryWindow = GUIRuntime.Instance.Windows.Open(characters ? "Персонажи поблизости" : "Предметы поблизости", Pointer, 450);
        var filters = queryWindow.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 30 });
        var radius = filters.Add(new TextInput("500", 8) { Width = 85 });
        var filter = filters.Add(new TextInput("", 80));
        void Search() {
            if (!float.TryParse(radius.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value <= 0 || value > 5000) { Error("Радиус: число от 1 до 5000"); return; }
            client.Query(world, value, characters, filter.Text);
        }
        filters.Add(new Button("Найти", _ => Search()) { Width = 85 });
        filter.Submitted += _ => Search();
        resultList = queryWindow.Add(new VirtualList(index => {
            var row = client.Results[index];
            return row.Name + "  #" + row.Id + "  ·  " + row.Distance.ToString("F0");
        }, 300));
        resultDetail = queryWindow.Add(new Text("Выберите объект"));
        resultList.SelectionChanged += index => resultDetail.Text = client.Results[index].Detail;
        queryWindow.Add(new Button("Открыть действия", _ => {
            int index = resultList.SelectedIndex;
            if ((uint)index >= (uint)client.Results.Count) { return; }
            var row = client.Results[index];
            Entity entity = Entity.FindEntityByID(row.Id);
            if (entity == null || entity.Removed) { Error("Объект больше не существует"); return; }
            if (entity is Character character) { OpenCharacter(character, Pointer); }
            else { OpenItem((Item)entity); }
        }));
        Search();
    }
    private void ResultsChanged() {
        if (resultList is { Disposed: false }) { resultList.Count = client.Results.Count; }
    }
    private void OpenItem(Item item) {
        var window = GUIRuntime.Instance.Windows.Open(item.Name, Pointer, 330);
        window.DismissOnOutside = true;
        window.Add(new Text("ID " + item.ID + " · " + item.Prefab.Identifier.Value));
        window.Add(new Text("Контейнер: " + (item.Container?.Name ?? "Мир")));
        window.Add(new Text("Состояние: " + item.Condition.ToString("F1")));
        if (client.Allows(InteractionAction.ManageEntities)) {
            window.Add(new Text("Прочность (0–" + item.MaxCondition.ToString("F0") + ")"));
            var condition = window.Add(new TextInput(item.Condition.ToString("F1", CultureInfo.InvariantCulture), 12));
            window.Add(new Button("Применить прочность", _ => client.Act(item.ID, EntityOperation.Condition, condition.Text)));
            if (item.GetComponent<Door>() != null) { window.Add(new Button("Разрушить дверь", _ => Confirm("Разрушить " + item.Name + "?", () => client.Act(item.ID, EntityOperation.BreakDoor)))); }
            window.Add(new Button("Удалить предмет", _ => Confirm("Удалить " + item.Name + "?", () => client.Act(item.ID, EntityOperation.RemoveItem))));
        }
        if (!client.Allows(InteractionAction.FlickerLight)) { return; }
        for (int i = 0; i < item.Components.Count && i <= byte.MaxValue; i++) {
            if (item.Components[i] is not LightComponent) { continue; }
            byte component = (byte)i;
            window.Add(new Button("Мерцание · источник " + i, _ => client.Flicker(item, component)));
        }
    }
    public void Notify(PrivateMessage message) {
        var window = GUIRuntime.Instance.Windows.Notify("Сообщение · " + message.SenderName, new Vector2(GameMain.GraphicsWidth - 380 * Barotrauma.GUI.Scale, 60 * Barotrauma.GUI.Scale));
        notifications.Add((window, GUIRuntime.Now + 8));
        string preview = message.Text.Length > 60 ? message.Text.Substring(0, 60) + "…" : message.Text;
        window.Add(new Text(preview));
        window.Add(new Button("Открыть переписку", _ => { window.Close(); OpenMessages(null, ""); SetConversation(message.Kind, message.Kind == MessageKind.Private ? message.From : message.Thread, message.SenderName); }));
    }
    public void Error(string message) {
        if (errorWindow is { Disposed: false, Closing: false }) { errorText.Text = message; return; }
        var window = GUIRuntime.Instance.Windows.OpenPopup("Не удалось выполнить действие", Pointer, 420);
        errorWindow = window;
        errorText = window.Add(new Text(message) { Height = 80, Wrap = true });
        window.Add(new Button("Закрыть", _ => window.Close()));
    }
    private void OpenPermissions(string target = null) {
        hub?.Close(); permissionWindow?.Close(); permissionModes.Clear();
        permissionWindow = GUIRuntime.Instance.Windows.Open("Разрешения · действия", Pointer, 570);
        if (target == null) {
            var peers = client.Peers.ToArray();
            var list = permissionWindow.Add(new VirtualList(i => peers[i].Name + " · " + peers[i].Account, 240) { Count = peers.Length });
            list.SelectionChanged += index => OpenPermissions(peers[index].Account);
            return;
        }
        permissionAccount = target;
        permissionWindow.Add(new Text(target));
        foreach (InteractionAction action in Enum.GetValues<InteractionAction>()) {
            var row = permissionWindow.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 30 });
            permissionModes.Add(row.Add(new Text(ActionName(action)) { Width = 230 }));
            row.Add(new Button("Да", _ => client.SetPermission(target, action, 1)));
            row.Add(new Button("Нет", _ => client.SetPermission(target, action, 0)));
            row.Add(new Button("Наслед.", _ => client.SetPermission(target, action, 2)) { Width = 85 });
        }
        permissionWindow.Add(new Text("Наследование: Barotrauma и GM Tools"));
        auditList = permissionWindow.Add(new VirtualList(index => {
            var change = permissionAudit[index];
            return DateTimeOffset.FromUnixTimeMilliseconds(change.Timestamp).ToLocalTime().ToString("g") + " · " + change.Actor + " · " + ActionName(change.Action) + " · " + (change.Mode == 1 ? "Да" : change.Mode == 0 ? "Нет" : "Наслед.");
        }, 150));
        client.RequestPermissions(target);
    }
    private static string ActionName(InteractionAction action) => action switch {
        InteractionAction.ViewCharacter => "Сведения о персонаже",
        InteractionAction.SendPrivateMessage => "Личные сообщения",
        InteractionAction.QueryCharacters => "Поиск персонажей",
        InteractionAction.QueryItems => "Поиск предметов",
        InteractionAction.SpawnCharacter => "Создание персонажей",
        InteractionAction.ControlCharacter => "Управление персонажем",
        InteractionAction.FlickerLight => "Мерцание света",
        InteractionAction.ManagePermissions => "Управление разрешениями",
        InteractionAction.ViewGMFeed => "Обращения и RP-лента",
        InteractionAction.ManageEntities => "Редактирование сущностей",
        InteractionAction.DirectEvents => "Horror Director",
        InteractionAction.ManageKits => "Сохранение и удаление Kits",
        _ => action.ToString()
    };
    private void PermissionsChanged(string account, byte[] modes, PermissionAuditEntry[] audit) {
        if (permissionWindow == null || permissionWindow.Disposed || permissionAccount != account) { return; }
        for (int i = 0; i < permissionModes.Count && i < modes.Length; i++) { permissionModes[i].Text = ActionName((InteractionAction)i) + " · " + (modes[i] == 1 ? "Да" : modes[i] == 0 ? "Нет" : "Наслед."); }
        permissionAudit = audit;
        if (auditList is { Disposed: false }) { auditList.Count = audit.Length; }
    }
    private void OpenCreator(Vector2 pointer, CharacterTemplate preset = null, Vector2? fixedWorld = null) => OpenStudio(preset, fixedWorld ?? GameMain.GameScreen.Cam.ScreenToWorld(pointer));
    public void Update() {
        if (messagesDirty && messageWindow is { Closing: false, Disposed: false }) { RefreshMessages(); }
        if (context is { Closing: false } && (contextCharacter == null || contextCharacter.Removed || contextCamera != GameMain.GameScreen.Cam)) { context.Close(); }
        double now = GUIRuntime.Now;
        for (int i = notifications.Count - 1; i >= 0; i--) {
            var notification = notifications[i];
            if (notification.Window.Disposed || now >= notification.Until) { notification.Window.Close(); notifications.RemoveAt(i); }
        }
    }
    public void Reset() {
        context?.Dispose(); hub?.Dispose(); queryWindow?.Dispose(); messageWindow?.Dispose(); permissionWindow?.Dispose(); creator?.Dispose(); errorWindow?.Dispose();
        context = hub = queryWindow = messageWindow = permissionWindow = creator = errorWindow = null;
        resultList = auditList = activeEffectsList = null; transcript = null; peersList = null;
        resultDetail = errorText = null; director?.Dispose(); director = null; ReleaseStudio();
        for (int i = 0; i < notifications.Count; i++) { notifications[i].Window.Dispose(); }
        contextCharacter = null; contextCamera = null; headerCache.Clear(); history.Clear(); notifications.Clear(); permissionModes.Clear();
    }
    public void Dispose() {
        client.ResultsChanged -= ResultsChanged; client.MessagesChanged -= MessagesChanged; client.PermissionsChanged -= PermissionsChanged;
        client.EffectsChanged -= EffectsChanged;
        Reset();
    }
}
