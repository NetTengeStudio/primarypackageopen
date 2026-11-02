using FarseerPhysics;
using System;
using System.Collections.Generic;
using FarseerPhysics.Collision;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

/// <summary>Moves owned fixture geometry while retaining fixture identity, filters and callbacks.
/// Detach/attach uses Farseer's contact/proxy lifecycle; changing vertices alone is insufficient.</summary>
internal static class StaticFixtureTranslation {
    private const int MaxFixtures = 64;
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Inside(in AABB outer, in AABB inner) => inner.LowerBound.X > outer.LowerBound.X &&
        inner.LowerBound.Y > outer.LowerBound.Y && inner.UpperBound.X < outer.UpperBound.X && inner.UpperBound.Y < outer.UpperBound.Y;
    private static bool CanCollide(Fixture first, Fixture second) {
        if (first.CollisionGroup != 0 && first.CollisionGroup == second.CollisionGroup) { return first.CollisionGroup > 0; }
        return (first.CollidesWith & second.CollisionCategories) != Category.None && (second.CollidesWith & first.CollisionCategories) != Category.None;
    }
    internal static void Validate(IReadOnlyList<Fixture> fixtures, Body owner, Vector2 delta, in AABB allowed, bool rejectContacts) {
        if (!Finite(delta) || !Finite(allowed.LowerBound) || !Finite(allowed.UpperBound) ||
            allowed.LowerBound.X >= allowed.UpperBound.X || allowed.LowerBound.Y >= allowed.UpperBound.Y) { throw new ArgumentException("Недопустимая геометрия перемещения коллизии"); }
        if (owner?.World == null || owner.World.IsLocked) { throw new InvalidOperationException("Физический мир сейчас недоступен для изменения fixtures"); }
        if (fixtures == null || fixtures.Count is < 1 or > MaxFixtures) { throw new ArgumentException("Недопустимое число fixtures предмета"); }
        Transform identity = new(Vector2.Zero, 0);
        owner.GetTransform(out Transform destination);
        destination.p += owner.GetWorldVector(delta);
        for (int i = 0; i < fixtures.Count; i++) {
            Fixture fixture = fixtures[i];
            if (fixture?.Body != owner || (fixture.Shape.GetType() != typeof(CircleShape) && fixture.Shape.GetType() != typeof(PolygonShape))) { throw new InvalidOperationException("Неизвестное владение или тип формы коллизии предмета"); }
            for (int j = 0; j < i; j++) { if (ReferenceEquals(fixtures[j], fixture)) { throw new InvalidOperationException("Повторная ссылка на fixture предмета"); } }
            fixture.Shape.ComputeAABB(out AABB before, ref identity, 0);
            AABB after = before; after.LowerBound += delta; after.UpperBound += delta;
            if (!Inside(allowed, before) || !Inside(allowed, after)) { throw new InvalidOperationException("Перемещение затрагивает внешние границы физического тела подлодки"); }
            if (!rejectContacts || fixture.IsSensor) { continue; }
            for (var edge = owner.ContactList; edge != null; edge = edge.Next) {
                var contact = edge.Contact;
                if (contact.IsTouching && !contact.FixtureA.IsSensor && !contact.FixtureB.IsSensor &&
                    (contact.FixtureA == fixture || contact.FixtureB == fixture)) { throw new InvalidOperationException("Предмет находится в активном физическом контакте"); }
            }
            fixture.Shape.ComputeAABB(out AABB worldAfter, ref destination, 0);
            bool occupied = false;
            owner.World.QueryAABB(candidate => {
                if (candidate.Body != owner && candidate.Body.BodyType != BodyType.Static && !candidate.IsSensor && CanCollide(fixture, candidate)) { occupied = true; return false; }
                return true;
            }, ref worldAfter);
            if (occupied) { throw new InvalidOperationException("Конечная область коллизии занята физическим телом"); }
        }
    }
    internal static Plan Prepare(IReadOnlyList<Fixture> fixtures, Body owner, Vector2 delta, in AABB allowed) {
        Validate(fixtures, owner, delta, allowed, false);
        return new Plan(fixtures, owner, delta);
    }
    internal sealed class Plan {
        private readonly struct ShapeState {
            internal readonly Fixture Fixture;
            internal readonly Vector2 CircleBefore, CircleAfter;
            internal readonly Vertices PolygonBefore, PolygonAfter;
            internal ShapeState(Fixture fixture, Vector2 delta) {
                Fixture = fixture; CircleBefore = CircleAfter = default; PolygonBefore = PolygonAfter = null;
                if (fixture.Shape is CircleShape circle) { CircleBefore = circle.Position; CircleAfter = circle.Position + delta; }
                else {
                    var polygon = (PolygonShape)fixture.Shape;
                    PolygonBefore = new Vertices(polygon.Vertices); PolygonAfter = new Vertices(polygon.Vertices.Count);
                    for (int i = 0; i < polygon.Vertices.Count; i++) { PolygonAfter.Add(polygon.Vertices[i] + delta); }
                }
            }
            internal void Set(bool after) {
                if (Fixture.Shape is CircleShape circle) { circle.Position = after ? CircleAfter : CircleBefore; }
                else { ((PolygonShape)Fixture.Shape).Vertices = after ? PolygonAfter : PolygonBefore; }
            }
            internal bool Unchanged() {
                if (Fixture.Shape is CircleShape circle) { return circle.Position == CircleBefore; }
                var vertices = ((PolygonShape)Fixture.Shape).Vertices;
                if (vertices.Count != PolygonBefore.Count) { return false; }
                for (int i = 0; i < vertices.Count; i++) { if (vertices[i] != PolygonBefore[i]) { return false; } }
                return true;
            }
        }
        private readonly ShapeState[] shapes;
        private readonly Body owner;
        private readonly Vector2 linearVelocity;
        private readonly float angularVelocity;
        private bool applied, used;
        internal Plan(IReadOnlyList<Fixture> fixtures, Body owner, Vector2 delta) {
            this.owner = owner; linearVelocity = owner.LinearVelocity; angularVelocity = owner.AngularVelocity;
            shapes = new ShapeState[fixtures.Count];
            for (int i = 0; i < fixtures.Count; i++) { shapes[i] = new(fixtures[i], delta); }
        }
        internal void Apply() {
            if (used || owner.World == null || owner.World.IsLocked) { throw new InvalidOperationException("План коллизии устарел или уже выполнен"); }
            foreach (var shape in shapes) { if (shape.Fixture.Body != owner || !shape.Unchanged()) { throw new InvalidOperationException("Коллизия изменилась после подготовки плана"); } }
            used = true;
            try { Replace(true); applied = true; }
            catch (Exception failure) {
                try { Restore(); }
                catch (Exception rollback) { throw new AggregateException("Ошибка восстановления физических fixtures", failure, rollback); }
                throw;
            }
        }
        internal void Revert() {
            if (!applied) { return; }
            if (owner.World == null || owner.World.IsLocked) { throw new InvalidOperationException("Нельзя восстановить fixtures внутри physics step"); }
            Restore(); applied = false;
        }
        private void Restore() {
            Replace(false);
            owner.LinearVelocity = linearVelocity; owner.AngularVelocity = angularVelocity;
        }
        private void Replace(bool after) {
            foreach (var shape in shapes) {
                if (shape.Fixture.Body == owner) { owner.Remove(shape.Fixture); }
                else if (shape.Fixture.Body != null) { throw new InvalidOperationException("Владение fixture было изменено другим исполнителем"); }
            }
            foreach (var shape in shapes) { shape.Set(after); }
            foreach (var shape in shapes) { owner.Add(shape.Fixture, resetMassData: false); }
            owner.ResetMassData();
        }
    }
}
