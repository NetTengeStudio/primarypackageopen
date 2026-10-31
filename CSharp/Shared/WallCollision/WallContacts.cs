using System.Collections.Generic;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    /// <summary>
    /// Narrow-phase gate on a physical wall section. The rule shares a crossing state across
    /// all its sections so a body is not blocked by a neighboring section halfway through.
    /// </summary>
    public sealed class WallGate {
        internal WallGate(Body body, WallRule rule) {
            Body = body;
            Rule = rule;
        }

        internal Body Body { get; }
        internal WallRule Rule { get; }

        internal bool Passable(Body other) => Rule.AllowCrossing(other);
    }

    public static class WallContacts {
        private static readonly Dictionary<Body, WallGate> gates = new Dictionary<Body, WallGate>(64);
        private static readonly PreSolveDelegate hook = OnPreSolve;
        private static World bound;

        public static int Count => gates.Count;
        public static World BoundWorld => bound;

        public static void Attach(World world, WallGate gate) {
            if (world == null || gate == null || gate.Body == null) { return; }
            Bind(world);
            gates[gate.Body] = gate;
        }

        public static void Detach(Body body) {
            if (body != null) { gates.Remove(body); }
        }

        public static void Clear() {
            gates.Clear();
            Release();
        }

        public static void Release() {
            if (bound == null) { return; }
            bound.ContactManager.PreSolve -= hook;
            bound = null;
        }

        private static void Bind(World world) {
            if (bound == world) { return; }
            Release();
            bound = world;
            world.ContactManager.PreSolve += hook;
        }

        private static void OnPreSolve(Contact contact, ref Manifold oldManifold) {
            if (gates.Count == 0) { return; }
            Fixture first = contact.FixtureA;
            Fixture second = contact.FixtureB;
            if (first == null || second == null || first.IsSensor || second.IsSensor) { return; }
            Body bodyA = first.Body;
            Body bodyB = second.Body;
            if (bodyA == null || bodyB == null) { return; }

            if (gates.TryGetValue(bodyA, out WallGate gate) &&
                WallGeometry.IsWallFixture(first, Physics.CollisionWall) &&
                gate.Passable(bodyB)) {
                contact.Enabled = false;
                return;
            }
            if (gates.TryGetValue(bodyB, out gate) &&
                WallGeometry.IsWallFixture(second, Physics.CollisionWall) &&
                gate.Passable(bodyA)) {
                contact.Enabled = false;
            }
        }
    }
}
