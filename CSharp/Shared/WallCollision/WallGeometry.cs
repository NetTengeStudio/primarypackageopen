using System;
using System.Collections.Generic;
using FarseerPhysics.Collision;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public readonly struct WallPlane {
        public readonly WallFace Face;

        public WallPlane(WallFace face) { Face = face; }

        public bool AlongX => Face == WallFace.PlusX || Face == WallFace.MinusX;

        public float Sign => Face == WallFace.PlusX || Face == WallFace.PlusY ? 1f : -1f;

        public Vector2 LocalNormal => AlongX ? new Vector2(Sign, 0f) : new Vector2(0f, Sign);

        public Vector2 Normal(float rotation) {
            float sin = (float)Math.Sin(rotation);
            float cos = (float)Math.Cos(rotation);
            Vector2 local = LocalNormal;
            return new Vector2(cos * local.X - sin * local.Y, sin * local.X + cos * local.Y);
        }
    }

    public static class WallGeometry {
        public static bool IsWallFixture(Fixture fixture, Category wallCategory) {
            if (fixture == null || fixture.IsSensor) { return false; }
            return (fixture.CollisionCategories & wallCategory) != Category.None;
        }

        public static bool HasWallFixture(Body body, Category wallCategory) {
            if (body == null) { return false; }
            List<Fixture> fixtures = body.FixtureList;
            if (fixtures == null) { return false; }
            for (int i = 0; i < fixtures.Count; i++) {
                if (IsWallFixture(fixtures[i], wallCategory)) { return true; }
            }
            return false;
        }

        public static bool TryMeasure(IReadOnlyList<Body> bodies, Category wallCategory,
            out float width, out float height, out float rotation) {
            width = 0f;
            height = 0f;
            rotation = 0f;
            if (bodies == null) { return false; }
            bool found = false;
            for (int i = 0; i < bodies.Count; i++) {
                Body body = bodies[i];
                if (body == null) { continue; }
                List<Fixture> fixtures = body.FixtureList;
                if (fixtures == null) { continue; }
                for (int f = 0; f < fixtures.Count; f++) {
                    Fixture fixture = fixtures[f];
                    if (!IsWallFixture(fixture, wallCategory)) { continue; }
                    Shape shape = fixture.Shape;
                    if (shape == null || shape.ShapeType != ShapeType.Polygon) { continue; }
                    AABB local = ((PolygonShape)shape).Vertices.GetAABB();
                    float extentX = local.UpperBound.X - local.LowerBound.X;
                    float extentY = local.UpperBound.Y - local.LowerBound.Y;
                    if (extentX > width) { width = extentX; }
                    if (extentY > height) { height = extentY; }
                    if (!found) { rotation = body.Rotation; found = true; }
                }
            }
            return found && width > 0f && height > 0f;
        }

        public static bool TryAxis(IReadOnlyList<Body> bodies, Category wallCategory,
            out int axis, out Vector2 axisWorld, out float rotation) {
            axis = 0;
            axisWorld = Vector2.UnitX;
            if (!TryMeasure(bodies, wallCategory, out float width, out float height, out rotation)) { return false; }
            axis = width <= height ? 0 : 1;
            axisWorld = AxisWorld(axis, rotation);
            return true;
        }

        public static Vector2 AxisWorld(int axis, float rotation) {
            float sin = (float)Math.Sin(rotation);
            float cos = (float)Math.Cos(rotation);
            return axis == 0
                ? new Vector2(cos, sin)
                : new Vector2(-sin, cos);
        }

        public static bool TryFaceFromPoint(IReadOnlyList<Body> bodies, Category wallCategory,
            Vector2 pointSim, bool hasPoint, out WallFace face, out Vector2 normal) {
            face = WallFace.PlusX;
            normal = Vector2.UnitX;
            if (!TryAxis(bodies, wallCategory, out int axis, out Vector2 axisWorld, out _)) { return false; }
            bool positive = true;
            if (hasPoint && TryCenter(bodies, wallCategory, out Vector2 center)) {
                float side = (pointSim.X - center.X) * axisWorld.X + (pointSim.Y - center.Y) * axisWorld.Y;
                if (side != 0f) { positive = side > 0f; }
            }
            face = FaceOf(axis, positive);
            normal = positive ? axisWorld : -axisWorld;
            return true;
        }

        public static bool TryFaceFromDirection(IReadOnlyList<Body> bodies, Category wallCategory,
            Vector2 direction, out WallFace face, out Vector2 normal) {
            face = WallFace.PlusX;
            normal = Vector2.UnitX;
            if (!TryAxis(bodies, wallCategory, out int axis, out Vector2 axisWorld, out _)) { return false; }
            float along = direction.X * axisWorld.X + direction.Y * axisWorld.Y;
            if (Math.Abs(along) < MinAlignment) { return false; }
            bool positive = along > 0f;
            face = FaceOf(axis, positive);
            normal = positive ? axisWorld : -axisWorld;
            return true;
        }

        public const float MinAlignment = 0.5f;

        public static WallFace Opposite(WallFace face) {
            switch (face) {
                case WallFace.PlusX: return WallFace.MinusX;
                case WallFace.MinusX: return WallFace.PlusX;
                case WallFace.PlusY: return WallFace.MinusY;
                default: return WallFace.PlusY;
            }
        }

        public static WallFace FaceOf(int axis, bool positive) {
            if (axis == 0) { return positive ? WallFace.PlusX : WallFace.MinusX; }
            return positive ? WallFace.PlusY : WallFace.MinusY;
        }

        public static string Describe(Vector2 normal) {
            if (Math.Abs(normal.X) >= Math.Abs(normal.Y)) {
                return normal.X >= 0f ? "справа (+X)" : "слева (-X)";
            }
            return normal.Y >= 0f ? "сверху (+Y)" : "снизу (-Y)";
        }

        public static string DescribeExact(Vector2 normal) =>
            Describe(normal) + " " + normal.X.ToString("0.00") + ";" + normal.Y.ToString("0.00");

        private static bool TryCenter(IReadOnlyList<Body> bodies, Category wallCategory, out Vector2 center) {
            center = Vector2.Zero;
            if (bodies == null) { return false; }
            int count = 0;
            float x = 0f;
            float y = 0f;
            for (int i = 0; i < bodies.Count; i++) {
                Body body = bodies[i];
                if (!HasWallFixture(body, wallCategory)) { continue; }
                Vector2 position = body.Position;
                x += position.X;
                y += position.Y;
                count++;
            }
            if (count == 0) { return false; }
            center = new Vector2(x / count, y / count);
            return true;
        }
    }
}
