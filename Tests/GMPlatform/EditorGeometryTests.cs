using PrimaryPackage.Interactions;

internal static partial class Program {
    private static void EditorGeometryCases() {
        Check(EditorGeometry.Snap(8, 16) == 16 && EditorGeometry.Snap(-8, 16) == -16 && EditorGeometry.Snap(-7.9f, 16) == 0, "grid snapping is symmetric about submarine-local origin");
        Reject<ArgumentOutOfRangeException>(() => EditorGeometry.Snap(float.NaN, 16), "nonfinite drag delta cannot reach integer geometry");
        Reject<ArgumentOutOfRangeException>(() => EditorGeometry.Snap(1, 0), "zero grid size rejected");
        var input = new[] { new EditorRect(-50, 100, 20, 30), new EditorRect(30, 40, 60, 20), new EditorRect(110, -20, 10, 40) };
        var output = new EditorPoint[input.Length];
        EditorGeometry.Arrange(input, output, EditorAlignment.Left); Check(output.All(p => p.X == -50) && output[1].Y == 40, "left alignment preserves independent Y coordinates");
        EditorGeometry.Arrange(input, output, EditorAlignment.Right); Check(output[0].X == 100 && output[1].X == 60 && output[2].X == 110, "right alignment includes item widths");
        EditorGeometry.Arrange(input, output, EditorAlignment.CenterX); Check(output[0].X == 25 && output[1].X == 5 && output[2].X == 30, "horizontal centering accounts for different widths");
        EditorGeometry.Arrange(input, output, EditorAlignment.Top); Check(output.All(p => p.Y == 100) && output[0].X == -50, "top alignment uses Y-up submarine coordinates");
        EditorGeometry.Arrange(input, output, EditorAlignment.Bottom); Check(output[0].Y == -30 && output[1].Y == -40 && output[2].Y == -20, "bottom alignment includes item heights");
        EditorGeometry.Arrange(input, output, EditorAlignment.CenterY); Check(output[0].Y == 35 && output[1].Y == 30 && output[2].Y == 40, "vertical centering respects Y-up rectangles");
        EditorGeometry.Arrange(input, output, EditorAlignment.DistributeX);
        Check(output[0] == new EditorPoint(-50, 100) && output[2] == new EditorPoint(110, -20) && output[1] == new EditorPoint(8, 40), "distribution pins both endpoints and rounds only the final center position");
        EditorGeometry.Arrange(input, output, EditorAlignment.DistributeY);
        Check(output[0] == new EditorPoint(-50, 100) && output[2] == new EditorPoint(110, -20) && output[1] == new EditorPoint(30, 33), "vertical center distribution handles odd dimensions and negative positions");
        var tied = new[] { new EditorRect(-1, 0, 2, 2), new EditorRect(-2, 0, 4, 2), new EditorRect(9, 0, 2, 2) };
        EditorGeometry.Arrange(tied, output, EditorAlignment.DistributeX); Check(output[0].X == -1 && output[1].X == 3 && output[2].X == 9, "equal centers keep selection order and do not swap anchor identity");
        var two = input.AsSpan(0, 2).ToArray(); var twoOutput = new EditorPoint[2];
        Reject<ArgumentException>(() => EditorGeometry.Arrange(two, twoOutput, EditorAlignment.DistributeX), "distribution requires at least three objects");
        Reject<ArgumentException>(() => EditorGeometry.Arrange(input, twoOutput, EditorAlignment.Top), "output span must match the whole selection");
        var invalid = new[] { input[0], new EditorRect(10000000, 100, 10000000, 30) };
        Reject<ArgumentException>(() => EditorGeometry.Arrange(invalid, twoOutput, EditorAlignment.Right), "coordinate overflow is rejected before producing a sendable layout");
        var large = new EditorRect[64]; var positions = new EditorPoint[64];
        for (int i = 0; i < large.Length; i++) { large[i] = new(-5000 + i * 137, i * 40 - 1000, 32, 16); }
        for (int i = 0; i < 100; i++) { EditorGeometry.Arrange(large, positions, EditorAlignment.DistributeX); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { EditorGeometry.Arrange(large, positions, EditorAlignment.DistributeX); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "warmed arrangement math allocates zero bytes for 64-object selections");
        Check(large[0].X == positions[0].X && large[^1].X == positions[^1].X, "large distribution retains fixed endpoints");
    }
}
