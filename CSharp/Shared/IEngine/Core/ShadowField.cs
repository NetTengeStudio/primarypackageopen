using System;
using System.Runtime.CompilerServices;

namespace IEngine.Core;

public sealed class ShadowField
{
    public const int Bins = 4096;
    const float Scale = Bins / 4f;
    const float Slack = 0.5f;
    const float Eps = 1e-5f;
    const float None = float.MaxValue;

    static readonly float[] edgeX = new float[Bins + 1], edgeY = new float[Bins + 1];

    readonly float[] depth = new float[Bins], tree = new float[2 * Bins];
    readonly float[] headStart = new float[Bins], headDepth = new float[Bins], tailReach = new float[Bins], tailDepth = new float[Bins];
    float vx, vy, reach;
    int occluders;

    static ShadowField()
    {
        for (int e = 0; e <= Bins; e++)
        {
            Dir(e / Scale, out float x, out float y);
            edgeX[e] = x;
            edgeY[e] = y;
        }
    }

    public float ViewX => vx;
    public float ViewY => vy;
    public float Reach => reach;
    public int Occluders => occluders;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Pseudo(float dx, float dy)
    {
        float s = MathF.Abs(dx) + MathF.Abs(dy);
        if (s == 0) { return 0; }
        float p = dy >= 0 ? (dx >= 0 ? dy / s : 1 - dx / s) : (dx < 0 ? 2 - dy / s : 3 + dx / s);
        return p >= 4 ? 0 : p;
    }

    static void Dir(float p, out float x, out float y)
    {
        if (p < 1) { x = 1 - p; y = p; }
        else if (p < 2) { x = 1 - p; y = 2 - p; }
        else if (p < 3) { x = p - 3; y = 2 - p; }
        else { x = p - 3; y = p - 4; }
        float n = 1 / MathF.Sqrt(x * x + y * y);
        x *= n;
        y *= n;
    }

    public void Begin(float viewX, float viewY, float viewReach)
    {
        vx = viewX;
        vy = viewY;
        reach = viewReach;
        occluders = 0;
        depth.AsSpan().Fill(None);
        headStart.AsSpan().Fill(1);
        tailReach.AsSpan().Fill(0);
    }

    public void AddOccluder(float ax, float ay, float bx, float by, float far)
    {
        if (far < reach) { return; }
        float pa = Pseudo(ax - vx, ay - vy), pb = Pseudo(bx - vx, by - vy);
        float span = pb - pa;
        if (span < 0) { span += 4; }
        if (span > 2)
        {
            (ax, ay, bx, by) = (bx, by, ax, ay);
            (pa, span) = (pb, 4 - span);
        }
        if (span >= 2 - 1e-3f || span <= 0) { return; }
        float ex = bx - ax, ey = by - ay;
        float num = (ax - vx) * ey - (ay - vy) * ex;
        float lo = pa * Scale, hi = (pa + span) * Scale;
        int e0 = (int)MathF.Ceiling(lo), e1 = (int)MathF.Floor(hi);
        if (e1 < e0) { return; }
        occluders++;
        float prev = Ray(e0, num, ex, ey);
        for (int e = e0; e < e1; e++)
        {
            float next = Ray(e + 1, num, ex, ey);
            ref float d = ref depth[e & (Bins - 1)];
            d = MathF.Min(d, MathF.Max(prev, next));
            prev = next;
        }
        float first = Ray(e0, num, ex, ey);
        if (lo < e0)
        {
            int k = (e0 - 1) & (Bins - 1);
            float f = lo - (e0 - 1);
            if (f < headStart[k])
            {
                headStart[k] = f;
                headDepth[k] = MathF.Max(first, Distance(ax, ay));
            }
        }
        if (hi > e1)
        {
            int k = e1 & (Bins - 1);
            float f = hi - e1;
            if (f > tailReach[k])
            {
                tailReach[k] = f;
                tailDepth[k] = MathF.Max(prev, Distance(bx, by));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    float Ray(int e, float num, float ex, float ey)
    {
        int i = e & (Bins - 1);
        float t = num / (edgeX[i] * ey - edgeY[i] * ex);
        return t > 0 ? t : None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    float Distance(float x, float y) => MathF.Sqrt((x - vx) * (x - vx) + (y - vy) * (y - vy));

    public void End()
    {
        for (int k = 0; k < Bins; k++)
        {
            float d = depth[k];
            if (headStart[k] <= tailReach[k] + Eps) { d = MathF.Min(d, MathF.Max(headDepth[k], tailDepth[k])); }
            tree[Bins + k] = d;
        }
        for (int i = Bins - 1; i > 0; i--) { tree[i] = MathF.Max(tree[2 * i], tree[2 * i + 1]); }
    }

    public bool Covers(float x0, float y0, float x1, float y1)
    {
        if (occluders == 0 || (vx >= x0 && vx <= x1 && vy >= y0 && vy <= y1)) { return false; }
        float fx = MathF.Max(vx - x0, x1 - vx), fy = MathF.Max(vy - y0, y1 - vy);
        if (fx * fx + fy * fy > reach * reach) { return false; }
        var box = new Box(this, x0, y0, x1, y1);
        if (box.Dq + Slack < tree[Bins + ((int)(box.Pq * Scale) & (Bins - 1))]) { return false; }
        int kLo = (int)MathF.Floor(box.Lo * Scale), kHi = (int)MathF.Floor(box.Hi * Scale);
        float max = kLo < 0 ? MathF.Max(RangeMax(kLo + Bins, Bins - 1), RangeMax(0, kHi))
            : kHi >= Bins ? MathF.Max(RangeMax(kLo, Bins - 1), RangeMax(0, kHi - Bins))
            : RangeMax(kLo, kHi);
        if (max + Slack <= box.Dq) { return true; }
        if (max > box.Dmax) { return false; }
        if (kLo < 0) { return Check(ref box, 1, 0, Bins - 1, kLo + Bins, Bins - 1, -Bins) && Check(ref box, 1, 0, Bins - 1, 0, kHi, 0); }
        if (kHi >= Bins) { return Check(ref box, 1, 0, Bins - 1, kLo, Bins - 1, 0) && Check(ref box, 1, 0, Bins - 1, 0, kHi - Bins, Bins); }
        return Check(ref box, 1, 0, Bins - 1, kLo, kHi, 0);
    }

    float RangeMax(int l, int r)
    {
        float m = 0;
        for (l += Bins, r += Bins + 1; l < r; l >>= 1, r >>= 1)
        {
            if ((l & 1) != 0) { m = MathF.Max(m, tree[l++]); }
            if ((r & 1) != 0) { m = MathF.Max(m, tree[--r]); }
        }
        return m;
    }

    bool Check(ref Box box, int node, int nl, int nh, int ql, int qh, int shift)
    {
        if (nh < ql || nl > qh) { return true; }
        if (ql <= nl && nh <= qh || nl == nh)
        {
            if (tree[node] + Slack <= box.Bound(nl + shift, nh + 1 + shift)) { return true; }
            if (nl == nh) { return false; }
        }
        int mid = (nl + nh) >> 1;
        return Check(ref box, 2 * node, nl, mid, ql, qh, shift) && Check(ref box, 2 * node + 1, mid + 1, nh, ql, qh, shift);
    }

    struct Box
    {
        readonly float vx, vy, x0, y0, x1, y1;
        public readonly float Pq, Dq, Dmax, Lo, Hi;
        readonly float p0, p1, p2, p3, d0, d1, d2, d3;

        public Box(ShadowField f, float x0, float y0, float x1, float y1)
        {
            vx = f.vx;
            vy = f.vy;
            this.x0 = x0;
            this.y0 = y0;
            this.x1 = x1;
            this.y1 = y1;
            float qx = Math.Clamp(vx, x0, x1) - vx, qy = Math.Clamp(vy, y0, y1) - vy;
            Pq = Pseudo(qx, qy);
            Dq = MathF.Sqrt(qx * qx + qy * qy);
            p0 = Corner(Pq, x0 - vx, y0 - vy, out d0);
            p1 = Corner(Pq, x1 - vx, y0 - vy, out d1);
            p2 = Corner(Pq, x0 - vx, y1 - vy, out d2);
            p3 = Corner(Pq, x1 - vx, y1 - vy, out d3);
            Lo = MathF.Min(MathF.Min(p0, p1), MathF.Min(p2, p3));
            Hi = MathF.Max(MathF.Max(p0, p1), MathF.Max(p2, p3));
            Dmax = MathF.Max(MathF.Max(d0, d1), MathF.Max(d2, d3));
        }

        static float Corner(float pq, float dx, float dy, out float d)
        {
            d = MathF.Sqrt(dx * dx + dy * dy);
            float r = Pseudo(dx, dy) - pq;
            if (r > 2) { r -= 4; }
            else if (r <= -2) { r += 4; }
            return pq + r;
        }

        public readonly float Bound(int ea, int eb)
        {
            float pa = ea / Scale, pb = eb / Scale;
            bool clipA = pa <= Lo, clipB = pb >= Hi;
            pa = MathF.Max(pa, Lo) - Eps;
            pb = MathF.Min(pb, Hi) + Eps;
            if (Pq >= pa && Pq <= pb) { return Dq; }
            float b = MathF.Min(clipA ? None : Entry(ea), clipB ? None : Entry(eb));
            if (p0 >= pa && p0 <= pb) { b = MathF.Min(b, d0); }
            if (p1 >= pa && p1 <= pb) { b = MathF.Min(b, d1); }
            if (p2 >= pa && p2 <= pb) { b = MathF.Min(b, d2); }
            if (p3 >= pa && p3 <= pb) { b = MathF.Min(b, d3); }
            return b;
        }

        readonly float Entry(int e)
        {
            int i = e & (Bins - 1);
            float tMin = 0, tMax = None;
            if (!Slab(edgeX[i], x0 - vx, x1 - vx, ref tMin, ref tMax) || !Slab(edgeY[i], y0 - vy, y1 - vy, ref tMin, ref tMax)) { return None; }
            return tMin;
        }

        static bool Slab(float u, float a, float b, ref float tMin, ref float tMax)
        {
            if (u == 0) { return a <= 0 && b >= 0; }
            float t0 = a / u, t1 = b / u;
            if (t0 > t1) { (t0, t1) = (t1, t0); }
            tMin = MathF.Max(tMin, t0);
            tMax = MathF.Min(tMax, t1);
            return tMin <= tMax;
        }
    }
}
