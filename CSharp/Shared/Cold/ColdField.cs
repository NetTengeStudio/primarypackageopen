using System;
using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public interface IColdSource {
        Hull ColdHull { get; }
        float ColdTarget { get; }
        float ColdRate { get; }
    }

    public interface IColdArea {
        void ApplyColdArea(float deltaTime);
    }

    public static class ColdField {
        private struct Edge {
            public int A;
            public int B;
            public Gap Gap;
        }

        private static readonly Dictionary<Hull, int> index = new Dictionary<Hull, int>();
        private static readonly List<IColdSource> sources = new List<IColdSource>();
        private static readonly List<IColdArea> areas = new List<IColdArea>();
        private static readonly Dictionary<ushort, float> restored = new Dictionary<ushort, float>();
        private static readonly Dictionary<Hull, float> carry = new Dictionary<Hull, float>();
        private static readonly HashSet<long> pairs = new HashSet<long>();

        private static Hull[] hulls = Array.Empty<Hull>();
        private static float[] temperature = Array.Empty<float>();
        private static float[] shown = Array.Empty<float>();
        private static float[] next = Array.Empty<float>();
        private static float[] invCapacity = Array.Empty<float>();
        private static float[] baseCapacity = Array.Empty<float>();
        private static int[] group = Array.Empty<int>();
        private static int[] degree = Array.Empty<int>();
        private static Edge[] edges = Array.Empty<Edge>();

        private static int hullCount;
        private static int edgeCount;
        private static int knownHulls = -1;
        private static int knownGaps = -1;
        private static float tickTimer;

        public static int Count => hullCount;
        public static int Version { get; private set; }
        public static Hull HullAt(int i) => hulls[i];
        public static float Raw(int i) => temperature[i];
        public static float Smoothed(int i) => shown[i];
        public static bool Dirty { get; set; }
        public static float LastStep { get; private set; }

        public static void Register(IColdSource source) {
            if (!sources.Contains(source)) { sources.Add(source); }
        }

        public static void Unregister(IColdSource source) => sources.Remove(source);

        public static void Register(IColdArea area) {
            if (!areas.Contains(area)) { areas.Add(area); }
        }

        public static void Unregister(IColdArea area) => areas.Remove(area);

        public static void Reset() {
            index.Clear();
            restored.Clear();
            carry.Clear();
            hullCount = 0;
            edgeCount = 0;
            knownHulls = -1;
            knownGaps = -1;
            tickTimer = 0.0f;
        }

        public static void Restore(ushort hullId, float value) => restored[hullId] = value;

        public static int IndexOf(Hull hull) =>
            hull != null && index.TryGetValue(hull, out int i) ? i : -1;

        public static float Get(Hull hull) {
            int i = IndexOf(hull);
            return i < 0 ? ColdConfig.BaseTemperature : temperature[i];
        }

        public static float GetSmoothed(Hull hull) {
            int i = IndexOf(hull);
            return i < 0 ? ColdConfig.BaseTemperature : shown[i];
        }

        public static void Set(Hull hull, float value) {
            int i = IndexOf(hull);
            if (i < 0) { return; }
            temperature[i] = Clamp(value);
        }

        public static void Nudge(Hull hull, float target, float rate, float deltaTime) {
            int i = IndexOf(hull);
            if (i < 0) { return; }
            float sizedRate = i < baseCapacity.Length && baseCapacity[i] > 0.0f
                ? rate * (ColdConfig.MinHullVolume / baseCapacity[i]) : rate;
            temperature[i] = Clamp(temperature[i] + (target - temperature[i]) * Response(sizedRate, deltaTime));
        }

        public static void Interpolate(float deltaTime) {
            EnsureGraph();
            float k = Response(ColdConfig.InterpolationRate, deltaTime);
            for (int i = 0; i < hullCount; i++) {
                shown[i] += (temperature[i] - shown[i]) * k;
            }
        }

        public static bool Advance(float deltaTime) {
            EnsureGraph();
            if (hullCount == 0) { return false; }
            tickTimer += deltaTime;
            if (tickTimer < ColdConfig.TickInterval) { return false; }
            float step = tickTimer;
            tickTimer = 0.0f;
            LastStep = step;
            Diffuse(step);
            for (int i = 0; i < areas.Count; i++) { areas[i].ApplyColdArea(step); }
            return true;
        }

        private static void Diffuse(float step) {
            float baseTemp = ColdConfig.BaseTemperature;
            float relax = Response(ColdConfig.RelaxRate, step);
            float conductance = ColdConfig.Conductance;

            Array.Copy(temperature, next, hullCount);
            float waterCapacity = ColdConfig.WaterHeatCapacity - 1.0f;
            for (int i = 0; i < hullCount; i++) {
                Hull hull = hulls[i];
                float capacity = baseCapacity[i];
                if (hull != null && waterCapacity > 0.0f && hull.Volume > 0.0f) {
                    float flooded = hull.WaterVolume / hull.Volume;
                    if (flooded > 1.0f) { flooded = 1.0f; } else if (flooded < 0.0f) { flooded = 0.0f; }
                    capacity *= 1.0f + flooded * waterCapacity;
                }
                invCapacity[i] = 1.0f / capacity;
            }

            for (int e = 0; e < edgeCount; e++) {
                ref Edge edge = ref edges[e];
                if (!EdgeConductance(ref edge, out float open, out float size)) { continue; }
                int a = edge.A;
                int b = edge.B;
                float flow = FlowRate(conductance, open, size, temperature[a], temperature[b]) * step;
                float da = flow * invCapacity[a];
                float db = -flow * invCapacity[b];
                float half = Math.Abs(temperature[b] - temperature[a]) * 0.5f;
                float capA = degree[a] > 1 ? half / degree[a] : half;
                float capB = degree[b] > 1 ? half / degree[b] : half;
                da = da > capA ? capA : da < -capA ? -capA : da;
                db = db > capB ? capB : db < -capB ? -capB : db;
                next[a] += da;
                next[b] += db;
            }

            for (int i = 0; i < hullCount; i++) {
                float value = next[i];
                temperature[i] = Clamp(value + (baseTemp - value) * relax);
            }

            for (int i = 0; i < sources.Count; i++) {
                IColdSource source = sources[i];
                Hull hull = source.ColdHull;
                if (hull == null) { continue; }
                Nudge(hull, source.ColdTarget, source.ColdRate, step);
            }
        }

        private static float FlowRate(float conductance, float open, float size, float from, float to) =>
            conductance * open * size * (to - from);

        private static bool EdgeConductance(ref Edge edge, out float open, out float size) {
            Gap gap = edge.Gap;
            if (gap == null) {
                open = 1.0f;
                size = ColdConfig.LinkArea;
                return size > 0.0f;
            }
            open = gap.Open;
            size = gap.Size;
            return !gap.Removed && open > 0.0f;
        }

        public static int EdgeCount => edgeCount;

        public static bool TryGetEdge(int e, out int a, out int b, out Gap gap) {
            a = -1;
            b = -1;
            gap = null;
            if (e < 0 || e >= edgeCount) { return false; }
            ref Edge edge = ref edges[e];
            gap = edge.Gap;
            if (gap != null && gap.Removed) { return false; }
            a = edge.A;
            b = edge.B;
            return hulls[a] != null && hulls[b] != null;
        }

        public static bool IsLinkEdge(int e) => e >= 0 && e < edgeCount && edges[e].Gap == null;

        public static float EdgeFlow(int e, bool smoothed) {
            if (e < 0 || e >= edgeCount) { return 0.0f; }
            ref Edge edge = ref edges[e];
            if (!EdgeConductance(ref edge, out float open, out float size)) { return 0.0f; }
            float[] src = smoothed ? shown : temperature;
            return FlowRate(ColdConfig.Conductance, open, size, src[edge.A], src[edge.B]);
        }

        public static void Touch() => EnsureGraph();

        public static int GroupOf(int i) => i >= 0 && i < hullCount ? group[i] : -1;

        private static void EnsureGraph() {
            int hullTotal = Hull.HullList.Count;
            int gapTotal = Gap.GapList.Count;
            if (!Dirty && hullTotal == knownHulls && gapTotal == knownGaps) { return; }
            Dirty = false;
            knownHulls = hullTotal;
            knownGaps = gapTotal;
            Rebuild();
        }

        private static void Rebuild() {
            Version++;
            carry.Clear();
            for (int i = 0; i < hullCount; i++) {
                Hull previous = hulls[i];
                if (previous != null && !previous.Removed) { carry[previous] = temperature[i]; }
            }
            index.Clear();
            hullCount = Hull.HullList.Count;
            Grow(ref hulls, hullCount);
            Grow(ref temperature, hullCount);
            Grow(ref shown, hullCount);
            Grow(ref next, hullCount);
            Grow(ref invCapacity, hullCount);
            Grow(ref baseCapacity, hullCount);
            Grow(ref group, hullCount);
            Grow(ref degree, hullCount);
            Array.Clear(degree, 0, hullCount);

            float minVolume = ColdConfig.MinHullVolume;
            float baseTemp = ColdConfig.BaseTemperature;
            for (int i = 0; i < hullCount; i++) {
                Hull hull = Hull.HullList[i];
                hulls[i] = hull;
                index[hull] = i;
                float volume = hull.Volume;
                if (volume < minVolume) { volume = minVolume; }
                baseCapacity[i] = volume;
                invCapacity[i] = 1.0f / volume;
                float start = carry.TryGetValue(hull, out float kept) ? kept
                    : restored.TryGetValue(hull.ID, out float saved) ? saved
                    : baseTemp;
                temperature[i] = start;
                shown[i] = start;
            }
            restored.Clear();
            carry.Clear();

            edgeCount = 0;
            pairs.Clear();
            EnsureEdgeCapacity(Gap.GapList.Count);
            for (int i = 0; i < Gap.GapList.Count; i++) {
                Gap gap = Gap.GapList[i];
                if (gap.Removed || gap.linkedTo.Count < 2) { continue; }
                if (gap.linkedTo[0] is not Hull a || gap.linkedTo[1] is not Hull b || a == b) { continue; }
                if (!index.TryGetValue(a, out int ia) || !index.TryGetValue(b, out int ib)) { continue; }
                AddEdge(ia, ib, gap);
            }

            for (int i = 0; i < hullCount; i++) { group[i] = i; }
            for (int i = 0; i < hullCount; i++) {
                Hull hull = hulls[i];
                if (hull?.linkedTo == null) { continue; }
                for (int l = 0; l < hull.linkedTo.Count; l++) {
                    if (hull.linkedTo[l] is not Hull other || other == hull) { continue; }
                    if (!index.TryGetValue(other, out int j)) { continue; }
                    Merge(i, j);
                    if (!pairs.Add(PairKey(i, j))) { continue; }
                    AddEdge(i, j, null);
                }
            }
            for (int i = 0; i < hullCount; i++) { group[i] = Root(i); }
        }

        private static void Merge(int a, int b) {
            int ra = Root(a);
            int rb = Root(b);
            if (ra == rb) { return; }
            if (ra < rb) { group[rb] = ra; } else { group[ra] = rb; }
        }

        private static int Root(int i) {
            while (group[i] != i) {
                group[i] = group[group[i]];
                i = group[i];
            }
            return i;
        }

        private static long PairKey(int a, int b) =>
            a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private static void EnsureEdgeCapacity(int extra) {
            int needed = edgeCount + extra;
            if (edges.Length >= needed) { return; }
            int size = edges.Length < 16 ? 16 : edges.Length;
            while (size < needed) { size *= 2; }
            Array.Resize(ref edges, size);
        }

        private static void AddEdge(int a, int b, Gap gap) {
            EnsureEdgeCapacity(1);
            edges[edgeCount].A = a;
            edges[edgeCount].B = b;
            edges[edgeCount].Gap = gap;
            edgeCount++;
            degree[a]++;
            degree[b]++;
        }

        private static void Grow<T>(ref T[] array, int size) {
            if (array.Length >= size) { return; }
            array = new T[size < 16 ? 16 : size];
        }

        public static float Clamp(float value) =>
            value < ColdConfig.MinTemperature ? ColdConfig.MinTemperature
            : value > ColdConfig.MaxTemperature ? ColdConfig.MaxTemperature
            : value;

        private static float Response(float rate, float deltaTime) {
            float x = rate * deltaTime;
            if (x <= 0.0f) { return 0.0f; }
            if (x >= 6.0f) { return 1.0f; }
            return 1.0f - (float)Math.Exp(-x);
        }
    }
}
