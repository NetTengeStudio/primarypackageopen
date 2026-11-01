using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.RPWheel;

internal sealed class WheelArc {
    public readonly Vector2[] Units;

    public WheelArc(float from, float to) {
        int count = Math.Max(2, (int)((to - from) / (MathF.Tau / 1024)) + 1);
        Units = new Vector2[count];
        float step = (to - from) / (count - 1);
        for (int i = 0; i < count; i++) {
            float angle = from + step * i;
            Units[i] = new Vector2(MathF.Cos(angle), -MathF.Sin(angle));
        }
    }
}

internal sealed class WheelRenderer : IDisposable {
    private readonly VertexPositionColor[] vertices;
    private DynamicVertexBuffer buffer;
    private int offset;
    public int Count { get; private set; }

    public WheelRenderer(int capacity) => vertices = new VertexPositionColor[capacity];

    public void Rewind(int count) {
        if ((uint)count > (uint)Count) { throw new ArgumentOutOfRangeException(nameof(count)); }
        Count = count;
    }

    public void Flush(GraphicsDevice device) {
        if (Count < 3) { return; }
        if (buffer == null || buffer.IsDisposed || buffer.IsContentLost || buffer.GraphicsDevice != device) {
            buffer?.Dispose();
            buffer = new DynamicVertexBuffer(device, VertexPositionColor.VertexDeclaration, vertices.Length * 3, BufferUsage.WriteOnly);
            offset = 0;
        }
        SetDataOptions options = SetDataOptions.NoOverwrite;
        if (offset == 0 || Count > buffer.VertexCount - offset) {
            offset = 0;
            options = SetDataOptions.Discard;
        }
        buffer.SetData(offset * VertexPositionColor.VertexDeclaration.VertexStride, vertices, 0, Count,
            VertexPositionColor.VertexDeclaration.VertexStride, options);
        device.SetVertexBuffer(buffer);
        device.DrawPrimitives(PrimitiveType.TriangleStrip, offset, Count - 2);
        offset += Count;
    }

    public void Dispose() {
        buffer?.Dispose();
        buffer = null;
        offset = 0;
    }

    public void Strip(ReadOnlySpan<VertexPositionColor> strip) {
        if (strip.Length < 4 || (strip.Length & 1) != 0) { throw new ArgumentException(nameof(strip)); }
        Join(in strip[0], strip.Length);
        strip.CopyTo(vertices.AsSpan(Count));
        Count += strip.Length;
    }

    public void Annulus(Vector2 center, float inner, float outer, WheelArc arc, Color color, float feather = 1) {
        inner = MathF.Max(0, inner);
        Color clear = new Color(color.R, color.G, color.B, (byte)0);
        if (inner > 0.001f) {
            Band(center, inner, MathF.Max(0, inner - feather), arc.Units, color, clear);
        }
        Band(center, outer, inner, arc.Units, color, color);
        Band(center, outer + feather, outer, arc.Units, clear, color);
    }

    public void Glow(Vector2 center, float inner, float outer, WheelArc arc, float hover, float alpha) {
        if (hover < 0.01f) { return; }
        Color full = new Color(Theme.GlowBase.R, Theme.GlowBase.G, Theme.GlowBase.B,
            (byte)(Theme.GlowAlphaMax * 255 * hover * alpha));
        Color clear = new Color(full.R, full.G, full.B, (byte)0);
        Band(center, inner, inner - Theme.GlowExpandInner * hover, arc.Units, full, clear);
        Band(center, outer, inner, arc.Units, full, full);
        Band(center, outer + Theme.GlowExpandOuter * hover, outer, arc.Units, clear, full);
    }

    public void Border(Vector2 center, float radius, WheelArc arc, Color color, float thickness) =>
        Annulus(center, radius - thickness * 0.5f, radius + thickness * 0.5f, arc, color);

    public void Line(Vector2 a, Vector2 b, float halfWidth, Color color) {
        Vector2 direction = b - a;
        float length = direction.Length();
        if (length < 0.001f) { return; }
        Vector2 normal = new Vector2(-direction.Y, direction.X) / length;
        Vector2 solid = normal * halfWidth;
        Vector2 edge = normal * (halfWidth + 1);
        Color clear = new Color(color.R, color.G, color.B, (byte)0);
        Quad(a - edge, a - solid, b - edge, b - solid, clear, color, clear, color);
        Quad(a - solid, a + solid, b - solid, b + solid, color, color, color, color);
        Quad(a + solid, a + edge, b + solid, b + edge, color, clear, color, clear);
    }

    private void Band(Vector2 center, float outer, float inner, ReadOnlySpan<Vector2> units, Color outerColor, Color innerColor) {
        var first = new VertexPositionColor(new Vector3(center + units[0] * outer, 0), outerColor);
        Join(in first, units.Length * 2);
        Span<VertexPositionColor> output = vertices.AsSpan(Count, units.Length * 2);
        for (int i = 0; i < units.Length; i++) {
            ref VertexPositionColor outside = ref output[i * 2];
            ref VertexPositionColor inside = ref output[i * 2 + 1];
            Vector2 unit = units[i];
            outside.Position.X = center.X + unit.X * outer;
            outside.Position.Y = center.Y + unit.Y * outer;
            outside.Position.Z = 0;
            outside.Color = outerColor;
            inside.Position.X = center.X + unit.X * inner;
            inside.Position.Y = center.Y + unit.Y * inner;
            inside.Position.Z = 0;
            inside.Color = innerColor;
        }
        Count += output.Length;
    }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color ca, Color cb, Color cc, Color cd) {
        var first = new VertexPositionColor(new Vector3(a, 0), ca);
        Join(in first, 4);
        vertices[Count++] = first;
        vertices[Count++] = new VertexPositionColor(new Vector3(b, 0), cb);
        vertices[Count++] = new VertexPositionColor(new Vector3(c, 0), cc);
        vertices[Count++] = new VertexPositionColor(new Vector3(d, 0), cd);
    }

    private void Join(in VertexPositionColor first, int count) {
        int join = Count == 0 ? 0 : 2;
        if (count > vertices.Length - Count - join) { throw new InvalidOperationException("Wheel geometry capacity exceeded"); }
        if (join == 0) { return; }
        vertices[Count] = vertices[Count - 1];
        vertices[Count + 1] = first;
        Count += 2;
    }
}
