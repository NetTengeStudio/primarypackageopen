using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using FarseerPhysics.Dynamics.Joints;

namespace MoreBlood
{
  public class Cleaning
  {
    public static void PatchAll(Harmony harmony)
    {
      harmony.Patch(
        original: typeof(Hull).GetMethod("CleanSection", AccessTools.all),
        postfix: new HarmonyMethod(typeof(Cleaning).GetMethod("Hull_CleanSection_Postfix"))
      );
    }

    public static void Hull_CleanSection_Postfix(Hull __instance, BackgroundSection section, float cleanVal, bool updateRequired)
    {
      Mixins.GetHullMixin(__instance).AdvancedDecals.ForEach(decal =>
      {
        if (section.Rect.Intersects(decal.HullRectangle))
        {
          decal.LifeTime -= Mod.Config.DecalCleaningSpeed * decal.TimeLeft;
        }
      });
    }
  }
}
