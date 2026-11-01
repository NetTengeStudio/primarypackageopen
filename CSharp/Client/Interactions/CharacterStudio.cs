using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;
using PrimaryPackage.Kits;
using TextInput = PrimaryPackage.GUI.TextInput;
using GUIComponent = PrimaryPackage.GUI.GUIComponent;

namespace PrimaryPackage.Interactions;

internal sealed partial class InteractionViews {
    private readonly List<CharacterInfo> studioInfos = new();
    private Panel kitDetails;
    private Action refreshKits;
    private static CharacterTemplate Copy(CharacterTemplate value) => new() {
        Name = value.Name, Job = value.Job, Head = value.Head, Hair = value.Hair, Beard = value.Beard, Moustache = value.Moustache,
        Attachment = value.Attachment, SkinColor = value.SkinColor, HairColor = value.HairColor, FacialHairColor = value.FacialHairColor, Control = value.Control
    };
    internal void RefreshStudioKits() => refreshKits?.Invoke();
    private void ReleaseStudio() {
        for (int i = 0; i < studioInfos.Count; i++) { studioInfos[i].Remove(); }
        studioInfos.Clear(); refreshKits = null; kitDetails = null;
    }
    internal void ToggleStudio() { if (creator is { Disposed: false, Closing: false }) { creator.Close(); } else { OpenStudio(library: true); } }
    public void OpenStudio(CharacterTemplate preset = null, Vector2? fixedWorld = null, bool library = false) {
        hub?.Close(); creator?.Dispose(); ReleaseStudio();
        creator = OpenCentered("Character Studio", 960, 680);
        var window = creator;
        window.Closed += _ => { if (ReferenceEquals(creator, window)) { ReleaseStudio(); } };
        Vector2 position = fixedWorld ?? GameMain.GameScreen.Cam.WorldViewCenter;
        var template = preset == null ? new CharacterTemplate { Name = "Новый персонаж" } : Copy(preset);
        var tabs = window.Add(new Tabs { FillHeight = true });
        var editor = tabs.AddTab("Персонаж");
        var kitLibrary = tabs.AddTab("Kits и роли");
        var body = editor.Add(new Panel { Flow = GUIFlow.Horizontal, FillHeight = true });
        var identity = body.Add(new Panel { Width = 290, FillHeight = true });
        identity.Add(new Text("Имя персонажа") { Color = GUITheme.Accent });
        var name = identity.Add(new TextInput(template.Name, 40));
        name.Changed += input => template.Name = input.Text;
        identity.Add(new Text("Профессия"));
        var jobs = JobPrefab.Prefabs.OrderBy(p => p.Name.Value, StringComparer.CurrentCulture).ToArray();
        var job = identity.Add(new Dropdown(jobs.Select(p => p.Name.Value).ToArray()));
        for (int i = 0; i < jobs.Length; i++) { if (jobs[i].Identifier.Value == template.Job) { job.Select(i); } }
        var mainPreview = identity.Add(new Panel { Height = 210 });
        var role = identity.Add(new Button(template.Control ? "После создания: управлять" : "После создания: NPC") { Enabled = client.Allows(InteractionAction.ControlCharacter) });
        role.Activated += _ => { template.Control = !template.Control; role.Text = template.Control ? "После создания: управлять" : "После создания: NPC"; };
        identity.Add(new Text("Шаблоны") { Color = GUITheme.Accent });
        var saved = identity.Add(new VirtualList(i => templates[i].Name, 110) { FillHeight = true, Count = templates.Count });
        saved.SelectionChanged += i => OpenStudio(templates[i], position);
        identity.Add(new Button("Сохранить вариант", _ => {
            try {
                ValidateInfo(template);
                var next = new List<CharacterTemplate>(templates);
                next.RemoveAll(value => value.Name == template.Name);
                next.Add(Copy(template)); templateStore.Save(next); templates = next; saved.Count = templates.Count;
            } catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { Error(error.Message); }
        }));
        var appearance = body.Add(new Panel { FillHeight = true });
        appearance.Add(new Text("Внешность") { Color = GUITheme.Accent });
        var section = appearance.Add(new Dropdown(new[] { "Лицо", "Причёска", "Борода", "Усы", "Аксессуар" }));
        var grid = appearance.Add(new Panel { FillHeight = true });
        var paging = appearance.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        int page = 0;
        var pageLabel = new Text("") { Width = 140 };
        var colors = appearance.Add(new Panel { Height = 150 });
        CharacterInfo info = null;
        var variants = new List<CharacterInfo>();
        void ClearVariants() { grid.Clear(); foreach (var variant in variants) { variant.Remove(); studioInfos.Remove(variant); } variants.Clear(); }
        void MainPreview() {
            mainPreview.Clear();
            if (info != null) { info.Remove(); studioInfos.Remove(info); }
            info = CharacterCreationService.Instance.CreatePreview(template); studioInfos.Add(info);
            mainPreview.Add(new CharacterPreview(info) { Height = 200 });
        }
        int Count() => section.SelectedIndex switch { 0 => info.Prefab.Heads.Length, 1 => info.Hairs.Count, 2 => info.Beards.Count, 3 => info.Moustaches.Count, _ => info.FaceAttachments.Count };
        int Selected() => section.SelectedIndex switch { 0 => template.Head, 1 => template.Hair, 2 => template.Beard, 3 => template.Moustache, _ => template.Attachment };
        void SetVariant(CharacterTemplate value, int index) {
            switch (section.SelectedIndex) {
                case 0: value.Head = index; value.Hair = value.Beard = value.Moustache = value.Attachment = 0; break;
                case 1: value.Hair = index; break;
                case 2: value.Beard = index; break;
                case 3: value.Moustache = index; break;
                default: value.Attachment = index; break;
            }
        }
        void Grid() {
            ClearVariants();
            int count = Count(); page = Math.Clamp(page, 0, Math.Max(0, (count - 1) / 8));
            pageLabel.Text = (page + 1) + " / " + Math.Max(1, (count + 7) / 8);
            for (int row = 0; row < 2; row++) {
                var line = grid.Add(new Panel { Flow = GUIFlow.Horizontal, FillHeight = true });
                for (int column = 0; column < 4; column++) {
                    int index = page * 8 + row * 4 + column;
                    if (index >= count) { break; }
                    var value = Copy(template); SetVariant(value, index);
                    var variant = CharacterCreationService.Instance.CreatePreview(value); variants.Add(variant); studioInfos.Add(variant);
                    var choice = line.Add(new PortraitChoice(variant, "Выбрать", _ => { SetVariant(template, index); MainPreview(); Grid(); Palettes(); }) { FillHeight = true, Selected = Selected() == index });
                }
            }
        }
        void Palette(string title, Color[] values, int selected, Action<int> change) {
            colors.Add(new Text(title));
            var row = colors.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 24 });
            for (int i = 0; i < values.Length; i++) { int index = i; row.Add(new ColorChoice(values[i], _ => { change(index); MainPreview(); Grid(); Palettes(); }) { Selected = i == selected }); }
        }
        void Palettes() {
            colors.Clear();
            Palette("Кожа", info.SkinColors.Select(c => c.Color).ToArray(), template.SkinColor, i => template.SkinColor = i);
            Palette("Волосы", info.HairColors.Select(c => c.Color).ToArray(), template.HairColor, i => template.HairColor = i);
            Palette("Растительность на лице", info.FacialHairColors.Select(c => c.Color).ToArray(), template.FacialHairColor, i => template.FacialHairColor = i);
        }
        paging.Add(new Button("Назад", _ => { page--; Grid(); })); paging.Add(pageLabel); paging.Add(new Button("Далее", _ => { if ((page + 1) * 8 < Count()) { page++; } Grid(); }));
        section.SelectionChanged += _ => { page = 0; Grid(); };
        job.SelectionChanged += i => { template.Job = jobs[i].Identifier.Value; MainPreview(); Grid(); Palettes(); };
        editor.Add(new Button("Выбрать точку появления", _ => { var draft = Copy(template); creator.Close(); client.PickWorld(point => OpenStudio(draft, point)); }));
        editor.Add(new Text("Создание в выбранной точке мира; внешность и профессия проверяются сервером") { Color = GUITheme.Muted });
        editor.Add(new Button("Создать персонажа", _ => { try { client.Spawn(Copy(template), position); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Error(error.Message); } }) { Enabled = client.Allows(InteractionAction.SpawnCharacter) });
        try { MainPreview(); Grid(); Palettes(); }
        catch (ArgumentException error) { window.Dispose(); Error(error.Message); return; }
        BuildKitLibrary(kitLibrary);
        if (library) { tabs.Select(1); }
    }
    private static void ValidateInfo(CharacterTemplate template) { var value = CharacterCreationService.Instance.CreateInfo(template); value.Remove(); }
    private void BuildKitLibrary(Panel library) {
        KitClient.RequestList();
        var body = library.Add(new Panel { Flow = GUIFlow.Horizontal, FillHeight = true });
        var left = body.Add(new Panel { Width = 290, FillHeight = true });
        left.Add(new Text("Поиск сохранённого Kit или роли"));
        var search = left.Add(new TextInput("", 80));
        var entries = new List<(HumanPrefab Prefab, bool Kit)>();
        var list = left.Add(new VirtualList(i => KitLabel(entries[i].Prefab), 300) { FillHeight = true });
        kitDetails = body.Add(new Panel { FillHeight = true });
        var details = kitDetails;
        var previews = new List<CharacterInfo>();
        HumanPrefab selected = null;
        void ClearPreviews() { details.Clear(); foreach (var preview in previews) { preview.Remove(); studioInfos.Remove(preview); } previews.Clear(); }
        void Select(int index) {
            ClearPreviews();
            var entry = entries[index]; selected = entry.Prefab;
            details.Add(new Text(KitLabel(selected)) { Color = GUITheme.Accent });
            var gallery = details.Add(new Panel { Height = 210 });
            for (int row = 0; row < 2; row++) {
                var line = gallery.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 104 });
                for (int column = 0; column < 3; column++) {
                    var portrait = selected.CreateCharacterInfo(); previews.Add(portrait); studioInfos.Add(portrait);
                    var variant = line.Add(new Panel { FillHeight = true });
                    variant.Add(new CharacterPreview(portrait) { Height = 80 });
                    variant.Add(new Text(portrait.Name));
                }
            }
            details.Add(new Text(previews[0].Job?.Prefab.Name.Value ?? "Без профессии"));
            details.Add(new Button("Другие варианты внешности", _ => Select(index)));
            var inventory = new List<string>();
            var set = selected.ItemSets.FirstOrDefault().element;
            if (set != null) { foreach (var element in set.Descendants()) { string id = element.GetAttributeString("identifier", ""); if (ItemPrefab.Prefabs.TryGet(id.ToIdentifier(), out var item)) { inventory.Add(item.Name.Value); } } }
            details.Add(new VirtualList(i => inventory[i], 140) { Count = inventory.Count, FillHeight = true });
            details.Add(new Button("Создать NPC", _ => KitClient.Spawn(selected.Identifier.Value, KitClient.MenuSpawnPos(), false, false)) { Enabled = client.Allows(InteractionAction.SpawnCharacter) });
            details.Add(new Button("Создать и управлять", _ => KitClient.Spawn(selected.Identifier.Value, KitClient.MenuSpawnPos(), true, false)) { Enabled = client.Allows(InteractionAction.ControlCharacter) });
            details.Add(new Button("Создать враждебного NPC", _ => Confirm("Создать враждебного NPC?", () => KitClient.Spawn(selected.Identifier.Value, KitClient.MenuSpawnPos(), false, true))) { Enabled = client.Allows(InteractionAction.SpawnCharacter) });
            if (entry.Kit) {
                details.Add(new Button("Экспортировать профессию", _ => KitClient.ExportKit(selected.Identifier.Value)) { Enabled = client.Allows(InteractionAction.ManageKits) });
                details.Add(new Button("Удалить Kit", _ => Confirm("Удалить " + KitLabel(selected) + "?", () => KitClient.DeleteKit(selected.Identifier.Value))) { Enabled = client.Allows(InteractionAction.ManageKits) });
            }
        }
        refreshKits = () => {
            entries.Clear(); var ids = new HashSet<Identifier>();
            foreach (var pair in KitStore.All) { if (pair.Value.Prefab != null && ids.Add(pair.Key)) { entries.Add((pair.Value.Prefab, true)); } }
            foreach (var set in NPCSet.Sets) { foreach (var human in set.Humans) { if (ids.Add(human.Identifier)) { entries.Add((human, false)); } } }
            entries.RemoveAll(e => KitLabel(e.Prefab).IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase) < 0);
            entries.Sort((a, b) => string.Compare(KitLabel(a.Prefab), KitLabel(b.Prefab), StringComparison.CurrentCulture));
            list.Count = entries.Count;
            ClearPreviews();
            if (entries.Count > 0) { Select(0); }
        };
        list.SelectionChanged += Select; search.Changed += _ => refreshKits(); refreshKits();
        left.Add(new Text("Сохранить управляемого персонажа как Kit"));
        var saveName = left.Add(new TextInput("", 64));
        left.Add(new Button("Сохранить персонажа", _ => KitClient.CreateKit(saveName.Text)) { Enabled = client.Allows(InteractionAction.ManageKits) });
    }
    private static string KitLabel(HumanPrefab prefab) => TextManager.Get("npctitle." + prefab.Identifier.Value).Fallback(prefab.Identifier.Value.Replace('_', ' ')).Value;
}

internal sealed class PortraitChoice : Button {
    private readonly CharacterInfo info;
    public PortraitChoice(CharacterInfo info, string label, Action<GUIComponent> action) : base(label, action) { this.info = info; }
    internal override void Paint(GUIRenderer renderer, float opacity) {
        renderer.Fill(Bounds, GUITheme.Control, opacity);
        renderer.Preview(info, new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, Math.Max(0, Bounds.Height - 24)), opacity);
        renderer.Border(Bounds, Selected ? GUITheme.Accent : Hovered ? GUITheme.EdgeLit : GUITheme.Border, opacity);
    }
}
internal sealed class ColorChoice : Button {
    private readonly Color color;
    public ColorChoice(Color color, Action<GUIComponent> action) : base("", action) { this.color = color; }
    internal override void Paint(GUIRenderer renderer, float opacity) {
        renderer.Fill(Bounds, color, opacity);
        renderer.Border(Bounds, Selected ? GUITheme.Accent : Hovered ? Color.White : GUITheme.Border, opacity);
    }
}
