using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;
using TextInput = PrimaryPackage.GUI.TextInput;

namespace PrimaryPackage.Interactions;

internal sealed partial class InteractionViews {
    private GUIWindow deviceWindow, editHistoryWindow;
    private Item deviceItem;
    private Guid deviceToken;
    private readonly List<InteractionClient.DeviceRow> deviceRows = new();
    private readonly List<int> visibleDeviceRows = new();
    private VirtualList deviceFields, editHistoryList;
    private TextInput deviceSearch;
    private Panel deviceEditor;
    private string selectedDeviceField = "";
    private ushort selectedDeviceComponent;
    private Text deviceCurrent;
    private InteractionClient.DeviceRow selectedDeviceRow;
    private void OpenDevice(Item item) {
        if (deviceWindow is { Disposed: false } && deviceItem == item) { deviceWindow.Reopen(); client.InspectDevice(item); return; }
        deviceWindow?.Dispose(); deviceItem = item; deviceRows.Clear(); visibleDeviceRows.Clear(); selectedDeviceField = "";
        var layout = GUIRuntime.Instance.Layout;
        deviceWindow = GUIRuntime.Instance.Windows.Open("Live Editor · " + item.Name, new Vector2(Math.Max(0, layout.Viewport.X - 390 * layout.Scale), 35 * layout.Scale), 380, Math.Min(640, layout.Viewport.Y / layout.Scale - 60));
        deviceWindow.ClosingRequested += _ => client.StopDeviceInspection();
        deviceSearch = deviceWindow.Add(new TextInput("", 80) { Placeholder = "Поиск по компонентам и свойствам" });
        deviceSearch.Changed += _ => FilterDeviceRows();
        deviceFields = deviceWindow.Add(new VirtualList(i => {
            var row = deviceRows[visibleDeviceRows[i]];
            string type = row.Type[(row.Type.LastIndexOf('.') + 1)..]; return $"{row.Component + 1} · {type} / {row.Field}";
        }, 220));
        deviceEditor = deviceWindow.Add(new ScrollView { FillHeight = true });
        deviceFields.SelectionChanged += ShowDeviceField;
        var actions = deviceWindow.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        actions.Add(new Button("Обновить", _ => client.InspectDevice(item)));
        actions.Add(new Button("История", _ => OpenEditHistory()));
        deviceEditor.Add(new Text("Получение схемы объекта…") { Wrap = true, Height = 60 });
        client.InspectDevice(item);
    }
    private void FilterDeviceRows() {
        visibleDeviceRows.Clear(); string search = deviceSearch?.Text ?? "";
        for (int i = 0; i < deviceRows.Count; i++) {
            var row = deviceRows[i]; if (row.Field.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Type.Contains(search, StringComparison.OrdinalIgnoreCase)) { visibleDeviceRows.Add(i); }
        }
        deviceFields.Count = visibleDeviceRows.Count;
    }
    private void DeviceReceived(ushort item, Guid token, List<InteractionClient.DeviceRow> rows) {
        if (deviceWindow is not { Disposed: false } || deviceItem is not { Removed: false } || deviceItem.ID != item) { return; }
        deviceToken = token;
        bool schemaChanged = rows.Count != deviceRows.Count;
        if (!schemaChanged) { for (int i = 0; i < rows.Count; i++) { if (rows[i].Component != deviceRows[i].Component || rows[i].Type != deviceRows[i].Type || rows[i].Field != deviceRows[i].Field) { schemaChanged = true; break; } } }
        if (!schemaChanged) {
            for (int i = 0; i < rows.Count; i++) { if (deviceRows[i].Sample > rows[i].Sample) { rows[i] = deviceRows[i]; } }
        }
        deviceRows.Clear(); deviceRows.AddRange(rows);
        if (schemaChanged) { FilterDeviceRows(); }
        foreach (var row in rows) {
            if (row.Component == selectedDeviceComponent && row.Field == selectedDeviceField) {
                selectedDeviceRow = row;
                if (deviceCurrent is { Disposed: false }) { deviceCurrent.Text = "Сейчас: " + row.Value; }
                return;
            }
        }
        if (schemaChanged || rows.Count == 0) {
            deviceEditor.Clear(); deviceEditor.Add(new Text(rows.Count == 0 ? "Нет свойств, разрешённых для изменения во время симуляции" : "Выберите свойство. Значения обновляются без пересоздания полей ввода.") { Wrap = true, Height = 80 });
        }
    }
    private void DeviceValueReceived(ushort item, Guid token, InteractionClient.DeviceRow row) {
        if (deviceItem is not { Removed: false } || deviceItem.ID != item || deviceToken != token) { return; }
        for (int i = 0; i < deviceRows.Count; i++) { if (deviceRows[i].Component == row.Component && deviceRows[i].Field == row.Field) { if (deviceRows[i].Sample > row.Sample) { return; } deviceRows[i] = row; break; } }
        if (row.Component == selectedDeviceComponent && row.Field == selectedDeviceField) {
            selectedDeviceRow = row; if (deviceCurrent is { Disposed: false }) { deviceCurrent.Text = "Сейчас: " + row.Value; }
        }
    }
    private void ShowDeviceField(int index) {
        if ((uint)index >= (uint)visibleDeviceRows.Count) { return; }
        var row = deviceRows[visibleDeviceRows[index]];
        var field = DeviceEditor.Resolve(DeviceEditor.Resolve(deviceItem, row.Component, row.Type), row.Field);
        selectedDeviceField = row.Field; selectedDeviceComponent = row.Component; selectedDeviceRow = row;
        deviceEditor.Clear(); deviceEditor.Add(new Text(field.Label) { Wrap = true, Height = 40, Color = GUITheme.Accent });
        deviceCurrent = deviceEditor.Add(new Text("Сейчас: " + row.Value) { Wrap = true, Height = 48 });
        // Capture the revision shown when editing began, not a later background refresh.
        if (row.Kind is DeviceValueKind.Boolean or DeviceValueKind.Enumeration) {
            string[] values = row.Kind == DeviceValueKind.Boolean ? new[] { "false", "true" } : field.Choices;
            var choice = deviceEditor.Add(new Dropdown(values)); choice.Select(Math.Max(0, Array.IndexOf(values, row.Value)));
            deviceEditor.Add(new Button("Применить", _ => SceneTry(() => client.ChangeDevice(deviceItem, deviceToken, row, values[choice.SelectedIndex]))));
        } else {
            var input = deviceEditor.Add(new TextInput(row.Value, 512));
            if (row.Kind == DeviceValueKind.Color) {
                var palette = deviceEditor.Add(new GMColorPalette());
                palette.Changed += color => input.Text = DeviceEditor.Format(color);
            }
            deviceEditor.Add(new Text(field.Range) { Wrap = true, Height = 48 });
            deviceEditor.Add(new Button("Применить", _ => SceneTry(() => client.ChangeDevice(deviceItem, deviceToken, row, input.Text))));
        }
        deviceEditor.Add(new Button("Взять актуальное значение", _ => {
            for (int i = 0; i < visibleDeviceRows.Count; i++) { var current = deviceRows[visibleDeviceRows[i]]; if (current.Component == selectedDeviceComponent && current.Field == selectedDeviceField) { ShowDeviceField(i); return; } }
        }));
    }
    private void OpenEditHistory() {
        editHistoryWindow?.Dispose(); editHistoryWindow = OpenCentered("История мира · последние 16 операций", 720, 440);
        editHistoryList = editHistoryWindow.Add(new VirtualList(i => {
            var row = client.EditHistory[i]; return DateTimeOffset.FromUnixTimeMilliseconds(row.Timestamp).ToLocalTime().ToString("HH:mm:ss") + " · " + row.Actor + " · " + row.Property[(row.Property.IndexOf('|') + 1)..] + " · правок: " + row.Changes + (row.Undone ? " · отменено" : " · применено");
        }) { FillHeight = true, Count = client.EditHistory.Count });
        editHistoryWindow.Add(new Text("Отмена восстанавливает настройку. Последствия симуляции не отменяются; чужие и последующие правки защищены.") { Wrap = true, Height = 50 });
        editHistoryWindow.Add(new Button("Отменить / повторить выбранное", _ => SceneTry(() => {
            if ((uint)editHistoryList.SelectedIndex < (uint)client.EditHistory.Count) { client.ReverseEdit(client.EditHistory[editHistoryList.SelectedIndex]); }
        })));
        editHistoryWindow.Add(new Button("Экспортировать журнал раунда на сервер", _ => client.ExportWorldEdits()));
        client.RequestEditHistory();
    }
    private void EditHistoryReceived() { if (editHistoryList is { Disposed: false }) { editHistoryList.Count = client.EditHistory.Count; } }
    internal void OpenWorldContext(Vector2 world, Vector2 pointer) {
        var candidates = new List<MapEntity>(); var characters = new List<WorldQueryResult>();
        float radius = Math.Min(5000, 100 / Math.Max(.1f, GameMain.GameScreen.Cam.Zoom));
        WorldQueryService.Instance.QueryMap(world, world, candidates);
        candidates.Sort(static (a, b) => a.SpriteDepth.CompareTo(b.SpriteDepth));
        WorldQueryService.Instance.Query(world, radius, true, "", characters);
        var menu = GUIRuntime.Instance.Windows.OpenContext("Объекты под курсором", pointer, 360);
        int count = 0;
        foreach (var result in characters) {
            if (result.Entity is not Character { Removed: false } character || character.InvisibleTimer > 0) { continue; }
            menu.Add(new Button(character.Name + " · персонаж", _ => { menu.Close(); OpenCharacter(character, pointer); })); count++;
        }
        foreach (var result in candidates) {
            if (result is not Item { ParentInventory: null } item || item.IsHidden || !item.IsMouseOn(world)) { continue; }
            menu.Add(new Button(item.Name + " · свойства", _ => { menu.Close(); OpenDevice(item); })); count++;
        }
        if (count == 0) { menu.Add(new Text("Здесь нет персонажей или предметов")); }
        menu.Add(new Button("Сценарии и эффекты", _ => { menu.Close(); OpenDirector(); }) { Enabled = client.Allows(InteractionAction.DirectEvents) });
        menu.Add(new Button("Рабочее пространство GM", _ => { menu.Close(); OpenHub(); }));
    }
}
