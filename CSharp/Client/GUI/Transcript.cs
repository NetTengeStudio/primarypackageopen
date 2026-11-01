using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.GUI;

public sealed class Transcript : GUIComponent {
    private readonly List<(string Text, bool Header)> lines = new();
    private readonly List<(string Header, string Body)> entries = new();
    private readonly List<int> starts = new();
    private int firstDirty;
    public long WrappedEntries { get; private set; }
    private readonly MotionValue offset = GUIMotion.Shared.Create(0, MotionSettings.Scroll);
    private int wrappedWidth = -1, lineHeight;
    private int textGeneration = -1;
    private float wrappedScale;
    private bool follow = true;
    public override bool Interactive => true;
    internal override void FinishAnimations() { offset.Finish(); base.FinishAnimations(); }
    public Transcript() { Height = 300; FillHeight = true; offset.Changed += OffsetChanged; }
    private void OffsetChanged(MotionValue value) => Invalidate(GUIDirty.Render);
    public void Replace(IReadOnlyList<(string Header, string Body)> values) {
        int common = 0;
        bool changed = entries.Count != values.Count;
        while (common < entries.Count && common < values.Count && entries[common].Body == values[common].Body) {
            if (entries[common].Header != values[common].Header) {
                if (common < starts.Count) { lines[starts[common]] = (values[common].Header, true); }
                changed = true;
            }
            common++;
        }
        if (common < values.Count) { changed = true; }
        firstDirty = Math.Min(firstDirty, common);
        entries.Clear();
        for (int i = 0; i < values.Count; i++) { entries.Add(values[i]); }
        if (changed) { Invalidate(GUIDirty.Content); }
    }
    public void Follow() { follow = true; Invalidate(GUIDirty.Render); }
    private double Maximum => Math.Max(0, lines.Count * (double)lineHeight - Bounds.Height);
    internal override bool Wheel(float delta) {
        double target = Math.Clamp(offset.Target - delta * .6, 0, Maximum);
        follow = target >= Maximum;
        offset.MoveTo(target);
        return true;
    }
    internal override void Navigate(int direction) => Wheel(-direction * 120);
    private void Wrap(GUIRenderer renderer, float scale) {
        if (wrappedWidth != Bounds.Width || wrappedScale != scale || textGeneration != renderer.TextGeneration) { firstDirty = 0; }
        int begin = Math.Min(firstDirty, starts.Count);
        int cut = begin < starts.Count ? starts[begin] : lines.Count;
        lines.RemoveRange(cut, lines.Count - cut);
        starts.RemoveRange(begin, starts.Count - begin);
        int width = Math.Max(1, Bounds.Width - (int)(16 * scale));
        lineHeight = Math.Max(1, (int)Math.Ceiling(GUITheme.FontSize * scale * 1.25f) + 2);
        for (int i = begin; i < entries.Count; i++) {
            starts.Add(lines.Count); WrappedEntries++;
            lines.Add((entries[i].Header, true));
            string text = entries[i].Body;
            int start = 0;
            while (start < text.Length) {
                int low = 1, high = text.Length - start, count = 1;
                while (low <= high) {
                    int middle = low + (high - low) / 2;
                    if (renderer.Measure(text.Substring(start, middle)).X * scale <= width) { count = middle; low = middle + 1; }
                    else { high = middle - 1; }
                }
                if (start + count < text.Length) {
                    int space = text.LastIndexOf(' ', start + count - 1, count);
                    if (space > start) { count = space - start; }
                    if (char.IsHighSurrogate(text[start + count - 1])) { count = Math.Max(2, count - 1); }
                }
                lines.Add((text.Substring(start, count), false));
                start += count;
                while (start < text.Length && text[start] == ' ') { start++; }
            }
            lines.Add(("", false));
        }
        wrappedWidth = Bounds.Width; wrappedScale = scale; firstDirty = entries.Count;
        textGeneration = renderer.TextGeneration;
        if (follow) { offset.MoveTo(Maximum); offset.Finish(); }
        else if (offset.Target > Maximum) { offset.MoveTo(Maximum); }
    }
    internal override void Paint(GUIRenderer renderer, float opacity) {
        float scale = renderer.Scale;
        if (scale <= 0) { scale = 1; }
        if (wrappedWidth != Bounds.Width || wrappedScale != scale || textGeneration != renderer.TextGeneration || firstDirty < entries.Count || starts.Count > entries.Count) { Wrap(renderer, scale); }
        renderer.Fill(Bounds, GUITheme.Control, opacity);
        double scroll = Math.Clamp(offset.Position, 0, Maximum);
        if (follow && offset.Target != Maximum) { offset.MoveTo(Maximum); }
        int first = Math.Max(0, (int)(scroll / Math.Max(1, lineHeight)));
        int last = Math.Min(lines.Count, first + Bounds.Height / Math.Max(1, lineHeight) + 2);
        for (int i = first; i < last; i++) {
            renderer.Write(lines[i].Text, new Rectangle(Bounds.X + 6, Bounds.Y + i * lineHeight - (int)scroll, Math.Max(0, Bounds.Width - 16), lineHeight), lines[i].Header ? GUITheme.Muted : GUITheme.Text, opacity);
        }
        if (Maximum > 0) {
            int height = Math.Max(12, (int)(Bounds.Height * Bounds.Height / (Bounds.Height + Maximum)));
            renderer.Fill(new Rectangle(Bounds.Right - 4, Bounds.Y + (int)((Bounds.Height - height) * scroll / Maximum), 3, height), GUITheme.Border, opacity);
        }
    }
    public override void Dispose() { offset.Dispose(); entries.Clear(); lines.Clear(); starts.Clear(); base.Dispose(); }
}
