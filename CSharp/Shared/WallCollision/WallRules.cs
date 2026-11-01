using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public sealed class WallRule {
        private readonly List<Body> gated = new List<Body>(4);
        private WallGateState gateState;

        internal WallRule(ushort id) {
            Id = id;
            Normal = Vector2.UnitX;
        }

        public ushort Id { get; }
        public Structure Wall { get; internal set; }
        public WallMode Mode { get; internal set; }
        public WallFace Face { get; internal set; }
        public Vector2 Normal { get; private set; }
        public int GatedBodies => gated.Count;
        internal bool HasCrossings => gateState?.HasCrossings ?? false;

        internal void Rebind() {
            Release(clearCrossings: false);
            if (Mode != WallMode.OneWay || Wall?.Bodies is null) { Release(); return; }
            List<Body> bodies = Wall.Bodies;
            Body reference = null;
            for (int i = 0; i < bodies.Count; i++) {
                if (WallGeometry.HasWallFixture(bodies[i], Physics.CollisionWall)) { reference = bodies[i]; break; }
            }
            if (reference == null) { Release(); return; }
            Normal = reference.GetWorldVector(new WallPlane(Face).LocalNormal);
            WallBounds bounds = default;
            for (int i = 0; i < bodies.Count; i++) { bounds.Include(bodies[i], reference.Position, Normal, wallOnly: true); }
            if (!bounds.Valid) { Release(); return; }
            gateState ??= new WallGateState();
            gateState.Configure(reference, Face, in bounds);
            for (int i = 0; i < bodies.Count; i++) {
                Body body = bodies[i];
                if (!WallGeometry.HasWallFixture(body, Physics.CollisionWall)) { continue; }
                WallContacts.Attach(GameMain.World, new WallGate(body, gateState));
                gated.Add(body);
            }
        }

        internal bool AllowCrossing(Body other)
            => gateState?.AllowCrossing(other) ?? false;

        internal void SweepCrossings(World world) => gateState?.Sweep(world);

        internal void Release(bool clearCrossings = true) {
            for (int i = 0; i < gated.Count; i++) { WallContacts.Detach(gated[i]); }
            gated.Clear();
            if (clearCrossings) { gateState?.Clear(); gateState = null; }
        }
    }

    public readonly struct RuleChange {
        public readonly bool Changed;
        public readonly ushort Id;
        public readonly WallMode Mode;
        public readonly WallFace Face;

        private RuleChange(bool changed, ushort id, WallMode mode, WallFace face) {
            Changed = changed;
            Id = id;
            Mode = mode;
            Face = face;
        }

        public RuleChange(ushort id, WallMode mode, WallFace face) : this(true, id, mode, face) { }

        public static RuleChange None => default;
    }

    internal readonly struct PreparedRule {
        internal readonly WallMode Mode;
        internal readonly WallFace Face;

        internal PreparedRule(WallMode mode, WallFace face) {
            Mode = mode;
            Face = face;
        }
    }

    public static class WallRules {
        private static readonly List<WallRule> ordered = new List<WallRule>(16);
        private static readonly Dictionary<ushort, WallRule> byId = new Dictionary<ushort, WallRule>(16);
        private static readonly List<ushort> stale = new List<ushort>(8);
        private static readonly Dictionary<Structure, PreparedRule> prepared =
            new Dictionary<Structure, PreparedRule>();
        private static readonly List<Structure> retiredPrepared = new List<Structure>(8);
        private static double nextCrossingSweep;
        private static double nextPrune;

        public static bool Ready { get; private set; }

        public static int Count => ordered.Count;

        public static WallRule At(int index) => ordered[index];

        public static void Bind(Structure wall, WallMode mode, WallFace face) {
            if (!Ready || wall == null) { return; }
            if (mode == WallMode.None) {
                Unbind(wall.ID);
                return;
            }
            WallContacts.BeginBatch();
            try {
                if (!byId.TryGetValue(wall.ID, out WallRule rule)) {
                    rule = new WallRule(wall.ID);
                    byId.Add(wall.ID, rule);
                    ordered.Add(rule);
                }
                bool changed = rule.Mode != mode || rule.Face != face ||
                               !ReferenceEquals(rule.Wall, wall);
                if (!changed) { return; }
                rule.Release();
                rule.Wall = wall;
                rule.Mode = mode;
                rule.Face = face;
                rule.Rebind();
            }
            finally {
                WallContacts.EndBatch();
            }
        }

        public static bool Apply(ushort id, WallMode mode, WallFace face) {
            if (!Ready) { return false; }
            if (mode == WallMode.None) { return Unbind(id); }
            if (mode != WallMode.Solid && mode != WallMode.OneWay) { return false; }
            if ((byte)face > (byte)WallFace.MinusY) { return false; }
            if (Entity.FindEntityByID(id) is not Structure wall) { return false; }
            if (wall.Removed) { return false; }
            Bind(wall, mode, face);
            return true;
        }

        public static bool Unbind(ushort id) {
            if (!byId.TryGetValue(id, out WallRule rule)) { return false; }
            WallContacts.BeginBatch();
            try {
                rule.Release();
                byId.Remove(id);
                ordered.Remove(rule);
            }
            finally {
                WallContacts.EndBatch();
            }
            return true;
        }

        public static WallRule Find(Structure wall) {
            if (wall == null) { return null; }
            if (!byId.TryGetValue(wall.ID, out WallRule rule)) { return null; }
            return ReferenceEquals(rule.Wall, wall) ? rule : null;
        }

        public static void Rebind(Structure wall) {
            if (!Ready) { return; }
            WallRule rule = Find(wall);
            if (rule == null) { return; }
            WallContacts.BeginBatch();
            try { rule.Rebind(); }
            finally { WallContacts.EndBatch(); }
        }

        public static void RegisterPrepared(Structure wall) {
            if (wall is null || wall.Removed) { return; }
            if (WallConfiguration.TryRead(wall, out WallMode mode, out WallFace face)) {
                prepared[wall] = new PreparedRule(mode, face);
            }
            else {
                prepared.Remove(wall);
            }
        }

        public static void UnregisterPrepared(Structure wall) {
            if (wall is null) { return; }
            prepared.Remove(wall);
            if (Ready && Find(wall) != null) { Unbind(wall.ID); }
        }

        public static int ActivatePrepared() {
            if (Ready) { return ordered.Count; }

            
            
            
            prepared.Clear();
            foreach (MapEntity entity in MapEntity.MapEntityList) {
                if (entity is Structure wall && !wall.Removed) {
                    RegisterPrepared(wall);
                }
            }

            Ready = true;
            int activated = 0;
            retiredPrepared.Clear();
            WallContacts.BeginBatch();
            try {
                foreach (KeyValuePair<Structure, PreparedRule> entry in prepared) {
                    Structure wall = entry.Key;
                    if (wall.Removed) {
                        retiredPrepared.Add(wall);
                        continue;
                    }
                    if (!wall.HasBody) { continue; }
                    if (!WallGeometry.TryAxis(wall.Bodies, Physics.CollisionWall, out _, out _, out _)) { continue; }
                    Bind(wall, entry.Value.Mode, entry.Value.Face);
                    activated++;
                }
                for (int i = 0; i < retiredPrepared.Count; i++) {
                    prepared.Remove(retiredPrepared[i]);
                }
            }
            finally {
                retiredPrepared.Clear();
                WallContacts.EndBatch();
            }
            return activated;
        }

        public static void SetReady(bool ready) {
            if (!ready) {
                Reset();
                return;
            }
            Ready = true;
        }

        public static void Prune() {
            if (ordered.Count == 0) { return; }
            double now = Timing.TotalTime;
            if (now >= nextPrune) {
                nextPrune = now + 2.0;
                stale.Clear();
                for (int i = 0; i < ordered.Count; i++) {
                    Structure wall = ordered[i].Wall;
                    if (wall == null || wall.Removed) { stale.Add(ordered[i].Id); }
                }
                if (stale.Count != 0) {
                    WallContacts.BeginBatch();
                    try {
                        for (int i = 0; i < stale.Count; i++) { Unbind(stale[i]); }
                    }
                    finally {
                        WallContacts.EndBatch();
                    }
                }
            }
            if (now < nextCrossingSweep) { return; }
            nextCrossingSweep = now + 0.1;
            World world = GameMain.World;
            for (int i = 0; i < ordered.Count; i++) {
                WallRule rule = ordered[i];
                if (rule.HasCrossings) { rule.SweepCrossings(world); }
            }
        }

        public static void Reset() {
            Ready = false;
            WallContacts.BeginBatch();
            try {
                for (int i = 0; i < ordered.Count; i++) { ordered[i].Release(); }
                ordered.Clear();
                byId.Clear();
                nextCrossingSweep = 0;
                nextPrune = 0;
                WallContacts.Clear();
            }
            finally { WallContacts.EndBatch(); }
        }
    }
}
