using System;
using Barotrauma;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Bloom {
    internal static class BlastSmoke {
        internal static float Amount => PrimaryPackage.Core.ModSettings.BlastSmoke.Value;

        private const string Core = "explosionsmoke";
        private const string Billow = "smoke";
        private const string Dust = "dustcloud";
        private const string Bubbles = "risingbubbles";

        internal static void Spawn(Vector2 origin, Hull hull, float attackRange, BlastField field, bool underwater) {
            if (Amount <= 0.001f || attackRange < 1f) { return; }
            Camera cam = GameMain.GameScreen.Cam;
            float budget = Amount * (cam == null ? 1f : MathHelper.Clamp(cam.Zoom, 0.4f, 1f));
            float radius = MathHelper.Clamp(attackRange * 0.35f, 25f, 400f);
            float size = MathHelper.Clamp(attackRange / 600f, 0.35f, 1.5f);
            if (underwater) {
                Emit(Bubbles, origin, hull, field, radius, Count(attackRange * 0.04f, 3, 12, budget),
                    size, 0.15f, 40f);
                Emit(Dust, origin, hull, field, radius * 1.2f, Count(attackRange * 0.02f, 2, 7, budget),
                    size * 1.3f, 0.5f, 15f);
                return;
            }
            Emit(Core, origin, hull, field, radius * 0.5f, Count(attackRange * 0.02f, 2, 6, budget),
                size, 0.08f, 30f);
            Emit(Billow, origin, hull, field, radius, Count(attackRange * 0.035f, 3, 11, budget),
                size * 1.15f, 0.3f, 20f);
            Emit(Dust, origin, hull, field, radius * 1.3f, Count(attackRange * 0.015f, 2, 6, budget),
                size * 1.4f, 0.55f, 10f);
        }

        private static int Count(float wanted, int min, int max, float budget)
            => (int)MathHelper.Clamp(wanted * budget, min * budget, max);

        private static void Emit(string prefab, Vector2 origin, Hull hull, BlastField field,
            float radius, int count, float size, float delay, float drift) {
            for (int i = 0; i < count; i++) {
                float angle = Rand.Range(0f, MathHelper.TwoPi);
                var dir = new Vector2((float)Math.Cos(angle), -(float)Math.Sin(angle));
                float limit = field?.Reach(angle) ?? radius;
                float distance = (float)Math.Sqrt(Rand.Range(0f, 1f)) * Math.Min(radius, limit * 0.7f);
                Particle particle = GameMain.ParticleManager.CreateParticle(prefab,
                    origin + dir * distance, dir * Rand.Range(0f, drift), 0f, hull);
                if (particle == null) { continue; }
                particle.Size *= size * Rand.Range(0.8f, 1.2f);
                particle.StartDelay = Rand.Range(0f, delay);
            }
        }
    }
}
