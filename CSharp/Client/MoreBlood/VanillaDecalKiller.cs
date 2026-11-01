using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System.IO;

namespace MoreBlood
{
  public class VanillaDecalKiller
  {
    public static string[] BloodDecalNames = new string[] { "blood", "blackblood" };
    public Dictionary<string, Color> OriginalColors = new();

    private static readonly FieldInfo ColorField = AccessTools.Field(typeof(DecalPrefab), "Color");

    public void HideVanillaBlood()
    {
      if (ColorField == null) { return; }
      foreach (string name in BloodDecalNames)
      {
        if (!DecalManager.Prefabs.ContainsKey(name)) { continue; }
        DecalPrefab prefab = DecalManager.Prefabs[name];
        OriginalColors[name] = prefab.Color;
        ColorField.SetValue(prefab, Color.Transparent);
      }
    }

    public void RestoreVanillaBlood()
    {
      if (ColorField == null) { return; }
      foreach (KeyValuePair<string, Color> entry in OriginalColors)
      {
        if (!DecalManager.Prefabs.ContainsKey(entry.Key)) { continue; }
        ColorField.SetValue(DecalManager.Prefabs[entry.Key], entry.Value);
      }
      OriginalColors.Clear();
    }
  }
}
