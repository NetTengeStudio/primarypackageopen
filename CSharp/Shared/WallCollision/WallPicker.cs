using System;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public static class WallPicker {
        private const float HalfSize = 0.02f;

        private static readonly Func<Fixture, bool> probe = Probe;

        private static Vector2 point;
        private static Structure best;
        private static float bestArea;
        private static ushort bestId;

        public static Structure Pick(Vector2 cursorWorld) {
            World world = GameMain.World;
            if (world == null) { return null; }
            best = null;
            bestArea = float.MaxValue;
            bestId = ushort.MaxValue;
            point = ConvertUnits.ToSimUnits(cursorWorld);
            AABB box;
            box.LowerBound = new Vector2(point.X - HalfSize, point.Y - HalfSize);
            box.UpperBound = new Vector2(point.X + HalfSize, point.Y + HalfSize);
            world.QueryAABB(probe, ref box);
            return best;
        }

        private static bool Probe(Fixture fixture) {
            if (!WallGeometry.IsWallFixture(fixture, Physics.CollisionWall)) { return true; }
            Body body = fixture.Body;
            if (body == null || body.UserData is not Structure wall) { return true; }
            if (wall.Removed || !wall.HasBody || wall.IsPlatform) { return true; }
            if (!fixture.TestPoint(ref point)) { return true; }
            float area = Area(fixture);
            if (area <= 0f) { return true; }
            if (area < bestArea || (area == bestArea && wall.ID < bestId)) {
                bestArea = area;
                bestId = wall.ID;
                best = wall;
            }
            return true;
        }

        private static float Area(Fixture fixture) {
            Shape shape = fixture.Shape;
            if (shape == null || shape.ShapeType != ShapeType.Polygon) { return 0f; }
            AABB local = ((PolygonShape)shape).Vertices.GetAABB();
            return (local.UpperBound.X - local.LowerBound.X) * (local.UpperBound.Y - local.LowerBound.Y);
        }
    }
}
