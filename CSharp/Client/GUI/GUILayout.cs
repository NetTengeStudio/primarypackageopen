using System;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.GUI;

public sealed class GUILayout {
    public long ArrangedNodes { get; private set; }
    public float Scale { get; private set; } = 1;
    public Point Viewport { get; private set; }
    public bool Resize(int width, int height, float scale) {
        scale = Math.Clamp(scale, 0.25f, 4);
        if (Viewport.X == width && Viewport.Y == height && Scale == scale) { return false; }
        Viewport = new Point(Math.Max(1, width), Math.Max(1, height));
        Scale = scale;
        return true;
    }

    internal void Arrange(GUIWindow window) {
        window.RebuildCommands = true;
        int padding = Math.Max(1, (int)(GUITheme.Padding * Scale));
        int edge = Math.Max(1, (int)MathF.Round(GUITheme.Edge * Scale));
        int title = (int)(GUITheme.TitleHeight * Scale) + edge;
        int width = Math.Min(Viewport.X, Math.Max(1, (int)(window.Width * Scale)));
        int height = window.Height > 0 ? (int)(window.Height * Scale) : Measure(window) + title + padding * 2 + edge;
        height = Math.Min(Viewport.Y, Math.Max(title + padding * 2, height));
        int x = (int)Math.Clamp((double)window.Anchor.X, 0, Math.Max(0, Viewport.X - width));
        int y = (int)Math.Clamp((double)window.Anchor.Y, 0, Math.Max(0, Viewport.Y - height));
        var bounds = new Rectangle(x, y, width, height);
        window.Bounds = window.Clip = bounds;
        var content = new Rectangle(x + edge + padding, y + title + padding, Math.Max(0, width - (padding + edge) * 2), Math.Max(0, height - title - padding * 2 - edge));
        ArrangeChildren(window, content, content);
        window.WindowDirty &= ~GUIDirty.Layout;
    }

    private int Measure(GUIComponent component) {
        if (!component.MeasureDirty && component.MeasuredScale == Scale) { return component.MeasuredHeight; }
        if (component is VirtualList) { return (int)(component.Height * Scale); }
        int result = 0, count = 0;
        for (int i = 0; i < component.Children.Count; i++) {
            var child = component.Children[i];
            if (!child.Visible || child.Disposed) { continue; }
            int height = child.Height > 0 ? (int)(child.Height * Scale) : Measure(child);
            result = component.Flow == GUIFlow.Vertical ? result + height : Math.Max(result, height);
            count++;
        }
        component.MeasuredHeight = result + (component.Flow == GUIFlow.Vertical ? Math.Max(0, count - 1) * (int)(GUITheme.Gap * Scale) : 0);
        component.MeasuredScale = Scale;
        component.MeasureDirty = false;
        return component.MeasuredHeight;
    }

    private void ArrangeChildren(GUIComponent parent, Rectangle area, Rectangle clip) {
        int offset = 0, gap = Math.Max(1, (int)(GUITheme.Gap * Scale)), flexible = 0, fixedWidth = 0, visibleCount = 0;
        int fixedHeight = 0, flexibleHeight = 0, verticalCount = 0;
        for (int i = 0; i < parent.Children.Count; i++) {
            var child = parent.Children[i];
            if (!child.Visible || child.Disposed) { continue; }
            verticalCount++;
            if (child.FillHeight) { flexibleHeight++; }
            else { fixedHeight += child.Height > 0 ? (int)(child.Height * Scale) : Measure(child); }
        }
        int heightShare = flexibleHeight == 0 ? 0 : Math.Max(0, area.Height - fixedHeight - Math.Max(0, verticalCount - 1) * gap) / flexibleHeight;
        if (parent is ScrollView scroll) {
            scroll.SetRange(Measure(parent) - area.Height);
            scroll.Viewport = area;
            offset = -(int)scroll.Offset;
        }
        if (parent.Flow == GUIFlow.Horizontal) {
            for (int i = 0; i < parent.Children.Count; i++) {
                var child = parent.Children[i];
                if (!child.Visible || child.Disposed) { continue; }
                visibleCount++;
                if (child.Width == 0) { flexible++; } else { fixedWidth += (int)(child.Width * Scale); }
            }
        }
        int available = Math.Max(0, area.Width - Math.Max(0, visibleCount - 1) * gap);
        float fixedScale = fixedWidth + flexible * 40 * Scale > available ? available / Math.Max(1f, fixedWidth + flexible * 40 * Scale) : 1;
        int share = flexible > 0 ? Math.Max(0, available - (int)(fixedWidth * fixedScale)) / flexible : 0;
        for (int i = 0; i < parent.Children.Count; i++) {
            var child = parent.Children[i];
            if (!child.Visible || child.Disposed) { continue; }
            int height = child.FillHeight ? parent.Flow == GUIFlow.Vertical ? heightShare : area.Height : child.Height > 0 ? (int)(child.Height * Scale) : Measure(child);
            int width = child.Width > 0 ? (int)(child.Width * Scale * (parent.Flow == GUIFlow.Horizontal ? fixedScale : 1)) : parent.Flow == GUIFlow.Horizontal ? share : area.Width;
            Rectangle bounds = parent.Flow switch {
                GUIFlow.Horizontal => new Rectangle(area.X + offset, area.Y, width, Math.Min(height, area.Height)),
                GUIFlow.Overlay => new Rectangle(area.X, area.Y, width, height),
                _ => new Rectangle(area.X, area.Y + offset, Math.Min(width, area.Width), height)
            };
            Rectangle intersection = Rectangle.Intersect(bounds, clip);
            bool changed = child.Bounds != bounds || child.Clip != intersection;
            if (changed || (child.Dirty & GUIDirty.Layout) != 0) {
                child.Bounds = bounds;
                child.Clip = intersection;
                ArrangedNodes++;
                if (child is not VirtualList) { ArrangeChildren(child, bounds, intersection); }
            }
            child.Dirty &= ~GUIDirty.Layout;
            offset += (parent.Flow == GUIFlow.Horizontal ? width : height) + gap;
        }
    }
}
