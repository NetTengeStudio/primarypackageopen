using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public sealed class WallGate {
        public const float AwaySpeed = 0.02f;

        public WallGate(Body body, WallFace face) {
            Body = body;
            Face = face;
            Normal = new WallPlane(face).Normal(body.Rotation);
        }

        public Body Body { get; }

        public WallFace Face { get; }

        public Vector2 Normal { get; }

        public bool Passable(Body other) {
            if (other == null) { return false; }
            Vector2 offset = other.WorldCenter - Body.Position;
            float side = offset.X * Normal.X + offset.Y * Normal.Y;
            if (side > 0f) { return true; }
            Vector2 velocity = other.LinearVelocity;
            return velocity.X * Normal.X + velocity.Y * Normal.Y < -AwaySpeed;
        }
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
            if (body == null || !gates.Remove(body)) { return; }
            // Avoid leaving a callback attached to the physics world when no gates remain.
            if (gates.Count == 0) { Release(); }
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
            if (first == null || second == null) { return; }
            Body bodyA = first.Body;
            Body bodyB = second.Body;
            if (bodyA == null || bodyB == null) { return; }
            // A structure body may carry non-wall fixtures. Restrict the rule to wall contacts.
            if (WallGeometry.IsWallFixture(first, Physics.CollisionWall) &&
                gates.TryGetValue(bodyA, out WallGate gateA) && gateA.Passable(bodyB)) {
                contact.Enabled = false;
                return;
            }
            if (WallGeometry.IsWallFixture(second, Physics.CollisionWall) &&
                gates.TryGetValue(bodyB, out WallGate gateB) && gateB.Passable(bodyA)) {
                contact.Enabled = false;
            }
        }
    }
}
