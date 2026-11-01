using System;
using PrimaryPackage.GUI;

namespace PrimaryPackage.RPWheel;

internal sealed class WheelModel : IDisposable {
    public const int MaxSectors = 32;
    private const int ScaleChannel = 0;
    private const int AlphaChannel = 1;
    private const int CenterChannel = 2;
    private const int HoverChannel = 3;
    private readonly MotionValue[] motion;
    private double time;

    public int Count { get; }
    public float InnerRadius { get; }
    public float OuterRadius { get; }
    public float SectorAngle { get; }
    public bool Expanded { get; private set; }
    public bool CenterHovered { get; private set; }
    public int Hovered { get; private set; } = -1;
    public float Scale => (float)motion[ScaleChannel].Position;
    public float Alpha => (float)motion[AlphaChannel].Position;
    public float Center => (float)motion[CenterChannel].Position;
    public bool Visible => Expanded || Alpha > 0.0001f;
    public float Hover(int index) => (float)motion[HoverChannel + index].Position;

    public WheelModel(int count) {
        Count = Math.Clamp(count, 0, MaxSectors);
        float radius = 100 + Count * 5;
        InnerRadius = radius * 0.5f;
        OuterRadius = radius * 1.5f;
        SectorAngle = Count > 0 ? MathF.Tau / Count : MathF.Tau;
        motion = new MotionValue[HoverChannel + Count];
        for (int i = 0; i < motion.Length; i++) {
            motion[i] = GUIMotion.Shared.Create(0, new MotionSettings(i == AlphaChannel ? .132 : i == CenterChannel ? .18 : .22));
        }
    }

    public void Advance(double now) {
        if (!double.IsFinite(now)) { throw new ArgumentOutOfRangeException(nameof(now)); }
        time = Math.Max(time, now);
        GUIMotion.Shared.Advance(time);
    }

    public void Open(double now) {
        Advance(now);
        if (Count == 0 || Expanded) { return; }
        Expanded = true;
        motion[ScaleChannel].MoveTo(1);
        motion[AlphaChannel].MoveTo(1);
    }

    public void Close(double now) {
        Advance(now);
        if (!Expanded) { return; }
        Expanded = false;
        motion[ScaleChannel].MoveTo(0);
        motion[AlphaChannel].MoveTo(0);
        Select(-1, false);
    }

    public void Pointer(double now, float x, float y, float uiScale) {
        Advance(now);
        if (!Expanded) { return; }
        float radius = InnerRadius * uiScale;
        bool center = x * x + y * y < radius * radius;
        int sector = -1;
        if (!center && Count > 0) {
            float angle = MathF.Atan2(-y, x) - MathF.PI * 0.5f;
            if (angle < 0) { angle += MathF.Tau; }
            sector = Math.Clamp((int)(angle / SectorAngle), 0, Count - 1);
        }
        Select(sector, center);
    }

    public int Input(double now, bool held, bool escape) {
        Advance(now);
        if (!Expanded || held && !escape) { return -1; }
        int selected = escape ? -1 : Hovered;
        Close(time);
        return selected;
    }

    public void Reset() {
        for (int i = 0; i < motion.Length; i++) { motion[i].MoveTo(0); motion[i].Finish(); }
        time = 0;
        Expanded = false;
        Hovered = -1;
        CenterHovered = false;
    }

    private void Select(int sector, bool center) {
        if (Hovered != sector) {
            if (Hovered >= 0) { motion[HoverChannel + Hovered].MoveTo(0); }
            if (sector >= 0) { motion[HoverChannel + sector].MoveTo(1); }
            Hovered = sector;
        }
        if (CenterHovered != center) {
            CenterHovered = center;
            motion[CenterChannel].MoveTo(center ? 1 : 0);
        }
    }
    public void Dispose() { for (int i = 0; i < motion.Length; i++) { motion[i].Dispose(); } }
}
