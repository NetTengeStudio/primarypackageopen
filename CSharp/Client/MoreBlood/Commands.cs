using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.IO;

using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using HarmonyLib;

namespace MoreBlood
{
  public partial class Mod
  {
    public static void AddCommands()
    {
      PrimaryPackage.Core.ModCommands.Register("reloadbloodprefabs", "reloadbloodprefabs: reload blood decal prefabs",
        ReloadBloodPrefabs_Command, localOnClient: true, relayToServer: false);
      PrimaryPackage.Core.ModCommands.Register("clearblood", "clearblood: remove every blood decal in the level",
        ClearBlood_Command, localOnClient: true, relayToServer: false);
    }

    public static void ReloadBloodPrefabs_Command(string[] args)
    {
      AdvancedDecalPrefab.LoadPrefabs(PrefabsPath);
      AdvancedDecalPrefab.SavePrefabs(PrefabsPath);
    }

    public static void ClearBlood_Command(string[] args)
    {
      Mixins.Clear();
      AdvancedDecal.Decals.Clear();
    }

  }
}
