using System;

namespace PrimaryPackage.Thoughts {
    internal struct ThoughtThermal {
        private float skin, core, frost, pain, heat;
        internal bool Present { get; private set; }
        internal float Cold => MathF.Max(skin * ThoughtSelector.ColdFreezing, MathF.Max(core > 0 ? ThoughtSelector.ColdFreezing + core * (1 - ThoughtSelector.ColdFreezing) : 0, frost > 0 ? ThoughtSelector.ColdLethal + frost * (1 - ThoughtSelector.ColdLethal) : 0));
        internal float Pain => pain * ThoughtSelector.PainAudible;
        internal bool Shivering => skin > .2f && core < .3f;
        internal bool Hypothermic => core > 0;
        internal bool Overheated => heat > 0;
        internal void Include(string identifier, float strength) {
            float value = Math.Clamp(strength / 100, 0, 1);
            switch (identifier) {
                case "scpitd_chilledskin": skin = MathF.Max(skin, value); break;
                case "scpitd_cold": core = MathF.Max(core, value); break;
                case "scpitd_frostbite": frost = MathF.Max(frost, value); break;
                case "scpitd_rewarmingpain": pain = MathF.Max(pain, value); break;
                case "scpitd_overheated": heat = MathF.Max(heat, value); break;
                default: return;
            }
            Present = true;
        }
    }
}
