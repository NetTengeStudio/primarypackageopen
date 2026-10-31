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
  public partial class Mod : PrimaryPackage.Core.IModule
  {
    private static ContentPackage package;
    public static ContentPackage Package
    {
      get
      {
        if (package == null)
        {
          package = PrimaryPackage.Core.ModAssets.Package;
        }
        return package;
      }
      set => package = value;
    }

    public static string ConfigPath => PrimaryPackage.Core.ModAssets.Resolve(Config.DefaultConfigPath);
    public static string OldConfigPath => PrimaryPackage.Core.ModAssets.Resolve(Config.DefaultOldConfigPath);
    public static string PrefabsPath => PrimaryPackage.Core.ModAssets.Resolve(AdvancedDecalPrefab.DefaultPrefabsPath);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Character, System.Runtime.CompilerServices.StrongBox<double>> pulseOffsets = new();

    public static double PulseOffset(Character character) =>
      character == null ? 0.0 : pulseOffsets.GetValue(character, static _ => new System.Runtime.CompilerServices.StrongBox<double>(Rand.Range(0.0f, 1.0f))).Value;
    public static Harmony Harmony;

    public static VanillaDecalKiller VanillaDecalKiller = new();

    public static Config Config = new Config();

    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
      ContentPackage package = PrimaryPackage.Core.ModAssets.Package;
      if (package == null)
      {
        Log($"Couldn't find ContentPackage for {this}");
        return;
      }
      Package = package;

      Harmony = harmony;
      PatchAll();
      AddCommands();

      ConfigManager.Load();

      VanillaDecalKiller.HideVanillaBlood();
    }

    private const string UpdateDecalsHook = "MoreBlood.UpdateDecals";
    private const string ClearMixinsHook = "MoreBlood.clearmixins";

    public void PatchAll()
    {
      LuaCsSetup.Instance.Hook.Add("think", UpdateDecalsHook, (object[] args) =>
      {
        AdvancedDecal.UpdateAll();
        return null;
      });

      LuaCsSetup.Instance.Hook.Add("roundStart", ClearMixinsHook, (object[] args) =>
      {
        Mixins.Clear();
        return null;
      });

      HullPatches.PatchAll(Harmony);
      FromBleeding.PatchAll(Harmony);
      FromImpact.PatchAll(Harmony);
      TrackDamageSource.PatchAll(Harmony);
      Cleaning.PatchAll(Harmony);
    }

    public void DestroyStaticVars()
    {
      foreach (FieldInfo fi in typeof(Mod).GetFields(AccessTools.all))
      {
        if (fi.IsLiteral || !fi.IsStatic) { continue; }
        if (fi.FieldType.IsValueType || fi.IsInitOnly) { continue; }
        fi.SetValue(null, null);
      }
    }

    public void Release()
    {
      LuaCsSetup.Instance?.Hook?.Remove("think", UpdateDecalsHook);
      LuaCsSetup.Instance?.Hook?.Remove("roundStart", ClearMixinsHook);
      VanillaDecalKiller.RestoreVanillaBlood();
      DestroyStaticVars();
      Mixins.Clear();
    }
  }
}
