using System;
using FarseerPhysics.Collision;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    
    internal struct WallBounds {
        internal Vector2 Min;
        internal Vector2 Max;
        internal bool Valid;

        internal bool Overlaps(in WallBounds other) {
            const float clearance = 2f * FarseerPhysics.Settings.LinearSlop;
            return Valid && other.Valid && Min.X <= other.Max.X + clearance &&
                Max.X >= other.Min.X - clearance && Min.Y <= other.Max.Y + clearance &&
                Max.Y >= other.Min.Y - clearance;
        }

        internal void Include(Body body, Vector2 origin, Vector2 normal, bool wallOnly) {
            if (body?.FixtureList == null) { return; }
            Vector2 tangent = new Vector2(-normal.Y, normal.X);
            var fixtures = body.FixtureList;
            for (int f = 0; f < fixtures.Count; f++) {
                Fixture fixture = fixtures[f];
                if (fixture.IsSensor || fixture.Shape == null ||
                    (wallOnly && !WallGeometry.IsWallFixture(fixture, Physics.CollisionWall))) { continue; }
                Shape shape = fixture.Shape;
                if (shape is PolygonShape polygon) {
                    var vertices = polygon.Vertices;
                    Vector2 localNormal = body.GetLocalVector(normal);
                    Vector2 localTangent = new Vector2(-localNormal.Y, localNormal.X);
                    Vector2 offset = body.Position - origin;
                    Vector2 shift = new Vector2(Vector2.Dot(offset, normal), Vector2.Dot(offset, tangent));
                    Vector2 min = new Vector2(float.PositiveInfinity);
                    Vector2 max = new Vector2(float.NegativeInfinity);
                    for (int v = 0; v < vertices.Count; v++) {
                        Vector2 vertex = vertices[v];
                        Vector2 point = new Vector2(Vector2.Dot(vertex, localNormal), Vector2.Dot(vertex, localTangent));
                        min = Vector2.Min(min, point);
                        max = Vector2.Max(max, point);
                    }
                    Vector2 margin = new Vector2(shape.Radius);
                    Include(min + shift - margin, max + shift + margin);
                }
                else if (shape is CircleShape circle) {
                    Include(body.GetWorldPoint(circle.Position) - origin, normal, tangent, circle.Radius);
                }
                else {
                    body.GetTransform(out var transform);
                    for (int child = 0; child < shape.ChildCount; child++) {
                        shape.ComputeAABB(out AABB aabb, ref transform, child);
                        Vector2 center = (aabb.LowerBound + aabb.UpperBound) * 0.5f - origin;
                        Vector2 half = (aabb.UpperBound - aabb.LowerBound) * 0.5f;
                        Vector2 projected = new Vector2(Vector2.Dot(center, normal), Vector2.Dot(center, tangent));
                        Vector2 radius = new Vector2(Math.Abs(normal.X) * half.X + Math.Abs(normal.Y) * half.Y,
                            Math.Abs(tangent.X) * half.X + Math.Abs(tangent.Y) * half.Y);
                        Include(projected - radius, projected + radius);
                    }
                }
            }
        }

        private void Include(Vector2 offset, Vector2 normal, Vector2 tangent, float radius) {
            Vector2 point = new Vector2(Vector2.Dot(offset, normal), Vector2.Dot(offset, tangent));
            Vector2 margin = new Vector2(radius);
            Include(point - margin, point + margin);
        }

        private void Include(Vector2 min, Vector2 max) {
            if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) ||
                !float.IsFinite(max.X) || !float.IsFinite(max.Y)) { return; }
            if (!Valid) { Min = min; Max = max; Valid = true; return; }
            Min = Vector2.Min(Min, min);
            Max = Vector2.Max(Max, max);
        }
    }
}
