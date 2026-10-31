using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace MoreBlood
{
  public class FromBleeding
  {
    public static void PatchAll(Harmony harmony)
    {
      harmony.Patch(
        original: typeof(CharacterHealth).GetMethod("UpdateBleedingProjSpecific", AccessTools.all),
        postfix: new HarmonyMethod(typeof(FromBleeding).GetMethod("CharacterHealth_UpdateBleedingProjSpecific_Postfix"))
      );

      harmony.Patch(
        original: typeof(Character).GetMethod("UpdateAll", AccessTools.all),
        postfix: new HarmonyMethod(typeof(FromBleeding).GetMethod("Character_UpdateAll_Postfix"))
      );
    }

    public static double LastDecalCreationTime;
    public static bool ShouldCreate;
    public static void Character_UpdateAll_Postfix(float deltaTime, Camera cam)
    {
      if (Timing.TotalTimeUnpaused - LastDecalCreationTime > Mod.Config.FromBleeding.DecalCreationInterval)
      {
        LastDecalCreationTime = Timing.TotalTimeUnpaused;
        ShouldCreate = false;
      }
      else
      {
        ShouldCreate = true;
      }
    }


    public static void AddBleedingDecal(CharacterHealth _, AfflictionBleeding affliction, Limb targetLimb, float deltaTime)
    {
      if (_.Character.CurrentHull is not null)
      {
        BleedingConfig config = Mod.Config.FromBleeding;

        Vector2 limbSpeed = targetLimb.LinearVelocity - _.Character.AnimController.Collider.LinearVelocity;

        float vitalityFactor = 1 + (_.Character.Params.Health.Vitality - Utils.HumanVitality) / Utils.HumanVitality * Mod.Config.VitalityMultiplier;

        float pulseSpeed =
          config.BasicPulseSpeed *
          (_.Character.IsUnconscious ? config.UnconciousPulseSpeed : 1.0f);

        float pulseFactor =
          1.0f +
          (float)Math.Pow(
            Math.Sin(
              (Timing.TotalTime - Mod.PulseOffset(_.Character)) *
              pulseSpeed
            ),
            config.PulseSteepness
          ) * config.PulseFlowFactor;

        float severityFactor = (affliction.Strength / affliction.Prefab.MaxStrength);

        float bloodDecalSize =
           (
            config.MinFlow +
            Mod.Config.GlobalBloodAmount * Mod.Config.FromBleeding.BloodAmountFromBleeding *
            vitalityFactor * severityFactor * (
              config.SeverityFlowFactor +
              config.LimbSpeedFlowFactor * limbSpeed.Length()
            ) * pulseFactor
          );

        bloodDecalSize *= _.Character.IsUnconscious ? config.UnconciousBloodFlow : 1.0f;

        if (bloodDecalSize < config.FlowCutoff) return;

        Vector2 decalPos =
          targetLimb.WorldPosition +
          config.LimbSpeedPosFactor * limbSpeed +
          config.RandomPosFactor * new Vector2(Utils.Random.NextSingle(), Utils.Random.NextSingle());

        AdvancedDecal decal = _.Character.CurrentHull?.AddDecal(
          AdvancedDecal.Create(_.Character.BloodDecalName, bloodDecalSize),
          decalPos
        );

        decal.LifeTime *= config.LifetimeMultiplier;
      }
    }

    public static void CharacterHealth_UpdateBleedingProjSpecific_Postfix(CharacterHealth __instance, AfflictionBleeding affliction, Limb targetLimb, float deltaTime)
    {
      if (ShouldCreate) return;
      try
      {
        AddBleedingDecal(__instance, affliction, targetLimb, deltaTime);
      }
      catch (Exception e) { Mod.Warning($"AddBleedingDecal threw: {e.Message}"); }
    }
  }
}
