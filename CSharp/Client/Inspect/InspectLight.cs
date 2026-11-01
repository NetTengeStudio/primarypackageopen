using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Lights;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Inspect {
    public static class InspectLight {
        private const float NegligibleFraction = 0.05f;
        private const float RayOffset = 32.0f;
        private const float SteadyFloor = 0.5f;

        private static readonly Predicate<Fixture> blocks = Blocks;
        private static MapEntity ignored;

        public static bool IsDark(in InspectTarget target) => Level(target) < target.Settings.DarkThreshold;

        public static float Level(in InspectTarget target) {
            if (target.Settings.DarkThreshold <= 0.0f) { return 1.0f; }
            LightManager manager = GameMain.LightManager;
            if (manager == null || !manager.LightingEnabled) { return 1.0f; }
            MapEntity entity = target.Entity;
            Vector2 world = entity.WorldPosition;
            Hull hull = entity is Item item ? item.CurrentHull ?? Hull.FindHull(world) : Hull.FindHull(world);
            Color ambient = hull != null && hull.AmbientLight.A > 0 ? hull.AmbientLight : manager.AmbientLight;
            float total = Luminance(ambient);
            float threshold = target.Settings.DarkThreshold;
            float floor = threshold * NegligibleFraction;
            ignored = entity;
            if (manager.Lights is List<LightSource> lights) {
                for (int i = 0; i < lights.Count && total < threshold; i++) {
                    total += Contribution(lights[i], world, floor);
                }
            }
            ignored = null;
            return total;
        }

        private static float Contribution(LightSource light, Vector2 world, float floor) {
            if (light == null || !light.Enabled || light.Range < 1.0f || light.Color.A < 1) { return 0.0f; }
            Vector2 position = light.WorldPosition;
            float dx = position.X - world.X, dy = position.Y - world.Y;
            float range = light.Range;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= range * range) { return 0.0f; }
            float distance = (float)Math.Sqrt(distanceSquared);
            float steady = SteadyFloor + (1.0f - SteadyFloor) * MathHelper.Clamp(light.CurrentBrightness, 0.0f, 1.0f);
            float value = Luminance(light.Color) * steady * (1.0f - distance / range);
            if (value < floor) { return 0.0f; }
            if (distance <= RayOffset) { return value; }
            var from = new Vector2(world.X + dx / distance * RayOffset, world.Y + dy / distance * RayOffset);
            return Occluded(from, position) ? 0.0f : value;
        }

        private static float Luminance(Color color) =>
            (color.R + color.G + color.B) * (color.A / 255.0f) * (1.0f / 765.0f);

        private static bool Occluded(Vector2 from, Vector2 to) =>
            Submarine.CheckVisibility(ConvertUnits.ToSimUnits(from), ConvertUnits.ToSimUnits(to),
                blocksVisibilityPredicate: blocks) != null;

        private static bool Blocks(Fixture fixture) {
            object owner = fixture?.Body?.UserData;
            if (ReferenceEquals(owner, ignored)) { return false; }
            if (owner is Structure wall) { return wall.CastShadow; }
            return owner is Item item && item.GetComponent<Door>() is { IsOpen: false, HasWindow: false };
        }
    }
}
