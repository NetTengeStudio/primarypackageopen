using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PrimaryPackage.Thoughts {
    public enum ThoughtCategory : byte {
        None = 0,
        Idle,
        Fear,
        Irony,
        Aimed,
        Pain,
        Agony,
        Fracture,
        Dislocation,
        Amputation,
        NearDeath,
        Will,
        Wake,
        Daze,
        Broken,
        Hungry,
        Starving,
        Fever,
        Choke,
        Chill,
        Freezing,
        Frostbite,
        Darkness
    }

    [Flags]
    public enum ThoughtFlags : ushort {
        None = 0,
        Fracture = 1 << 0,
        Dislocation = 1 << 1,
        FreshBone = 1 << 2,
        Amputation = 1 << 3,
        Unconscious = 1 << 4,
        JustWoke = 1 << 5,
        Aimed = 1 << 6,
        Adrenaline = 1 << 7,
        Choking = 1 << 8,
        Fever = 1 << 9,
        Sedated = 1 << 10,
        Muted = 1 << 11,
        Shivering = 1 << 12,
        Hypothermic = 1 << 13
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct ThoughtSignals {
        public readonly float Pain;
        public readonly float Bloodloss;
        public readonly float Hunger;
        public readonly float Brain;
        public readonly float Fear;
        public readonly float Vitality;
        public readonly float Cold;
        public readonly float Severity;
        public readonly ThoughtFlags Flags;
        private readonly ushort _pad;

        public ThoughtSignals(float pain, float bloodloss, float hunger, float brain, float fear, float vitality,
                              float cold, ThoughtFlags flags) {
            Pain = pain;
            Bloodloss = bloodloss;
            Hunger = hunger;
            Brain = brain;
            Fear = fear;
            Vitality = vitality;
            Cold = cold;
            Flags = flags;
            _pad = 0;

            float s = pain * 0.009f;
            float bleed = bloodloss * 0.011f;
            if (bleed > s) { s = bleed; }
            float weak = 1.0f - vitality;
            if (weak > s) { s = weak; }
            if (cold > s) { s = cold; }
            Severity = s < 0.0f ? 0.0f : s > 1.0f ? 1.0f : s;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(ThoughtFlags flag) => (Flags & flag) != 0;

        public bool BoneDamaged => (Flags & (ThoughtFlags.Fracture | ThoughtFlags.Dislocation)) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ThoughtRandom {
        private uint state;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ThoughtRandom(uint seed) { state = seed | 1u; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Next() {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Unit() => (Next() >> 8) * (1.0f / 16777216.0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Below(int exclusiveMax) => exclusiveMax <= 1 ? 0 : (int)(Unit() * exclusiveMax) % exclusiveMax;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Chance(float probability) => Unit() < probability;
    }
}
