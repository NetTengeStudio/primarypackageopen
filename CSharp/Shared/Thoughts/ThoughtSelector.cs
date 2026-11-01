namespace PrimaryPackage.Thoughts {
    public static class ThoughtSelector {
        public const float PainAudible = 75.0f;
        public const float PainAgony = 108.0f;
        public const float PainAlert = 65.0f;
        public const float PainAche = 32.0f;
        public const float BloodlossAlert = 48.0f;
        public const float BloodlossFatal = 68.0f;
        public const float HungerLow = 25.0f;
        public const float HungerHigh = 45.0f;
        public const float BrainSlight = 0.14f;
        public const float BrainHeavy = 0.34f;
        public const float FearActive = 0.5f;
        public const float FearBored = -0.6f;
        public const float ColdChill = 0.12f;
        public const float ColdFreezing = 0.4f;
        public const float ColdLethal = 0.72f;

        public static float Weight(in ThoughtSignals s) {
            if (s.Has(ThoughtFlags.Unconscious)) { return 0.0f; }
            if (s.Has(ThoughtFlags.JustWoke)) { return 6.0f; }
            if (s.Has(ThoughtFlags.FreshBone) || s.Has(ThoughtFlags.Amputation)) { return 5.0f; }
            if (s.Pain > PainAlert) { return 5.0f; }
            if (s.Cold > ColdLethal) { return 5.0f; }
            if (s.Has(ThoughtFlags.Choking)) { return 2.4f; }
            if (s.Cold > ColdFreezing) { return 1.6f; }
            if (s.Bloodloss > BloodlossAlert) { return 1.1f; }
            if (s.Has(ThoughtFlags.Fever)) { return 0.6f; }
            if (s.Fear > FearActive) { return s.Has(ThoughtFlags.Aimed) ? 2.0f : 0.9f; }
            if (s.Pain > PainAche) { return 1.5f; }
            if (s.Brain > BrainSlight) { return s.Brain * 4.0f; }
            if (s.Cold > ColdChill) { return 0.34f; }
            if (s.Hunger > HungerLow) { return 0.28f; }
            if (s.Fear < FearBored) { return 0.06f; }
            return 0.0f;
        }

        public static ThoughtCategory Pick(in ThoughtSignals s, ref ThoughtRandom rng) {
            if (s.Has(ThoughtFlags.Unconscious)) { return ThoughtCategory.None; }
            if (s.Brain > BrainHeavy) { return ThoughtCategory.Broken; }

            ThoughtCategory choice = ThoughtCategory.None;

            if (s.Has(ThoughtFlags.Choking)) { choice = ThoughtCategory.Choke; }

            if (choice == ThoughtCategory.None &&
                (s.Bloodloss > BloodlossAlert || s.Pain > PainAudible || s.BoneDamaged || s.Cold > ColdLethal ||
                 s.Has(ThoughtFlags.FreshBone) || s.Has(ThoughtFlags.Amputation))) {
                choice = Critical(s, ref rng);
            }

            if (choice == ThoughtCategory.None && s.Has(ThoughtFlags.JustWoke)) {
                choice = ThoughtCategory.Wake;
            }

            if (choice == ThoughtCategory.None && s.Cold > ColdFreezing && rng.Chance(0.7f)) {
                choice = ThoughtCategory.Freezing;
            }

            if (choice == ThoughtCategory.None && s.Has(ThoughtFlags.Fever) && rng.Chance(0.5f)) {
                choice = ThoughtCategory.Fever;
            }

            if (choice == ThoughtCategory.None && s.Cold > ColdChill && rng.Chance(0.4f)) {
                choice = ThoughtCategory.Chill;
            }

            if (choice == ThoughtCategory.None && s.Hunger > HungerLow && rng.Chance(0.34f)) {
                choice = s.Hunger > HungerHigh ? ThoughtCategory.Starving : ThoughtCategory.Hungry;
            }

            if (choice == ThoughtCategory.None && s.Fear > FearActive) {
                choice = s.Has(ThoughtFlags.Aimed) ? ThoughtCategory.Aimed
                    : rng.Chance(0.12f) ? ThoughtCategory.Irony : ThoughtCategory.Fear;
            }

            if (choice == ThoughtCategory.None && s.Fear < FearBored) {
                choice = s.Hunger > HungerLow && rng.Chance(0.25f)
                    ? s.Hunger > HungerHigh ? ThoughtCategory.Starving : ThoughtCategory.Hungry
                    : ThoughtCategory.Idle;
            }

            if (choice == ThoughtCategory.None && s.Pain > PainAlert) { choice = ThoughtCategory.Pain; }
            if (choice == ThoughtCategory.None && s.Pain > PainAche) { choice = ThoughtCategory.Fear; }

            if (s.Brain > BrainSlight && choice != ThoughtCategory.None) { return ThoughtCategory.Daze; }
            return choice;
        }

        private static ThoughtCategory Critical(in ThoughtSignals s, ref ThoughtRandom rng) {
            if (s.Pain > PainAgony) { return ThoughtCategory.Agony; }
            if (s.Cold > ColdLethal && (s.Pain <= PainAudible || rng.Chance(0.5f))) {
                return ThoughtCategory.Frostbite;
            }
            bool fresh = s.Has(ThoughtFlags.FreshBone) && s.BoneDamaged;
            if (s.Has(ThoughtFlags.Amputation) && (!fresh || rng.Chance(0.5f))) {
                return ThoughtCategory.Amputation;
            }
            if (s.Pain > PainAudible) {
                if (fresh && rng.Chance(0.5f)) { return Bone(s); }
                return ThoughtCategory.Pain;
            }
            if (fresh) { return Bone(s); }
            if (s.Bloodloss > BloodlossFatal) { return ThoughtCategory.NearDeath; }
            if (s.Bloodloss > BloodlossAlert) {
                if (s.Has(ThoughtFlags.Adrenaline) && s.Fear < FearActive) { return ThoughtCategory.Will; }
                return rng.Chance(0.35f) ? ThoughtCategory.Will : ThoughtCategory.NearDeath;
            }
            if (s.BoneDamaged) { return Bone(s); }
            return s.Has(ThoughtFlags.FreshBone) ? ThoughtCategory.Pain : ThoughtCategory.None;
        }

        private static ThoughtCategory Bone(in ThoughtSignals s) =>
            s.Has(ThoughtFlags.Fracture) ? ThoughtCategory.Fracture
            : s.Has(ThoughtFlags.Dislocation) ? ThoughtCategory.Dislocation
            : ThoughtCategory.Pain;
    }
}
