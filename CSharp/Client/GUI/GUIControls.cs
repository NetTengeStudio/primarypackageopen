using System;
using System.Collections.Generic;
using System.Text;
using Barotrauma;
using EventInput;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PrimaryPackage.GUI;

public sealed class TextInput : GUIComponent, IKeyboardSubscriber {
    private int caret;
    private string composition = "";
    private int anchor = -1;
    private char highSurrogate;
    public int MaxLength { get; }
    public string Placeholder { get; set; } = "";
    public bool Selected { get; set; }
    internal int Caret => Math.Min(caret, Text.Length);
    internal int SelectionStart => anchor < 0 ? Caret : Math.Min(Caret, Math.Min(anchor, Text.Length));
    internal int SelectionLength => anchor < 0 ? 0 : Math.Abs(Caret - Math.Min(anchor, Text.Length));
    public override bool Interactive => true;
    public string DisplayText => composition.Length > 0 ? Text.Insert(Math.Min(caret, Text.Length), composition) : Text;
    public event Action<TextInput> Changed;
    public event Action<TextInput> Submitted;
    public TextInput(string text = "", int maxLength = 512) {
        if (maxLength < 1 || maxLength > 8192) { throw new ArgumentOutOfRangeException(nameof(maxLength)); }
        MaxLength = maxLength;
        Text = text.Length <= maxLength ? text : text.Substring(0, maxLength);
        caret = Text.Length;
    }
    public void ReceiveTextInput(char inputChar) {
        if (char.IsHighSurrogate(inputChar)) { highSurrogate = inputChar; return; }
        if (char.IsLowSurrogate(inputChar)) {
            if (highSurrogate != 0) { string pair = new string(new[] { highSurrogate, inputChar }); highSurrogate = '\0'; ReceiveTextInput(pair); }
            return;
        }
        highSurrogate = '\0';
        if (!char.IsControl(inputChar)) { ReceiveTextInput(inputChar.ToString()); }
    }
    public void ReceiveTextInput(string text) {
        composition = "";
        if (string.IsNullOrEmpty(text)) { return; }
        caret = Math.Min(caret, Text.Length);
        int count = Math.Min(text.Length, MaxLength - Text.Length + SelectionLength);
        if (count == 0) { return; }
        var builder = new StringBuilder(count);
        for (int i = 0; i < count; i++) {
            char c = text[i];
            if (c is '\r' or '\n' or '\t') { builder.Append(' '); }
            else if (char.IsHighSurrogate(c)) { if (i + 1 < count && char.IsLowSurrogate(text[i + 1])) { builder.Append(c); builder.Append(text[++i]); } }
            else if (!char.IsControl(c) && !char.IsLowSurrogate(c)) { builder.Append(c); }
        }
        if (builder.Length == 0) { return; }
        DeleteSelection();
        string clean = builder.ToString();
        Text = Text.Insert(caret, clean);
        caret += clean.Length;
        Changed?.Invoke(this);
    }
    public void ReceiveCommandInput(char command) {
        if (composition.Length > 0 && command is '\b' or '\r' or '\n') { return; }
        caret = Math.Min(caret, Text.Length);
        if (command == '\b' && SelectionLength > 0) { DeleteSelection(); Changed?.Invoke(this); }
        else if (command == '\b' && caret > 0) {
            int count = caret > 1 && char.IsLowSurrogate(Text[caret - 1]) && char.IsHighSurrogate(Text[caret - 2]) ? 2 : 1;
            Text = Text.Remove(caret - count, count);
            caret -= count;
            Changed?.Invoke(this);
        } else if (command is '\r' or '\n') { Submitted?.Invoke(this); }
        else if (command == 1) { anchor = 0; caret = Text.Length; Invalidate(GUIDirty.Render); }
        else if (command is (char)3 or (char)24 && SelectionLength > 0) {
            Clipboard.SetText(Text.Substring(SelectionStart, SelectionLength));
            if (command == 24) { DeleteSelection(); Changed?.Invoke(this); }
        }
        else if (command == 22) { ReceiveTextInput(Clipboard.GetText()); }
    }
    private void DeleteSelection() {
        int start = SelectionStart, length = SelectionLength;
        if (length > 0) { Text = Text.Remove(start, length); caret = start; }
        anchor = -1;
    }
    public void ReceiveSpecialInput(Keys key) {
        caret = Math.Min(caret, Text.Length);
        int previous = caret;
        var keyboard = PlayerInput.GetKeyboardState;
        bool extend = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        if (key == Keys.Left && caret > 0) { caret--; if (caret > 0 && char.IsLowSurrogate(Text[caret])) { caret--; } }
        else if (key == Keys.Right && caret < Text.Length) { caret++; if (caret < Text.Length && char.IsLowSurrogate(Text[caret])) { caret++; } }
        else if (key == Keys.Home) { caret = 0; }
        else if (key == Keys.End) { caret = Text.Length; }
        else if (key == Keys.Delete && SelectionLength > 0) { DeleteSelection(); Changed?.Invoke(this); }
        else if (key == Keys.Delete && caret < Text.Length) {
            int count = char.IsHighSurrogate(Text[caret]) && caret + 1 < Text.Length && char.IsLowSurrogate(Text[caret + 1]) ? 2 : 1;
            Text = Text.Remove(caret, count);
            Changed?.Invoke(this);
        }
        if (key is Keys.Left or Keys.Right or Keys.Home or Keys.End) { if (extend) { if (anchor < 0) { anchor = previous; } } else { anchor = -1; } }
        Invalidate(GUIDirty.Render);
    }
    internal void PlaceCaret(int x, bool extend) {
        if (extend) { if (anchor < 0) { anchor = Caret; } } else { anchor = -1; }
        caret = GUIRuntime.Instance.Renderer.TextIndex(this, x);
        Invalidate(GUIDirty.Render);
    }
    internal void EndEditing() { composition = ""; highSurrogate = '\0'; anchor = -1; Invalidate(GUIDirty.Render); }
    public void ReceiveEditingInput(string text, int start, int length) { composition = text ?? ""; Invalidate(GUIDirty.Content); }
    internal override void Paint(GUIRenderer renderer, float opacity) {
        renderer.Fill(Bounds, GUITheme.Control, opacity);
        renderer.Border(Bounds, Selected ? GUITheme.Accent : GUITheme.Border, opacity);
        renderer.InputText(this, opacity);
    }
    public override void Dispose() { Changed = null; Submitted = null; base.Dispose(); }
}

public class VirtualList : GUIComponent {
    internal override void FinishAnimations() { scroll.Finish(); base.FinishAnimations(); }
    internal override bool Wheel(float delta) { Scroll(delta); return true; }
    private int count, selected = -1;
    private int hoveredRow = -1, labelCursor;
    private readonly Dictionary<int, (string Text, int Slot)> labels = new(256);
    private readonly int[] labelSlots = new int[256];
    private readonly MotionValue scroll = GUIMotion.Shared.Create(0, MotionSettings.Scroll);
    public Func<int, string> Label { get; }
    public event Action<int> SelectionChanged;
    public override bool Interactive => true;
    public int Count { get => count; set { if (value < 0 || value > 1000000) { throw new ArgumentOutOfRangeException(nameof(value)); } count = value; if (selected >= count) { selected = -1; } ClampScroll(); Invalidate(GUIDirty.Content); } }
    public int SelectedIndex => selected;
    public float RowHeight { get; } = GUITheme.RowHeight;
    public VirtualList(Func<int, string> label, int height = 240) {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Height = height;
        Array.Fill(labelSlots, -1);
        scroll.Changed += ScrollChanged;
    }
    private void ScrollChanged(MotionValue value) => Invalidate(GUIDirty.Render);
    internal void Scroll(float delta) {
        float row = RowHeight * (GUIRuntime.Instance?.Layout.Scale ?? 1);
        scroll.MoveTo(Math.Clamp(scroll.Target - delta * row / 120 * 3, 0, Math.Max(0, count * row - Bounds.Height)));
    }
    private void ClampScroll() {
        float row = RowHeight * (GUIRuntime.Instance?.Layout.Scale ?? 1);
        scroll.MoveTo(Math.Clamp(scroll.Target, 0, Math.Max(0, count * row - Bounds.Height)));
    }
    internal void Pick(int y) {
        int row = Math.Max(1, (int)(RowHeight * (GUIRuntime.Instance?.Layout.Scale ?? 1)));
        Select((int)((y - Bounds.Y + scroll.Position) / row));
    }
    internal void HoverAt(int y) {
        int row = Math.Max(1, (int)(RowHeight * (GUIRuntime.Instance?.Layout.Scale ?? 1)));
        double offset = Math.Clamp(scroll.Position, 0, Math.Max(0, count * (double)row - Bounds.Height));
        int next = (int)((y - Bounds.Y + offset) / row);
        if ((uint)next >= (uint)count) { next = -1; }
        if (next != hoveredRow) { hoveredRow = next; Invalidate(GUIDirty.Style); }
    }
    public void InvalidateRow(int index) { labels.Remove(index); Invalidate(GUIDirty.Render); }
    private string RowLabel(int index) {
        if (labels.TryGetValue(index, out var cached)) { return cached.Text; }
        int evicted = labelSlots[labelCursor];
        if (evicted >= 0 && labels.TryGetValue(evicted, out cached) && cached.Slot == labelCursor) { labels.Remove(evicted); }
        string text = Label(index) ?? "";
        labels[index] = (text, labelCursor);
        labelSlots[labelCursor] = index;
        labelCursor = (labelCursor + 1) & 255;
        return text;
    }
    public void Select(int index) {
        if (index < 0 || index >= count) { return; }
        selected = index;
        float row = RowHeight * (GUIRuntime.Instance?.Layout.Scale ?? 1);
        double top = index * row, bottom = top + row;
        if (top < scroll.Target) { scroll.MoveTo(top); }
        else if (bottom > scroll.Target + Bounds.Height) { scroll.MoveTo(Math.Max(0, bottom - Bounds.Height)); }
        Invalidate(GUIDirty.Style);
        SelectionChanged?.Invoke(index);
    }
    internal override void Navigate(int direction) => Select(Math.Clamp(selected + direction, 0, Math.Max(0, count - 1)));
    internal override void Paint(GUIRenderer renderer, float opacity) {
        if ((Dirty & GUIDirty.Content) != 0) { labels.Clear(); Array.Fill(labelSlots, -1); labelCursor = 0; }
        int row = Math.Max(1, (int)(RowHeight * renderer.Scale));
        double offset = Math.Clamp(scroll.Position, 0, Math.Max(0, count * (double)row - Bounds.Height));
        int first = Math.Max(0, (int)(offset / row));
        int last = Math.Min(count, first + Bounds.Height / row + 2);
        renderer.Fill(Bounds, GUITheme.Control, opacity);
        for (int i = first; i < last; i++) {
            Rectangle bounds = new(Bounds.X, Bounds.Y + i * row - (int)offset, Bounds.Width, row);
            if (i == selected) { renderer.Fill(bounds, GUITheme.Pressed, opacity); }
            else if (Hovered && i == hoveredRow) { renderer.Fill(bounds, Color.Lerp(GUITheme.Control, GUITheme.Hover, HoverAmount), opacity); }
            renderer.Write(RowLabel(i), new Rectangle(bounds.X + 6, bounds.Y, Math.Max(0, bounds.Width - 12), row), i == selected ? GUITheme.Accent : GUITheme.Text, opacity);
        }
    }
    public override void Dispose() { scroll.Dispose(); SelectionChanged = null; base.Dispose(); }
}

public sealed class Tabs : Panel {
    private readonly Panel header, content;
    private readonly System.Collections.Generic.List<Panel> pages = new();
    public int SelectedIndex { get; private set; } = -1;
    public Tabs() {
        header = Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        content = Add(new Panel { FillHeight = true });
    }
    public Panel AddTab(string label) {
        int index = pages.Count;
        var page = content.Add(new Panel { Visible = false, FillHeight = true });
        pages.Add(page);
        header.Add(new Button(label, _ => Select(index)));
        if (SelectedIndex < 0) { Select(index); }
        return page;
    }
    public void Select(int index) {
        if ((uint)index >= (uint)pages.Count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        if (SelectedIndex >= 0) { pages[SelectedIndex].Visible = false; }
        SelectedIndex = index;
        pages[index].Visible = true;
        for (int i = 0; i < header.Children.Count; i++) { if (header.Children[i] is Button button) { button.Selected = i == index; button.Invalidate(GUIDirty.Style); } }
    }
}

public class Dropdown : Button {
    private readonly string[] options;
    public int SelectedIndex { get; private set; }
    public event Action<int> SelectionChanged;
    public Dropdown(string[] options) : base(options.Length > 0 ? options[0] : "") {
        this.options = (string[])options.Clone();
        Enabled = options.Length > 0;
        Activated += Open;
    }
    public void Select(int index) {
        if ((uint)index >= (uint)options.Length) { throw new ArgumentOutOfRangeException(nameof(index)); }
        SelectedIndex = index;
        Text = options[index];
    }
    private void Open(GUIComponent source) {
        var window = GUIRuntime.Instance.Windows.OpenChild(Window, "Выбрать", new Vector2(Bounds.X, Bounds.Bottom), Math.Max(180, Bounds.Width / GUIRuntime.Instance.Layout.Scale));
        window.DismissOnOutside = true;
        var list = window.Add(new VirtualList(i => options[i], Math.Min(240, options.Length * 30)) { Count = options.Length });
        list.SelectionChanged += index => { Select(index); window.Close(); SelectionChanged?.Invoke(index); };
    }
    public override void Dispose() { SelectionChanged = null; base.Dispose(); }
}

public sealed class CharacterPreview : GUIComponent {
    public CharacterInfo CharacterInfo { get; }
    public CharacterPreview(CharacterInfo info) { CharacterInfo = info ?? throw new ArgumentNullException(nameof(info)); Height = 120; }
    internal override void Paint(GUIRenderer renderer, float opacity) => renderer.Preview(this, opacity);
}

public class ScrollView : Panel {
    internal override void FinishAnimations() { scroll.Finish(); base.FinishAnimations(); }
    internal override bool Wheel(float delta) { Scroll(delta); return true; }
    private readonly MotionValue scroll = GUIMotion.Shared.Create(0, MotionSettings.Scroll);
    private double maximum;
    internal Rectangle Viewport;
    internal double Offset => Math.Clamp(scroll.Position, 0, maximum);
    public ScrollView() { Height = 180; scroll.Changed += ScrollChanged; }
    private void ScrollChanged(MotionValue value) => Invalidate(GUIDirty.Layout);
    internal void SetRange(double maximum) {
        this.maximum = Math.Max(0, maximum);
        if (scroll.Target > this.maximum) { scroll.MoveTo(this.maximum); }
    }
    internal void Scroll(float delta) => scroll.MoveTo(Math.Clamp(scroll.Target - delta * 0.75, 0, maximum));
    internal void Reveal(Rectangle bounds) {
        if (bounds.Top < Viewport.Top) { scroll.MoveTo(Math.Clamp(Offset + bounds.Top - Viewport.Top, 0, maximum)); }
        else if (bounds.Bottom > Viewport.Bottom) { scroll.MoveTo(Math.Clamp(Offset + bounds.Bottom - Viewport.Bottom, 0, maximum)); }
    }
    internal override void Paint(GUIRenderer renderer, float opacity) {
        if (maximum <= 0 || Viewport.Height <= 0) { return; }
        int height = Math.Max(12, (int)(Viewport.Height * Viewport.Height / (Viewport.Height + maximum)));
        int y = Viewport.Y + (int)((Viewport.Height - height) * Offset / maximum);
        renderer.Fill(new Rectangle(Viewport.Right - 3, y, 3, height), GUITheme.Border, opacity);
    }
    public override void Dispose() { scroll.Dispose(); base.Dispose(); }
}

public sealed class List : VirtualList {
    public List(Func<int, string> label, int height = 240) : base(label, height) { }
}

public sealed class Selector : Panel {
    private readonly string[] options;
    private readonly System.Collections.Generic.List<int> matches = new();
    private readonly VirtualList list;
    public event Action<int> SelectionChanged;
    public Selector(string[] options) {
        this.options = (string[])options.Clone();
        var search = Add(new TextInput("", 80));
        list = Add(new VirtualList(index => this.options[matches[index]], 180));
        list.SelectionChanged += index => SelectionChanged?.Invoke(matches[index]);
        search.Changed += input => Filter(input.Text);
        Filter("");
    }
    private void Filter(string text) {
        matches.Clear();
        for (int i = 0; i < options.Length; i++) { if (options[i].IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0) { matches.Add(i); } }
        list.Count = matches.Count;
    }
    public override void Dispose() { SelectionChanged = null; base.Dispose(); }
}
