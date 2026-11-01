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
    internal void ToggleGMMessage() {
        if (gmMessageWindow is { Disposed: false, Closing: false }) { gmMessageWindow.Close(); return; }
        if (!client.Allows(InteractionAction.SendGMMessage)) { Error("Нет разрешения отправлять GM-сообщения"); return; }
        gmMessageWindow?.Dispose(); gmMessageWindow = OpenCentered("Сообщение игрокам · F3", 560, 680);
        var content = gmMessageWindow.Add(new ScrollView { FillHeight = true });
        var peers = client.Peers.ToArray();
        string[] names = new[] { "Всем игрокам" }.Concat(peers.Select(p => p.Name)).ToArray();
        var recipient = content.Add(new Dropdown(names));
        byte[] formats = { 3, 11, 10, 7, GMAnnouncement.Toast };
        var format = content.Add(new Dropdown(new[] { "Серверный чат", "Сообщение в игре · тип 11", "Серверное окно · тип 10", "Окно сообщения · тип 7", "Сценарная надпись внизу" }));
        var text = content.Add(new TextInput("", 512) { Placeholder = "Текст сообщения" });
        var preview = content.Add(new Text("Предварительный просмотр цвета") { Wrap = true, Height = 45 });
        var rgba = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 28 });
        TextInput[] channels = new TextInput[4];
        for (int i = 0; i < 4; i++) { channels[i] = rgba.Add(new TextInput("255", 3) { Width = 95, Placeholder = new[] { "R", "G", "B", "A" }[i] }); }
        Color ReadColor() {
            Span<byte> values = stackalloc byte[4];
            for (int i = 0; i < 4; i++) { if (!byte.TryParse(channels[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) { throw new ArgumentException("RGBA: введите целые числа от 0 до 255"); } }
            return new Color(values[0], values[1], values[2], values[3]);
        }
        void SetColor(Color color) { byte[] values = { color.R, color.G, color.B, color.A }; for (int i = 0; i < 4; i++) { channels[i].Text = values[i].ToString(CultureInfo.InvariantCulture); } preview.Color = color; preview.Invalidate(GUIDirty.Render); }
        foreach (var channel in channels) { channel.Changed += _ => { try { preview.Color = ReadColor(); preview.Invalidate(GUIDirty.Render); } catch (ArgumentException) { } }; }
        text.Changed += _ => preview.Text = text.Text.Length > 0 ? text.Text : "Предварительный просмотр цвета";
        var palette = content.Add(new GMColorPalette()); palette.Changed += color => SceneTry(() => { color.A = ReadColor().A; SetColor(color); });
        content.Add(new Text("Палитра: оттенок внизу, насыщенность и яркость сверху · RGBA: 0–255") { Wrap = true, Height = 42 });
        if (gmColorsStore == null) {
            var store = new AtomicStore<List<uint>>(Core.ModAssets.Resolve("Data/Interactions/gm-colors.client.json"), colors => { if (colors == null || colors.Count > 16) { throw new System.IO.InvalidDataException("Недопустимая палитра"); } });
            try {
                gmColors = store.Load(() => new List<uint>(), error => Core.ModLog.Error("Палитра восстановлена: " + error.Message));
                gmColorsStore = store;
            } catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) {
                gmMessageWindow.Close(); Error("Не удалось прочитать сохранённые цвета: " + error.Message); return;
            }
        }
        var saved = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        void RebuildSaved() {
            saved.Clear();
            for (int i = 0; i < gmColors.Count; i++) { uint packed = gmColors[i]; saved.Add(new Button((i + 1).ToString(), _ => SetColor(new Color(packed))) { Width = 27 }); }
        }
        RebuildSaved(); content.Add(new Button("Сохранить текущий цвет", _ => SceneTry(() => { uint packed = ReadColor().PackedValue; if (!gmColors.Contains(packed)) { if (gmColors.Count == 16) { gmColors.RemoveAt(0); } gmColors.Add(packed); gmColorsStore.Save(gmColors); RebuildSaved(); } })));
        var times = content.Add(new Panel { Flow = GUIFlow.Horizontal, Height = 28 });
        var hold = times.Add(new TextInput("5", 12) { Width = 140 }); var fadeIn = times.Add(new TextInput("0.2", 12) { Width = 140 }); var fadeOut = times.Add(new TextInput("0.5", 12) { Width = 140 });
        var timingHint = content.Add(new Text("Длительность · появление · исчезновение, с (для нижней надписи)") { Wrap = true, Height = 42 });
        void UpdateFormat() { bool toast = formats[format.SelectedIndex] == GMAnnouncement.Toast; times.Enabled = toast; times.Visible = toast; timingHint.Visible = toast; }
        format.SelectionChanged += _ => UpdateFormat(); UpdateFormat();
        GMAnnouncement Value() { Color color = ReadColor(); return new() { Id = Guid.NewGuid(), Target = recipient.SelectedIndex == 0 ? "" : peers[recipient.SelectedIndex - 1].Account,
            Text = text.Text, Format = formats[format.SelectedIndex], R = color.R, G = color.G, B = color.B, A = color.A, Hold = Number(hold), FadeIn = Number(fadeIn), FadeOut = Number(fadeOut) }; }
        var actions = gmMessageWindow.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        actions.Add(new Button("Предпросмотр", _ => SceneTry(() => client.Announce(Value(), true))));
        actions.Add(new Button("Отправить", _ => SceneTry(() => client.Announce(Value(), false))));
    }
}
