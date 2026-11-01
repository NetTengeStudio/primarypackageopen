using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;
using TextInput = PrimaryPackage.GUI.TextInput;

namespace PrimaryPackage.Interactions;

internal sealed partial class InteractionViews {
    private AtomicStore<List<SceneProgram>> programStore;
    private List<SceneProgram> programs;
    private SceneProgram draftScene = new() { Name = "Новая сцена", Steps = new List<SceneStep> { new() { Action = SceneAction.Caption, Text = "В темноте слышится движение…" } } };
    private string sceneTarget = "";
    private Item sceneDoor, sceneLight;
    private byte sceneLightComponent;
    private Submarine sceneSubmarine;
    private Vector2 sceneAnchor;
    private float sceneRadius = 500;
    private bool scenePointChosen;
    private VirtualList runningScenes, draftSteps;
    private readonly List<(TextInput Input, Action<float> Set)> sceneNumbers = new();
    private static readonly string[] SceneActionNames = { "Персональный звук", "Скример", "Текст игроку", "Галлюцинация в мире", "Мерцание лампы", "Открыть дверь", "Закрыть дверь", "Signal Collapse" };
    private static readonly string[] BehaviorNames = { "Наблюдает", "Приближается и исчезает", "Преследует", "Отступает", "Исчезает при приближении", "Чёрный силуэт · сбой изображения" };
    private static string StateName(SceneState state) => state switch { SceneState.Armed => "Ожидает условия", SceneState.Running => "Исполняется", SceneState.Completed => "Завершена", _ => "Остановлена с ошибкой" };
    private void ScenesChanged() { if (runningScenes is { Disposed: false }) { runningScenes.Count = client.SceneRows.Count; } }
    private static void ValidatePrograms(List<SceneProgram> values) {
        if (values == null || values.Count > 64) { throw new InvalidDataException("Недопустимая библиотека сцен"); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) { value.Validate(); if (!names.Add(value.Name)) { throw new InvalidDataException("Названия сцен должны различаться"); } }
    }
    private TextInput SceneNumber(Panel panel, string label, float value, Action<float> set) {
        panel.Add(new Text(label));
        var input = panel.Add(new TextInput(value.ToString("G", CultureInfo.InvariantCulture), 16));
        sceneNumbers.Add((input, set)); return input;
    }
    private void CommitSceneNumbers() { foreach (var field in sceneNumbers) { if (!field.Input.Disposed) { field.Set(Number(field.Input)); } } if (draftSteps is { Disposed: false }) { draftSteps.Count = draftScene.Steps.Count; } }
    private static List<SceneProgram> LoadLegacyScenes() {
        string path = Core.ModAssets.Resolve("Data/Interactions/director-scenes.client.json");
        if (!File.Exists(path) && !File.Exists(path + ".bak")) { return new List<SceneProgram>(); }
        var store = new AtomicStore<List<LegacyDirectorScene>>(path, values => LegacyScenes.Convert(values));
        return LegacyScenes.Convert(store.Load(() => new List<LegacyDirectorScene>(), error => Core.ModLog.Error("Legacy scenes recovered: " + error.Message)));
    }
    private void OpenDirector() {
        if (programStore == null) {
            var store = new AtomicStore<List<SceneProgram>>(Core.ModAssets.Resolve("Data/Interactions/scene-programs.client.json"), ValidatePrograms);
            try { programs = store.Load(LoadLegacyScenes, error => Core.ModLog.Error("Scene library recovered: " + error.Message)); programStore = store; }
            catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException) { Error("Не удалось открыть библиотеку сцен: " + error.Message); return; }
        }
        hub?.Close(); director?.Dispose(); sceneNumbers.Clear();
        director = OpenCentered("Сценарии и персональные эффекты", 1080, 730);
        var tabs = director.Add(new Tabs { FillHeight = true });
        var editor = tabs.AddTab("Подготовить сцену");
        var live = tabs.AddTab("Сцены на сервере");
        var effects = tabs.AddTab("Активные эффекты");
        var creatures = tabs.AddTab("Реальные существа");
        var body = editor.Add(new Panel { Flow = GUIFlow.Horizontal, FillHeight = true });
        var bindings = body.Add(new ScrollView { Width = 235, FillHeight = true });
        bindings.Add(new Text("Игрок и объекты") { Color = GUITheme.Accent });
        var peers = client.Peers.ToArray();
        var recipient = bindings.Add(new Dropdown(client.Multiplayer ? peers.Select(p => p.Name).ToArray() : new[] { "Локальный игрок" }));
        int selectedPeer = Array.FindIndex(peers, p => p.Account == sceneTarget);
        if (selectedPeer < 0 && peers.Length > 0) { selectedPeer = 0; sceneTarget = peers[0].Account; }
        if (selectedPeer >= 0) { recipient.Select(selectedPeer); }
        recipient.SelectionChanged += i => sceneTarget = peers[i].Account;
        bindings.Add(new Text("Условие запуска"));
        var trigger = bindings.Add(new Dropdown(new[] { "По команде GM", "Когда откроется дверь", "Когда игрок войдёт в область" }));
        trigger.Select((int)draftScene.Trigger);
        trigger.SelectionChanged += i => draftScene.Trigger = (SceneTrigger)i;
        var doorLabel = bindings.Add(new Text(sceneDoor?.Name ?? "Дверь не выбрана") { Wrap = true, Height = 40 });
        bindings.Add(new Button("Выбрать дверь в мире", _ => PickSceneItem(i => i.GetComponent<Door>() != null, i => { sceneDoor = i; doorLabel.Text = i.Name; })));
        var lightLabel = bindings.Add(new Text(sceneLight?.Name ?? "Лампа не выбрана") { Wrap = true, Height = 40 });
        bindings.Add(new Button("Выбрать лампу в мире", _ => PickSceneItem(i => i.GetComponent<LightComponent>() != null, i => {
            sceneLight = i; sceneLightComponent = (byte)i.Components.FindIndex(c => c is LightComponent); lightLabel.Text = i.Name;
        })));
        var pointLabel = bindings.Add(new Text("Точка появления и центр области") { Wrap = true, Height = 40 });
        bindings.Add(new Button("Выбрать точку в мире", _ => {
            var choosingWindow = director; choosingWindow.Suspend(); client.PickWorld(point => {
                if (choosingWindow.IsDisposed) { return; }
                scenePointChosen = true;
                sceneSubmarine = Hull.FindHull(point)?.Submarine;
                sceneAnchor = point - (sceneSubmarine?.WorldPosition ?? Vector2.Zero);
                pointLabel.Text = "Точка выбрана · " + (sceneSubmarine?.Info.Name ?? "внешний мир"); choosingWindow.Resume();
            }, choosingWindow.Resume);
        }));
        SceneNumber(bindings, "Радиус области, ед. мира", sceneRadius, value => sceneRadius = value);
        var repeat = bindings.Add(new Dropdown(new[] { "Однократно", "Повтор при новом входе / открытии" }));
        repeat.Select(draftScene.Repeat ? 1 : 0);
        repeat.SelectionChanged += i => draftScene.Repeat = i == 1;
        SceneNumber(bindings, "Пауза перед повтором, с", draftScene.Cooldown, value => draftScene.Cooldown = value);
        var plan = body.Add(new Panel { Width = 320, FillHeight = true });
        plan.Add(new Text("Что произойдёт") { Color = GUITheme.Accent });
        var name = plan.Add(new TextInput(draftScene.Name, 64) { Placeholder = "Название сцены" });
        name.Changed += input => draftScene.Name = input.Text;
        var steps = plan.Add(new VirtualList(i => draftScene.Steps[i].Delay.ToString("0.##") + " с · " + SceneActionNames[(int)draftScene.Steps[i].Action], 190) { Count = draftScene.Steps.Count, FillHeight = true });
        draftSteps = steps;
        var inspector = body.Add(new ScrollView { FillHeight = true });
        var action = plan.Add(new Dropdown(SceneActionNames));
        plan.Add(new Button("Добавить действие", _ => SceneTry(() => {
            CommitSceneNumbers(); if (draftScene.Steps.Count == SceneProgram.MaxSteps) { throw new ArgumentException("В сцене уже 16 действий"); }
            draftScene.Steps.Add(new SceneStep { Action = (SceneAction)action.SelectedIndex }); steps.Count = draftScene.Steps.Count;
            steps.Select(draftScene.Steps.Count - 1);
        })));
        plan.Add(new Button("Удалить выбранное действие", _ => SceneTry(() => {
            if ((uint)steps.SelectedIndex >= (uint)draftScene.Steps.Count) { return; }
            int index = steps.SelectedIndex; CommitSceneNumbers(); draftScene.Steps.RemoveAt(index); steps.Count = draftScene.Steps.Count; inspector.Clear(); if (draftScene.Steps.Count > 0) { steps.Select(Math.Min(index, draftScene.Steps.Count - 1)); }
        })));
        steps.SelectionChanged += i => SceneTry(() => { CommitSceneNumbers(); EditSceneStep(inspector, draftScene.Steps[i]); });
        var builtIn = plan.Add(new Dropdown(new[] { "Black Glitch", "Phantom Encounter", "Signal Collapse" }));
        plan.Add(new Button("Подготовить эффект", _ => {
            draftScene = HorrorPresets.Create(builtIn.SelectedIndex, "crawler"); OpenDirector();
        }));
        plan.Add(new Text("Библиотека пресетов") { Color = GUITheme.Accent });
        var presets = plan.Add(new VirtualList(i => programs[i].Name, 100) { Count = programs.Count });
        plan.Add(new Button("Загрузить выбранный пресет", _ => SceneTry(() => {
            if ((uint)presets.SelectedIndex >= (uint)programs.Count) { throw new ArgumentException("Выберите пресет в библиотеке"); }
            draftScene = programs[presets.SelectedIndex].Copy(); OpenDirector();
        })));
        plan.Add(new Button("Сохранить пресет", _ => SceneTry(() => {
            CommitSceneNumbers(); draftScene.Validate(); var next = new List<SceneProgram>(programs); next.RemoveAll(p => p.Name == draftScene.Name); next.Add(draftScene.Copy());
            programStore.Save(next); programs = next; presets.Count = programs.Count;
        })));
        editor.Add(new Text("Пресет хранит действия. Игрок, дверь, лампа и точка задаются при подготовке сцены и не входят в пресет.") { Color = GUITheme.Muted });
        editor.Add(new Button("Поставить сцену на сервер", _ => SceneTry(() => {
            CommitSceneNumbers();
            if (client.Multiplayer && !peers.Any(p => p.Account == sceneTarget)) { throw new ArgumentException("Выберите подключённого игрока"); }
            if (sceneDoor is { Removed: true } || sceneLight is { Removed: true } || sceneSubmarine is { Removed: true }) { throw new ArgumentException("Объект удалён. Выберите привязки заново"); }
            if (!scenePointChosen && (draftScene.Trigger == SceneTrigger.EnterArea || draftScene.Steps.Any(s => s.Action == SceneAction.Hallucination))) { throw new ArgumentException("Выберите точку появления и центр области в мире"); }
            var binding = new SceneBindings(sceneTarget, sceneDoor?.ID ?? 0, sceneLight?.ID ?? 0, sceneLightComponent, sceneSubmarine?.ID ?? 0, sceneAnchor.X, sceneAnchor.Y, sceneRadius);
            client.ArmScene(draftScene, binding);
        })));
        BuildLiveScenes(live, effects); BuildRealCreatures(creatures);
        if (draftScene.Steps.Count > 0) { steps.Select(0); }
        director.Add(new Button("Остановить все сцены и временные эффекты", _ => client.Direct(new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.Stop })));
    }
    private void SceneTry(Action action) { try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { Error(error.Message); } }
    private void EditSceneStep(Panel panel, SceneStep step) {
        // Keep the binding fields; release the disposed inspector's draft controls.
        sceneNumbers.RemoveAll(field => field.Input.Disposed || field.Input.Parent == panel);
        panel.Clear();
        panel.Add(new Text(SceneActionNames[(int)step.Action]) { Color = GUITheme.Accent });
        SceneNumber(panel, "Через сколько секунд", step.Delay, value => step.Delay = value);
        SceneNumber(panel, "Длительность, с", step.Duration, value => step.Duration = value);
        if (step.Action == SceneAction.Caption) {
            var audience = panel.Add(new Dropdown(new[] { "Выбранному игроку", "Всем игрокам" })); audience.Select((int)step.Audience); audience.SelectionChanged += i => step.Audience = (SceneAudience)i;
            var text = panel.Add(new TextInput(step.Text, 512) { Placeholder = "Сообщение игроку" }); text.Changed += input => step.Text = input.Text; }
        if (step.Action is SceneAction.Sound or SceneAction.Scare or SceneAction.Hallucination) {
            SceneNumber(panel, "Громкость 0–0.7", step.Gain, value => step.Gain = value);
            SceneNumber(panel, "Повтор звука каждые, с", step.Interval, value => step.Interval = value);
            panel.Add(new Text("Звук из ресурсов игры · необязателен для изображения") { Wrap = true, Height = 40 });
            var catalog = DirectorCatalog.Sounds.Values.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToArray();
            var matches = new List<int>(); var search = panel.Add(new TextInput("", 80) { Placeholder = "Поиск звука" });
            var chosen = panel.Add(new Text(step.Resource.Length == 0 ? "Без звука" : DirectorCatalog.Sounds.TryGetValue(step.Resource, out var entry) ? entry.Name : "Звук пресета недоступен") { Height = 40, Wrap = true });
            var sounds = panel.Add(new VirtualList(i => catalog[matches[i]].Name, 110));
            void Filter() { matches.Clear(); for (int i = 0; i < catalog.Length; i++) { if (catalog[i].Name.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0) { matches.Add(i); } } sounds.Count = matches.Count; }
            search.Changed += _ => Filter(); sounds.SelectionChanged += i => { step.Resource = catalog[matches[i]].Key; chosen.Text = catalog[matches[i]].Name; }; Filter();
            panel.Add(new Button("Убрать звук", _ => { step.Resource = ""; chosen.Text = "Без звука"; }));
        }
        if (step.Action is SceneAction.Scare or SceneAction.Hallucination) {
            var prefabs = CharacterPrefab.Prefabs.Where(p => p.Identifier != CharacterPrefab.HumanSpeciesName).OrderBy(p => p.Name).ToArray();
            var matches = new List<CharacterPrefab>();
            var search = panel.Add(new TextInput("", 80) { Placeholder = "Поиск внешности существа" });
            var selected = panel.Add(new Text(prefabs.FirstOrDefault(p => p.Identifier.Value == step.Visual)?.Name ?? "Внешность не выбрана"));
            var list = panel.Add(new VirtualList(i => matches[i].Name, 110));
            void Filter() { matches.Clear(); foreach (var prefab in prefabs) { if (prefab.Name.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0) { matches.Add(prefab); } } list.Count = matches.Count; }
            search.Changed += _ => Filter(); list.SelectionChanged += i => { step.Visual = matches[i].Identifier.Value; selected.Text = matches[i].Name; }; Filter();
            SceneNumber(panel, "Интенсивность 0–1", step.Intensity, value => step.Intensity = value);
        }
        if (step.Action == SceneAction.SignalCollapse) { SceneNumber(panel, "Интенсивность 0–1", step.Intensity, value => step.Intensity = value); }
        if (step.Action == SceneAction.Hallucination) {
            var behavior = panel.Add(new Dropdown(BehaviorNames)); behavior.Select((int)step.Behavior); behavior.SelectionChanged += i => step.Behavior = (PhantomBehavior)i;
            SceneNumber(panel, "Масштаб 0.1–4", step.Scale, value => step.Scale = value);
            SceneNumber(panel, "Скорость, ед. мира / с", step.Speed, value => step.Speed = value);
            SceneNumber(panel, "Дистанция реакции, ед. мира", step.NearDistance, value => step.NearDistance = value);
            panel.Add(new Text("Возникает в выбранной точке. Движение ограничено радиусом области.") { Wrap = true, Height = 64 });
        }
        if (step.Action is SceneAction.OpenDoor or SceneAction.CloseDoor) { panel.Add(new Text("Действие изменяет реальную дверь для всех игроков.") { Wrap = true, Height = 64 }); }
    }
    private void PickSceneItem(Func<Item, bool> filter, Action<Item> selected) {
        var choosingWindow = director; choosingWindow.Suspend(); client.PickWorld(point => {
            if (choosingWindow.IsDisposed) { return; }
            var results = new List<WorldQueryResult>(); WorldQueryService.Instance.Query(point, 100 / Math.Max(.1f, GameMain.GameScreen.Cam.Zoom), false, "", results);
            var items = results.Select(r => r.Entity).OfType<Item>().Where(i => i.ParentInventory == null && filter(i)).ToArray();
            if (items.Length == 0) { choosingWindow.Resume(); Error("Под курсором нет подходящего объекта"); return; }
            var picker = GUIRuntime.Instance.Windows.OpenContext("Выберите объект", Pointer, 360);
            picker.Closed += _ => choosingWindow.Resume();
            foreach (var item in items) { var captured = item; picker.Add(new Button(item.Name + " · #" + item.ID, _ => { SceneTry(() => { if (captured.Removed) { throw new ArgumentException("Объект уже удалён"); } selected(captured); }); picker.Close(); choosingWindow.Resume(); })); }
        }, choosingWindow.Resume);
    }
    private void BuildLiveScenes(Panel scenes, Panel effects) {
        scenes.Add(new Text("Отмена убирает ожидающие действия и временные эффекты сцены. Изменения двери сохраняются.") { Wrap = true, Height = 50 });
        runningScenes = scenes.Add(new VirtualList(i => client.SceneRows[i].Name + " · " + StateName(client.SceneRows[i].State), 350) { FillHeight = true, Count = client.SceneRows.Count });
        var detail = scenes.Add(new Text("Выберите сцену") { Wrap = true, Height = 60 });
        runningScenes.SelectionChanged += i => { var row = client.SceneRows[i]; detail.Text = "GM: " + row.Owner + (row.Error.Length == 0 ? "" : " · " + row.Error); };
        scenes.Add(new Button("Обновить список", _ => client.RequestScenes()));
        scenes.Add(new Button("Отменить выбранную сцену", _ => SceneTry(() => { if ((uint)runningScenes.SelectedIndex < (uint)client.SceneRows.Count) { client.RemoveScene(client.SceneRows[runningScenes.SelectedIndex]); } })));
        activeEffectsList = effects.Add(new VirtualList(i => client.Effects[i].Name + " · " + (client.Effects[i].Target.Length == 0 ? "все игроки" : client.Peers.Find(p => p.Account == client.Effects[i].Target)?.Name ?? "игрок отключился") + " · " + client.Effects[i].Remaining.ToString("0.0") + " с", 350) { FillHeight = true, Count = client.Effects.Count });
        effects.Add(new Button("Обновить эффекты", _ => client.RequestEffects()));
        effects.Add(new Button("Остановить выбранный эффект", _ => { if ((uint)activeEffectsList.SelectedIndex < (uint)client.Effects.Count) { client.Direct(new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.StopOne, Resource = client.Effects[activeEffectsList.SelectedIndex].Id }); } }));
        client.RequestScenes(); client.RequestEffects();
    }
    private void BuildRealCreatures(Panel panel) {
        panel.Add(new Text("Это настоящие существа: их видят все игроки, они участвуют в физике и сети.") { Wrap = true, Height = 48 });
        var prefabs = CharacterPrefab.Prefabs.Where(p => p.Identifier != CharacterPrefab.HumanSpeciesName).OrderBy(p => p.Name).ToArray();
        var matches = new List<CharacterPrefab>(); var search = panel.Add(new TextInput("", 80) { Placeholder = "Найти существо" });
        var list = panel.Add(new VirtualList(i => matches[i].Name, 320) { FillHeight = true });
        void Filter() { matches.Clear(); foreach (var prefab in prefabs) { if (prefab.Name.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0) { matches.Add(prefab); } } list.Count = matches.Count; }
        search.Changed += _ => Filter(); Filter();
        var ai = panel.Add(new Dropdown(new[] { "Обычный ИИ", "ИИ выключен до команды GM" }));
        panel.Add(new Button("Создать в выбранной точке", _ => { if ((uint)list.SelectedIndex >= (uint)matches.Count) { return; } var prefab = matches[list.SelectedIndex]; var operation = ai.SelectedIndex == 0 ? EntityOperation.SpawnCreature : EntityOperation.SpawnDormantCreature; director.Close(); client.PickWorld(point => Confirm("Создать " + prefab.Name + "?", () => client.Act(0, operation, prefab.Identifier.Value, point))); }) { Enabled = client.Allows(InteractionAction.SpawnCharacter) && client.Allows(InteractionAction.ManageEntities) });
    }
}
