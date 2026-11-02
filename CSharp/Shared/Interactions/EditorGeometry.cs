using System;

namespace PrimaryPackage.Interactions;

internal enum EditorAlignment : byte { Left, CenterX, Right, Top, CenterY, Bottom, DistributeX, DistributeY }
internal readonly record struct EditorRect(int X, int Y, int Width, int Height);
internal readonly record struct EditorPoint(int X, int Y);

// Submarine-local, Y-up integer geometry. The engine adapter owns validation of collisions and references.
internal static class EditorGeometry {
    internal static int Snap(float value, int grid) {
        if (!float.IsFinite(value) || Math.Abs(value) > 10000000 || grid is < 1 or > 4096) { throw new ArgumentOutOfRangeException(); }
        return checked((int)MathF.Round(value / grid, MidpointRounding.AwayFromZero) * grid);
    }
    internal static void Arrange(ReadOnlySpan<EditorRect> input, Span<EditorPoint> output, EditorAlignment mode) {
        if (input.Length is < 2 or > WorldEditJournal.MaxBatch || output.Length != input.Length || !Enum.IsDefined(mode)) { throw new ArgumentException("Выберите от 2 до 64 объектов"); }
        long left = long.MaxValue, right = long.MinValue, top = long.MinValue, bottom = long.MaxValue;
        foreach (var rect in input) {
            if (rect.Width <= 0 || rect.Height <= 0 || Math.Abs((long)rect.X) > 10000000 || Math.Abs((long)rect.Y) > 10000000 || rect.Width > 10000000 || rect.Height > 10000000) { throw new ArgumentException("Неверные границы выделения"); }
            left = Math.Min(left, rect.X); right = Math.Max(right, (long)rect.X + rect.Width);
            top = Math.Max(top, rect.Y); bottom = Math.Min(bottom, (long)rect.Y - rect.Height);
        }
        for (int i = 0; i < input.Length; i++) {
            var rect = input[i]; long x = rect.X, y = rect.Y;
            switch (mode) {
                case EditorAlignment.Left: x = left; break;
                case EditorAlignment.Right: x = right - rect.Width; break;
                case EditorAlignment.CenterX: x = RoundHalf(left + right - rect.Width); break;
                case EditorAlignment.Top: y = top; break;
                case EditorAlignment.Bottom: y = bottom + rect.Height; break;
                case EditorAlignment.CenterY: y = RoundHalf(top + bottom + rect.Height); break;
            }
            output[i] = Point(x, y);
        }
        if (mode is not (EditorAlignment.DistributeX or EditorAlignment.DistributeY)) { return; }
        if (input.Length < 3) { throw new ArgumentException("Для распределения нужны как минимум три объекта"); }
        bool horizontal = mode == EditorAlignment.DistributeX;
        Span<int> order = stackalloc int[input.Length];
        for (int i = 0; i < order.Length; i++) { order[i] = i; }
        // At most 64 entries: stable insertion sort avoids an allocating comparer/closure.
        for (int i = 1; i < order.Length; i++) {
            int id = order[i], j = i - 1; long center = Center(input[id], horizontal);
            while (j >= 0 && Center(input[order[j]], horizontal) > center) { order[j + 1] = order[j--]; }
            order[j + 1] = id;
        }
        long start = Center(input[order[0]], horizontal), extent = Center(input[order[^1]], horizontal) - start;
        for (int i = 1; i < order.Length - 1; i++) {
            int id = order[i]; var rect = input[id];
            // Keep both endpoints fixed; round only the final rational position, avoiding accumulated drift.
            long numerator = start * (order.Length - 1) + extent * i;
            numerator += (horizontal ? -rect.Width : rect.Height) * (long)(order.Length - 1);
            long position = DivideRound(numerator, 2L * (order.Length - 1));
            output[id] = horizontal ? Point(position, rect.Y) : Point(rect.X, position);
        }
    }
    private static long Center(in EditorRect rect, bool horizontal) => horizontal ? 2L * rect.X + rect.Width : 2L * rect.Y - rect.Height;
    private static long RoundHalf(long value) => DivideRound(value, 2);
    private static long DivideRound(long numerator, long denominator) => numerator >= 0 ? (numerator + denominator / 2) / denominator : -((-numerator + denominator / 2) / denominator);
    private static EditorPoint Point(long x, long y) => Math.Abs(x) <= 10000000 && Math.Abs(y) <= 10000000
        ? new((int)x, (int)y) : throw new ArgumentException("Результат выходит за границы координат мира");
}
