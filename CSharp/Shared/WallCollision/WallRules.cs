using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FarseerPhysics.Collision;
using Barotrauma;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public sealed class WallRule {
        private const float Clearance = 0.05f;
        private readonly List<Body> gated = new List<Body>(4);
        // PreSolve may run on parallel physics workers; per-wall crossing state must be thread-safe.
        private readonly ConcurrentDictionary<Body, float> crossings = new ConcurrentDictionary<Body, float>();
        private Vector2 referencePosition;
        private float halfThickness;

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

        internal void Rebind() {
            Release(clearCrossings: false);
            if (Mode != WallMode.OneWay || Wall == null || Wall.Bodies == null) { return; }
            List<Body> bodies = Wall.Bodies;
            if (!WallGeometry.TryMeasure(bodies, Physics.CollisionWall, out float width, out float height, out _)) { return; }
            halfThickness = (Face == WallFace.PlusX || Face == WallFace.MinusX ? width : height) * 0.5f;
            bool measured = false;
            for (int i = 0; i < bodies.Count; i++) {
                Body body = bodies[i];
                if (!WallGeometry.HasWallFixture(body, Physics.CollisionWall)) { continue; }
                if (!measured) {
                    Normal = new WallPlane(Face).Normal(body.Rotation);
                    referencePosition = body.Position;
                    measured = true;
                }
                WallContacts.Attach(GameMain.World, new WallGate(body, this));
                gated.Add(body);
            }
        }

        internal bool AllowCrossing(Body other) {
            if (Mode != WallMode.OneWay || other == null ||
                other.BodyType != BodyType.Dynamic || !other.Enabled) { return false; }
            if (crossings.ContainsKey(other)) { return true; }

            Vector2 offset = other.WorldCenter - referencePosition;
            float side = offset.X * Normal.X + offset.Y * Normal.Y;
            if (side <= 0f) { return false; }

            Vector2 velocity = other.LinearVelocity;
            if (velocity.X * Normal.X + velocity.Y * Normal.Y >= 0f) { return false; }

            if (crossings.TryAdd(other, MeasureRadius(other))) { return true; }
            return crossings.ContainsKey(other);
        }

        internal void SweepCrossings(World world) {
            if (crossings.IsEmpty) { return; }
            foreach (KeyValuePair<Body, float> entry in crossings) {
                Body body = entry.Key;
                if (body.World != world || !body.Enabled || body.BodyType != BodyType.Dynamic) {
                    crossings.TryRemove(body, out _);
                    continue;
                }
                Vector2 offset = body.WorldCenter - referencePosition;
                float distance = offset.X * Normal.X + offset.Y * Normal.Y;
                if (Math.Abs(distance) > halfThickness + entry.Value + Clearance) {
                    crossings.TryRemove(body, out _);
                }
            }
        }

        private static float MeasureRadius(Body body) {
            float radiusSquared = 0f;
            Vector2 center = body.WorldCenter;
            List<Fixture> fixtures = body.FixtureList;
            if (fixtures == null) { return 0.5f; }
            for (int index = 0; index < fixtures.Count; index++) {
                Fixture fixture = fixtures[index];
                if (fixture?.Shape == null) { continue; }
                for (int child = 0; child < fixture.Shape.ChildCount; child++) {
                    fixture.GetAABB(out AABB bounds, child);
                    float dx = Math.Max(Math.Abs(bounds.LowerBound.X - center.X),
                                        Math.Abs(bounds.UpperBound.X - center.X));
                    float dy = Math.Max(Math.Abs(bounds.LowerBound.Y - center.Y),
                                        Math.Abs(bounds.UpperBound.Y - center.Y));
                    radiusSquared = Math.Max(radiusSquared, dx * dx + dy * dy);
                }
            }
            return radiusSquared > 0f ? (float)Math.Sqrt(radiusSquared) : 0.5f;
        }

        internal void Release(bool clearCrossings = true) {
            for (int i = 0; i < gated.Count; i++) { WallContacts.Detach(gated[i]); }
            gated.Clear();
            if (clearCrossings) { crossings.Clear(); }
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

    public static class WallRules {
        private static readonly List<WallRule> ordered = new List<WallRule>(16);
        private static readonly Dictionary<ushort, WallRule> byId = new Dictionary<ushort, WallRule>(16);
        private static readonly List<ushort> stale = new List<ushort>(8);
        private static double nextCrossingSweep;

        public static bool Ready { get; private set; }

        public static int Count => ordered.Count;

        public static WallRule At(int index) => ordered[index];

        public static void Bind(Structure wall, WallMode mode, WallFace face) {
            if (!Ready || wall == null) { return; }
            if (mode == WallMode.None) {
                Unbind(wall.ID);
                return;
            }
            if (!byId.TryGetValue(wall.ID, out WallRule rule)) {
                rule = new WallRule(wall.ID);
                byId.Add(wall.ID, rule);
                ordered.Add(rule);
            }
            if (rule.Mode != mode || rule.Face != face) { rule.Release(); }
            rule.Wall = wall;
            rule.Mode = mode;
            rule.Face = face;
            rule.Rebind();
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
            rule.Release();
            byId.Remove(id);
            ordered.Remove(rule);
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
            rule.Rebind();
        }

        public static int ActivatePrepared() {
            if (Ready) { return ordered.Count; }
            Ready = true;
            int activated = 0;
            foreach (MapEntity entity in MapEntity.MapEntityList) {
                if (entity is not Structure wall || wall.Removed || !wall.HasBody) { continue; }
                if (!WallConfiguration.TryRead(wall, out WallMode mode, out WallFace face)) { continue; }
                if (!WallGeometry.TryAxis(wall.Bodies, Physics.CollisionWall, out _, out _, out _)) { continue; }
                Bind(wall, mode, face);
                activated++;
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
            stale.Clear();
            for (int i = 0; i < ordered.Count; i++) {
                Structure wall = ordered[i].Wall;
                if (wall == null || wall.Removed) { stale.Add(ordered[i].Id); }
            }
            for (int i = 0; i < stale.Count; i++) { Unbind(stale[i]); }
            if (Timing.TotalTime < nextCrossingSweep) { return; }
            nextCrossingSweep = Timing.TotalTime + 0.1;
            World world = GameMain.World;
            for (int i = 0; i < ordered.Count; i++) { ordered[i].SweepCrossings(world); }
        }

        public static void Reset() {
            Ready = false;
            for (int i = 0; i < ordered.Count; i++) { ordered[i].Release(); }
            ordered.Clear();
            byId.Clear();
            nextCrossingSweep = 0;
            WallContacts.Clear();
        }
    }
}
