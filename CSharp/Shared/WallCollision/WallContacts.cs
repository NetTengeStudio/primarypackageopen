using System.Collections.Generic;
using System.Threading;
using Barotrauma;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;

namespace PrimaryPackage.WallCollision {
    public sealed class WallGate {
        internal WallGate(Body body, WallGateState state) {
            Body = body;
            State = state;
        }

        internal Body Body { get; }
        internal WallGateState State { get; }
        internal bool Passable(Body other) => State.AllowCrossing(other);
    }

    public static class WallContacts {
        private static readonly PreSolveDelegate hook = OnPreSolve;
        private static readonly Dictionary<Body, WallGate> mutable = new Dictionary<Body, WallGate>(64);
        private static Dictionary<Body, WallGate> snapshot = new Dictionary<Body, WallGate>(0);
        private static World bound;
        private static int batchDepth;
        private static bool dirty;
        private static long physicsStep;

        internal static long PhysicsStep => Interlocked.Read(ref physicsStep);

        public static void StepPrefix() => Interlocked.Increment(ref physicsStep);

        public static void BeginBatch() => batchDepth++;

        public static void EndBatch() {
            if (batchDepth == 0) { return; }
            if (--batchDepth == 0 && dirty) { Publish(); }
        }

        public static int Count => mutable.Count;
        public static World BoundWorld => bound;

        internal static bool Passable(Fixture fixture, Body subject) {
            Dictionary<Body, WallGate> current = Volatile.Read(ref snapshot);
            return fixture != null && WallGeometry.IsWallFixture(fixture, Physics.CollisionWall) &&
                current.TryGetValue(fixture.Body, out WallGate gate) && gate.Passable(subject);
        }

        public static bool CollisionPrefix(Fixture f1, Fixture f2, ref bool __result) {
            if (f1 == null || f2 == null || f1.IsSensor || f2.IsSensor ||
                (!Passable(f1, f2.Body) && !Passable(f2, f1.Body))) { return true; }
            
            __result = false;
            return false;
        }

        public static void Attach(World world, WallGate gate) {
            if (world == null || gate?.Body == null) { return; }
            Bind(world);
            mutable[gate.Body] = gate;
            Publish();
        }

        public static void Detach(Body body) {
            if (body != null && mutable.Remove(body)) { Publish(); }
        }

        public static void Clear() {
            mutable.Clear();
            Publish();
            Release();
        }

        public static void Release() {
            if (bound == null) { return; }
            bound.ContactManager.PreSolve -= hook;
            bound = null;
        }

        private static void Bind(World world) {
            if (ReferenceEquals(bound, world)) { return; }
            Release();
            mutable.Clear();
            Publish();
            bound = world;
            bound.ContactManager.PreSolve += hook;
        }

        private static void Publish() {
            if (batchDepth != 0) { dirty = true; return; }
            dirty = false;
            Volatile.Write(ref snapshot, new Dictionary<Body, WallGate>(mutable));
        }

        private static void OnPreSolve(Contact contact, ref Manifold oldManifold) {
            Dictionary<Body, WallGate> current = Volatile.Read(ref snapshot);
            if (current.Count == 0) { return; }
            Fixture first = contact.FixtureA;
            Fixture second = contact.FixtureB;
            if (first == null || second == null || first.IsSensor || second.IsSensor) { return; }
            Body bodyA = first.Body;
            Body bodyB = second.Body;
            if (bodyA == null || bodyB == null) { return; }
            if (current.TryGetValue(bodyA, out WallGate firstGate) &&
                WallGeometry.IsWallFixture(first, Physics.CollisionWall) &&
                firstGate.Passable(bodyB)) {
                contact.Enabled = false;
                return;
            }
            if (current.TryGetValue(bodyB, out WallGate secondGate) &&
                WallGeometry.IsWallFixture(second, Physics.CollisionWall) &&
                secondGate.Passable(bodyA)) {
                contact.Enabled = false;
            }
        }
    }
}
