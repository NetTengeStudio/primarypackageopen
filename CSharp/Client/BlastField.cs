using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Bloom {
    internal sealed class BlastField {
        internal const int Rays = 48;
        private const float RefreshInterval = 0.15f;
        private const int MaxFields = 8;

        private static readonly Vector2[] dirs = BuildDirs();
        private static readonly List<BlastField> active = new List<BlastField>();

        private readonly float[] reach = new float[Rays + 1];
        private Vector2 origin;
        private Submarine sub;
        private Vector2 subOrigin;
        private float radius;
        private float ttl;
        private float sinceTrace;

        internal Vector2 Origin => sub == null ? origin : origin + sub.DrawPosition - subOrigin;
        internal float Radius => radius;

        private static Vector2[] BuildDirs() {
            var result = new Vector2[Rays + 1];
            for (int i = 0; i <= Rays; i++) {
                float angle = MathHelper.TwoPi * i / Rays;
                result[i] = new Vector2((float)Math.Cos(angle), -(float)Math.Sin(angle));
            }
            return result;
        }

        internal static Vector2 Dir(int index) => dirs[index];

        internal static BlastField Create(Vector2 worldPosition, Submarine submarine, float radius, float life) {
            if (active.Count >= MaxFields) { active.RemoveAt(0); }
            var field = new BlastField {
                origin = worldPosition, sub = submarine,
                subOrigin = submarine?.DrawPosition ?? Vector2.Zero,
                radius = Math.Max(radius, 1f), ttl = life
            };
            field.Trace();
            active.Add(field);
            return field;
        }

        internal void Keep(float life) => ttl = Math.Max(ttl, life);

        internal static void UpdateAll(float deltaTime) {
            for (int i = active.Count - 1; i >= 0; i--) {
                BlastField field = active[i];
                field.ttl -= deltaTime;
                if (field.ttl <= 0f) { active.RemoveAt(i); continue; }
                field.sinceTrace += deltaTime;
                if (field.sinceTrace >= RefreshInterval) { field.Trace(); }
            }
        }

        internal static void Clear() => active.Clear();

        private void Trace() {
            sinceTrace = 0f;
            Vector2 center = Origin;
            Vector2 simOrigin = ConvertUnits.ToSimUnits(center);
            for (int i = 0; i < Rays; i++) {
                Vector2 target = center + dirs[i] * radius;
                float hit = radius;
                try {
                    if (Submarine.PickBody(simOrigin, ConvertUnits.ToSimUnits(target), null,
                            Physics.CollisionWall | Physics.CollisionLevel) != null) {
                        hit = radius * MathHelper.Clamp(Submarine.LastPickedFraction, 0.12f, 1f);
                    }
                } catch (Exception) {
                    hit = radius;
                }
                reach[i] = hit;
            }
            reach[Rays] = reach[0];
            Smooth();
        }

        private void Smooth() {
            float prev = reach[Rays - 1];
            for (int i = 0; i < Rays; i++) {
                float current = reach[i];
                reach[i] = current * 0.6f + (prev + reach[(i + 1) % Rays]) * 0.2f;
                prev = current;
            }
            reach[Rays] = reach[0];
        }

        internal float Reach(int index) => reach[index];

        internal float Reach(float angle) {
            float t = angle / MathHelper.TwoPi * Rays;
            t -= (float)Math.Floor(t / Rays) * Rays;
            int i = (int)t;
            return MathHelper.Lerp(reach[i], reach[i + 1], t - i);
        }

        internal float ReachTowards(Vector2 worldPosition) {
            Vector2 offset = worldPosition - Origin;
            return Reach((float)Math.Atan2(-offset.Y, offset.X));
        }

    }
}
