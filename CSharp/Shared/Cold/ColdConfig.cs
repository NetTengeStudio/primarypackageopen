using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Cold {
    public static class ColdConfig {
        private const string RelativePath = "Data/pp_cold.xml";

        public static float TickInterval = 0.5f;
        public static float BaseTemperature = 20.0f;
        public static float RelaxRate = 0.035f;
        public static float Conductance = 0.85f;
        public static float MinTemperature = -45.0f;
        public static float MaxTemperature = 60.0f;
        public static float MinHullVolume = 20000.0f;
        public static float LinkArea = 3000.0f;
        public static float WaterHeatCapacity = 4.0f;

        public static float NetInterval = 1.0f;
        public static float NetKeepAlive = 20.0f;

        public static Identifier AfflictionId = "pp_hypothermia".ToIdentifier();
        public static float MaxStrength = 100.0f;
        public static float Comfort = 12.0f;
        public static float GainPerDegree = 0.03f;
        public static float WetMultiplier = 2.4f;
        public static float WetRise = 0.8f;
        public static float WetDry = 0.035f;
        public static float InsulationScale = 14.0f;
        public static float ShiverResist = 0.55f;
        public static float ShiverPeak = 0.35f;
        public static float ShiverFail = 0.8f;
        public static float ApplyInterval = 1.0f;
        public static float WarmRecoveryPerDegree = 0.02f;
        public static LimbType TargetLimb = LimbType.Torso;
        public static float NoHullTemperature = 12.0f;

        public static Color TintColor = new Color(86, 148, 235);
        public static float MaxTint = 0.2f;
        public static float TintUniform = 0.32f;
        public static Color VignetteColor = new Color(16, 42, 96);
        public static float MaxVignette = 0.72f;
        public static float VignetteStart = 0.3f;
        public static Color BlinkColor = new Color(0, 0, 0);
        public static float CriticalZoomAmount = 0.08f;
        public static float CriticalZoomRate = 1.4f;
        public static float FrostDrift = 0.021f;
        public static float MaxShake = 2.6f;
        public static float ShakeThreshold = 0.14f;
        public static float ShiverBurstMin = 0.5f;
        public static float ShiverBurstMax = 2.1f;
        public static float ShiverDuty = 0.42f;
        public static float BreathPeriodWarm = 5.0f;
        public static float BreathPeriodFast = 3.2f;
        public static float BreathPeriodCold = 9.0f;
        public static float BreathExhaleFraction = 0.62f;
        public static float BreathTemperature = 7.0f;
        public static float BreathFalloff = 15.0f;
        public static float BreathRate = 9.0f;
        public static float BreathSpeed = 160.0f;
        public static float BreathTilt = 10.0f;
        public static float BreathSpread = 14.0f;
        public static float BreathGap = 7.0f;
        public static float BreathMouthOffset = 11.0f;
        public static float BreathMouthDrop = 4.0f;
        public static string BreathParticle = "pp_coldbreath";
        public static float BreathScale = 1.0f;
        public static float MaxBlink = 0.55f;
        public static float BlinkThreshold = 0.8f;
        public static float BlinkInterval = 7.5f;
        public static float BlinkDuration = 0.9f;
        public static float RiseRate = 1.3f;
        public static float FallRate = 0.85f;
        public static float InterpolationRate = 2.2f;
        public static float HeartbeatPulse = 0.16f;
        public static float HeartbeatRate = 0.85f;
        public static Color HeatTintColor = new Color(235, 130, 70);
        public static float MaxHeatTint = 0.22f;
        public static Color HeatVignetteColor = new Color(120, 30, 12);
        public static float MaxHeatVignette = 0.6f;
        public static float HeatDrift = 0.085f;
        public static Color PainColor = new Color(150, 40, 40);
        public static float MaxPain = 0.45f;
        public static Color GreyColor = new Color(118, 122, 130);
        public static float MaxGrey = 0.34f;
        public static float GreyStart = 0.55f;

        public static bool NtEnabled = true;
        public static float NtInterval = 1.0f;
        public static float NtWaterMargin = 6.0f;
        public static float NtShockPerDegree = 0.6f;
        public static float NtShockCap = 15.0f;
        public static float NtShockMargin = 25.0f;
        public static float NtShockAdapt = 0.06f;
        public static float NtShockCooldown = 20.0f;
        public static Identifier NtShockAffliction = "traumaticshock".ToIdentifier();

        public static string DebugMapKey = "F6";
        public static bool DebugAllowChatKey;
        public static Color DebugColdColor = new Color(86, 148, 235);
        public static Color DebugNeutralColor = new Color(150, 152, 156);
        public static Color DebugWarmColor = new Color(235, 165, 105);
        public static string[] DebugStageNames = { "норма", "озноб", "дрожь", "переохлаждение", "критическое" };

        public static readonly Dictionary<Identifier, XElement> Treatments = new Dictionary<Identifier, XElement>();
        public static readonly Dictionary<Identifier, float> WetTreatments = new Dictionary<Identifier, float>();

        private static bool loaded;

        public static void EnsureLoaded() {
            if (loaded) { return; }
            loaded = true;
            XElement root = ModPrefs.Root(RelativePath);
            if (root == null) { return; }

            XElement sim = root.Element("Simulation");
            if (sim != null) {
                TickInterval = Float(sim, "tickinterval", TickInterval);
                BaseTemperature = Float(sim, "basetemperature", BaseTemperature);
                RelaxRate = Float(sim, "relaxrate", RelaxRate);
                Conductance = Float(sim, "conductance", Conductance);
                MinTemperature = Float(sim, "mintemperature", MinTemperature);
                MaxTemperature = Float(sim, "maxtemperature", MaxTemperature);
                MinHullVolume = Float(sim, "minhullvolume", MinHullVolume);
                LinkArea = Float(sim, "linkarea", LinkArea);
                WaterHeatCapacity = Float(sim, "waterheatcapacity", WaterHeatCapacity);
            }

            XElement net = root.Element("Network");
            if (net != null) {
                NetInterval = Float(net, "interval", NetInterval);
                NetKeepAlive = Float(net, "keepalive", NetKeepAlive);
            }

            XElement aff = root.Element("Affliction");
            if (aff != null) {
                string id = (string)aff.Attribute("identifier");
                if (!string.IsNullOrWhiteSpace(id)) { AfflictionId = id.ToIdentifier(); }
                MaxStrength = Float(aff, "maxstrength", MaxStrength);
                Comfort = Float(aff, "comfort", Comfort);
                GainPerDegree = Float(aff, "gainperdegree", GainPerDegree);
                WetMultiplier = Float(aff, "wetmultiplier", WetMultiplier);
                WetRise = Float(aff, "wetrise", WetRise);
                WetDry = Float(aff, "wetdry", WetDry);
                InsulationScale = Float(aff, "insulationscale", InsulationScale);
                ShiverResist = Float(aff, "shiverresist", ShiverResist);
                ShiverPeak = Float(aff, "shiverpeak", ShiverPeak);
                ShiverFail = Float(aff, "shiverfail", ShiverFail);
                ApplyInterval = Float(aff, "applyinterval", ApplyInterval);
                WarmRecoveryPerDegree = Float(aff, "warmrecoveryperdegree", WarmRecoveryPerDegree);
                NoHullTemperature = Float(aff, "nohulltemperature", NoHullTemperature);
                ColdAffliction.Warming.Clear();
                foreach (XElement warm in aff.Elements("Warming")) {
                    string warmId = (string)warm.Attribute("identifier");
                    if (string.IsNullOrWhiteSpace(warmId)) { continue; }
                    ColdAffliction.Warming.Add((ColdSnapshot.Register(warmId.Trim().ToIdentifier()),
                        warm.GetAttributeFloat("scale", 0.1f)));
                }
                string limb = (string)aff.Attribute("limb");
                if (!string.IsNullOrWhiteSpace(limb) && System.Enum.TryParse(limb, true, out LimbType parsed)) {
                    TargetLimb = parsed;
                }
            }

            XElement vis = root.Element("Visuals");
            if (vis != null) {
                TintColor = ParseColor(vis, "tintcolor", TintColor);
                MaxTint = Float(vis, "maxtint", MaxTint);
                TintUniform = Float(vis, "tintuniform", TintUniform);
                VignetteColor = ParseColor(vis, "vignettecolor", VignetteColor);
                MaxVignette = Float(vis, "maxvignette", MaxVignette);
                VignetteStart = Float(vis, "vignettestart", VignetteStart);
                BlinkColor = ParseColor(vis, "blinkcolor", BlinkColor);
                CriticalZoomAmount = Float(vis, "criticalzoomamount", CriticalZoomAmount);
                CriticalZoomRate = Float(vis, "criticalzoomrate", CriticalZoomRate);
                FrostDrift = Float(vis, "frostdrift", FrostDrift);
                MaxShake = Float(vis, "maxshake", MaxShake);
                ShakeThreshold = Float(vis, "shakethreshold", ShakeThreshold);
                ShiverBurstMin = Float(vis, "shiverburstmin", ShiverBurstMin);
                ShiverBurstMax = Float(vis, "shiverburstmax", ShiverBurstMax);
                ShiverDuty = Float(vis, "shiverduty", ShiverDuty);
                BreathPeriodWarm = Float(vis, "breathperiodwarm", BreathPeriodWarm);
                BreathPeriodFast = Float(vis, "breathperiodfast", BreathPeriodFast);
                BreathPeriodCold = Float(vis, "breathperiodcold", BreathPeriodCold);
                BreathExhaleFraction = Float(vis, "breathexhalefraction", BreathExhaleFraction);
                BreathTemperature = Float(vis, "breathtemperature", BreathTemperature);
                BreathFalloff = Float(vis, "breathfalloff", BreathFalloff);
                BreathRate = Float(vis, "breathrate", BreathRate);
                BreathSpeed = Float(vis, "breathspeed", BreathSpeed);
                BreathTilt = Float(vis, "breathtilt", BreathTilt);
                BreathSpread = Float(vis, "breathspread", BreathSpread);
                BreathGap = Float(vis, "breathgap", BreathGap);
                BreathMouthOffset = Float(vis, "breathmouthoffset", BreathMouthOffset);
                BreathMouthDrop = Float(vis, "breathmouthdrop", BreathMouthDrop);
                BreathScale = Float(vis, "breathscale", BreathScale);
                MaxBlink = Float(vis, "maxblink", MaxBlink);
                BlinkThreshold = Float(vis, "blinkthreshold", BlinkThreshold);
                BlinkInterval = Float(vis, "blinkinterval", BlinkInterval);
                BlinkDuration = Float(vis, "blinkduration", BlinkDuration);
                string breathParticle = (string)vis.Attribute("breathparticle");
                if (!string.IsNullOrWhiteSpace(breathParticle)) { BreathParticle = breathParticle.Trim(); }
                RiseRate = Float(vis, "riserate", RiseRate);
                FallRate = Float(vis, "fallrate", FallRate);
                InterpolationRate = Float(vis, "interpolationrate", InterpolationRate);
                HeartbeatPulse = Float(vis, "heartbeatpulse", HeartbeatPulse);
                HeartbeatRate = Float(vis, "heartbeatrate", HeartbeatRate);
                HeatTintColor = ParseColor(vis, "heattintcolor", HeatTintColor);
                MaxHeatTint = Float(vis, "maxheattint", MaxHeatTint);
                HeatVignetteColor = ParseColor(vis, "heatvignettecolor", HeatVignetteColor);
                MaxHeatVignette = Float(vis, "maxheatvignette", MaxHeatVignette);
                HeatDrift = Float(vis, "heatdrift", HeatDrift);
                PainColor = ParseColor(vis, "paincolor", PainColor);
                MaxPain = Float(vis, "maxpain", MaxPain);
                GreyColor = ParseColor(vis, "greycolor", GreyColor);
                MaxGrey = Float(vis, "maxgrey", MaxGrey);
                GreyStart = Float(vis, "greystart", GreyStart);
            }

            XElement debug = root.Element("Debug");
            if (debug != null) {
                string key = (string)debug.Attribute("mapkey");
                if (!string.IsNullOrWhiteSpace(key)) { DebugMapKey = key.Trim(); }
                DebugAllowChatKey = (bool?)debug.Attribute("allowchatkeyoverride") ?? DebugAllowChatKey;
                DebugColdColor = ParseColor(debug, "coldcolor", DebugColdColor);
                DebugNeutralColor = ParseColor(debug, "neutralcolor", DebugNeutralColor);
                DebugWarmColor = ParseColor(debug, "warmcolor", DebugWarmColor);
                string names = (string)debug.Attribute("stagenames");
                if (!string.IsNullOrWhiteSpace(names)) {
                    string[] split = names.Split(',');
                    for (int i = 0; i < split.Length; i++) { split[i] = split[i].Trim(); }
                    if (split.Length > 0) { DebugStageNames = split; }
                }
            }

            XElement nt = root.Element("Neurotrauma");
            if (nt != null) {
                NtEnabled = (bool?)nt.Attribute("enabled") ?? NtEnabled;
                NtInterval = Float(nt, "interval", NtInterval);
                NtWaterMargin = Float(nt, "watermargin", NtWaterMargin);
                NtShockPerDegree = Float(nt, "shockperdegree", NtShockPerDegree);
                NtShockCap = Float(nt, "shockcap", NtShockCap);
                NtShockMargin = Float(nt, "shockmargin", NtShockMargin);
                NtShockAdapt = Float(nt, "shockadapt", NtShockAdapt);
                NtShockCooldown = Float(nt, "shockcooldown", NtShockCooldown);
                string shockId = (string)nt.Attribute("shockaffliction");
                if (!string.IsNullOrWhiteSpace(shockId)) { NtShockAffliction = shockId.Trim().ToIdentifier(); }
            }

            ColdErcHeat.Load(root);
            ColdIgnite.Load(root);
            ColdHyperthermia.Load(root);

            ColdSubject.Load(root);
            ColdSuitHeater.Load(root);
            ColdFrostbite.Load(root);
            ColdReactorHeat.Load(root);
            ColdComplications.Load(root);
            ColdSuitPatch.Load(root);
            ColdStasis.Load(root);

            ColdAffliction.Slot = ColdSnapshot.Register(AfflictionId);
            ColdHyperthermia.Slot = ColdSnapshot.Register(ColdHyperthermia.AfflictionId);
            ColdStasis.StasisSlot = ColdSnapshot.Register(ColdStasis.StasisAffliction);
            ColdStasis.StarterSlot = ColdSnapshot.Register(ColdStasis.StarterAffliction);

            XElement treat = root.Element("Treatment");
            if (treat == null) { return; }
            foreach (XElement item in treat.Elements("Item")) {
                string id = (string)item.Attribute("identifier");
                if (string.IsNullOrWhiteSpace(id)) { continue; }
                Identifier identifier = id.ToIdentifier();
                foreach (XElement effect in item.Elements("StatusEffect")) {
                    Treatments[identifier] = effect;
                    break;
                }
                float dry = item.GetAttributeFloat("drywetness", 0.0f);
                if (dry > 0.0f) { WetTreatments[identifier] = dry; }
            }
        }

        private static float Float(XElement element, string name, float fallback) =>
            element.GetAttributeFloat(name, fallback);

        private static Color ParseColor(XElement element, string name, Color fallback) {
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? fallback : XMLExtensions.ParseColor(attribute.Value, false);
        }
    }
}
