using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Xml;
using System.Xml.Linq;
using System.IO;
namespace MoreBlood
{
  public static class ConfigManager
  {
    public static Config CurrentConfig => Mod.Config;

    public static void Load()
    {
      try
      {
        if (CurrentConfig.Load(Mod.ConfigPath))
        {
          if (String.Compare(CurrentConfig.Version, Mod.Package.ModVersion) < 0)
          {
            Mod.Warning($"Blood confing is outdated, moving it to {Config.DefaultOldConfigPath}");

            File.Delete(Mod.OldConfigPath);
            File.Move(Mod.ConfigPath, Mod.OldConfigPath);
          }
        }

        CurrentConfig.Version = Mod.Package.ModVersion;

        AdvancedDecalPrefab.LoadPrefabs(Mod.PrefabsPath);

        Save();
      }
      catch (Exception e) { Mod.Warning(e); }
    }

    public static void Save()
    {
      try
      {
        if (Mod.ConfigPath != null)
        {
          CurrentConfig.Save(Mod.ConfigPath);
          AdvancedDecalPrefab.SavePrefabs(Mod.PrefabsPath);
        }
      }
      catch (Exception e)
      {
        Mod.Warning($"config does not save: {e.Message}");
      }
    }

  }

}
