using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MoreBlood
{
  public class HullPatches
  {
    public static Stopwatch sw = new Stopwatch();
    public static void PatchAll(Harmony harmony)
    {

      harmony.Patch(
        original: typeof(Hull).GetMethod("DrawDecals", AccessTools.all),
        postfix: new HarmonyMethod(typeof(HullPatches).GetMethod("Hull_DrawDecals_Postfix"))
      );

      harmony.Patch(
        original: typeof(Hull).GetMethod("IsVisible", AccessTools.all),
        postfix: new HarmonyMethod(typeof(HullPatches).GetMethod("Hull_IsVisible_Postfix"))
      );

      harmony.Patch(
        original: typeof(Hull).GetMethod("get_DrawBelowWater", AccessTools.all),
        postfix: new HarmonyMethod(typeof(HullPatches).GetMethod("Hull_get_DrawBelowWater_Postfix"))
      );
    }

    public static void Hull_DrawDecals_Prefix(Hull __instance, SpriteBatch spriteBatch)
    {
      sw.Restart();
    }

    public static void Hull_DrawDecals_Postfix(Hull __instance, SpriteBatch spriteBatch)
    {
      Mixins.GetHullMixin(__instance).DrawAdvancedDecals(spriteBatch);

    }

    public static void Hull_IsVisible_Postfix(Hull __instance, ref bool __result)
    {
      __result = __result || Mixins.GetHullMixin(__instance).AdvancedDecals.Count > 0;
    }

    public static void Hull_get_DrawBelowWater_Postfix(Hull __instance, ref bool __result)
    {
      __result = __result || Mixins.GetHullMixin(__instance).AdvancedDecals.Count > 0;
    }
  }
}
