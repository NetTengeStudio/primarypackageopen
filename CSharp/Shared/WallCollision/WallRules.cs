using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public sealed class WallRule {
        private readonly List<Body> gated = new List<Body>(4);

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
            Release();
            if (Mode != WallMode.OneWay || Wall == null || Wall.Bodies == null) { return; }
            List<Body> bodies = Wall.Bodies;
            bool measured = false;
            for (int i = 0; i < bodies.Count; i++) {
                Body body = bodies[i];
                if (!WallGeometry.HasWallFixture(body, Physics.CollisionWall)) { continue; }
                if (!measured) {
                    Normal = new WallPlane(Face).Normal(body.Rotation);
                    measured = true;
                }
                WallContacts.Attach(GameMain.World, new WallGate(body, Face));
                gated.Add(body);
            }
        }

        internal void Release() {
            for (int i = 0; i < gated.Count; i++) { WallContacts.Detach(gated[i]); }
            gated.Clear();
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
            rule.Wall = wall;
            rule.Mode = mode;
            rule.Face = face;
            rule.Rebind();
        }

        public static bool Apply(ushort id, WallMode mode, WallFace face) {
            if (!Ready) { return false; }
            if (mode == WallMode.None) { return Unbind(id); }
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
                if (!WallGeometry.HasWallFixture(wall.Bodies != null && wall.Bodies.Count != 0 ?
                        wall.Bodies[0] : null, Physics.CollisionWall)) { continue; }
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
        }

        public static void Reset() {
            Ready = false;
            for (int i = 0; i < ordered.Count; i++) { ordered[i].Release(); }
            ordered.Clear();
            byId.Clear();
            WallContacts.Clear();
        }
    }
}
