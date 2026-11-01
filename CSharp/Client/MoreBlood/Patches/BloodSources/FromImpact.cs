using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace MoreBlood
{

  public class FromImpact
  {
    public static void PatchAll(Harmony harmony)
    {
      harmony.Patch(
        original: typeof(Limb).GetMethod("AddDamage", AccessTools.all, new Type[]{
          typeof(Vector2),
          typeof(IEnumerable<Affliction>),
          typeof(bool),
          typeof(float),
          typeof(float),
          typeof(Character),
        }),
        postfix: new HarmonyMethod(typeof(FromImpact).GetMethod("Limb_AddDamage_Postfix"))
      );
    }

    public static void AddDecal(Limb _, float bleedingDamage, Vector2? offset = null)
    {
      Vector2 realOffset = offset ?? Vector2.Zero;

      float vitalityFactor = 1 + (_.character.Params.Health.Vitality - Utils.HumanVitality) / Utils.HumanVitality * Mod.Config.VitalityMultiplier;

      float bloodDecalSize =
        Mod.Config.FromImpact.BloodAmountFromImpact * Mod.Config.GlobalBloodAmount * (
          Mod.Config.FromImpact.MinSplash +
          bleedingDamage * Mod.Config.FromImpact.BleedingDamageToDecalSize * vitalityFactor
        );

      if (bloodDecalSize < Mod.Config.FromImpact.Cutoff) return;

      AdvancedDecal decal = _.character.CurrentHull.AddDecal(
        AdvancedDecal.Create(_.character.BloodDecalName, bloodDecalSize),
        _.WorldPosition + realOffset
      );

      decal.LifeTime *= Mod.Config.FromImpact.LifetimeMultiplier;
    }

    public static void AddDecalFromProjectile(Limb _, float bleedingDamage, ProjectileDamageContext context)
    {
      Vector2 direction = context.Item.body.LinearVelocity / context.Item.body.LinearVelocity.Length();
      Vector2 offset =
        direction * Mod.Config.FromImpact.OfProjectile.MinBloodFlyDistance +
        direction * Mod.Config.FromImpact.OfProjectile.BloodSpeed * Utils.Random.NextSingle();

      AddDecal(_, bleedingDamage, offset + _.character.AnimController.Collider.LinearVelocity);
    }

    public static void AddDecalFromMelee(Limb _, float bleedingDamage, MeleeDamageContext context)
    {
      Vector2 direction = context.Item.body.LinearVelocity / context.Item.body.LinearVelocity.Length();

      Vector2 offset =
        direction * Mod.Config.FromImpact.OfMeleeWeapon.MinBloodFlyDistance +
        direction * Mod.Config.FromImpact.OfMeleeWeapon.BloodSpeed * Utils.Random.NextSingle();

      AddDecal(_, bleedingDamage, offset + _.character.AnimController.Collider.LinearVelocity);
    }

    public static void Limb_AddDamage_Postfix(Limb __instance, ref AttackResult __result, Vector2 simPosition, IEnumerable<Affliction> afflictions, bool playSound, float damageMultiplier = 1, float penetration = 0f, Character attacker = null)
    {
      if (__result.Afflictions is null) return;
      Limb _ = __instance;

      if (_.character.GodMode) return;

      float bleedingDamage = 0;
      try
      {
        if (_.character.CharacterHealth.DoesBleed)
        {
          foreach (var affliction in __result.Afflictions)
          {
            if (affliction is AfflictionBleeding)
            {
              bleedingDamage += affliction.GetVitalityDecrease(_.character.CharacterHealth);
            }
          }

          if (bleedingDamage > 0 && _.character.CurrentHull is not null)
          {
            switch (DamageContext.Current)
            {
              case ProjectileDamageContext pc:
                AddDecalFromProjectile(__instance, bleedingDamage, pc);
                break;

              case MeleeDamageContext mc:
                AddDecalFromMelee(__instance, bleedingDamage, mc);
                break;

              default: AddDecal(__instance, bleedingDamage); break;
            }
          }
        }
      }
      catch (Exception e) { Mod.Warning(e.Message); }
    }
  }
}
