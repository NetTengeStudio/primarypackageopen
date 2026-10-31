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
  public class FromImpactConfig : ConfigBase
  {
    public float BloodAmountFromImpact { get; set; } = 1.0f;
    public float LifetimeMultiplier { get; set; } = 2.0f;
    public float BleedingDamageToDecalSize { get; set; } = 1.3f;
    public float MinSplash { get; set; } = 0.1f;
    public float Cutoff { get; set; } = 0.2f;
    public ImpactConfig OfProjectile { get; set; } = new ImpactConfig()
    {
      MinBloodFlyDistance = 40.0f,
      BloodSpeed = 80.0f,
    };
    public ImpactConfig OfMeleeWeapon { get; set; } = new ImpactConfig()
    {
      MinBloodFlyDistance = 10.0f,
      BloodSpeed = 20.0f,
    };
  }

  public class ImpactConfig : ConfigBase
  {
    public float MinBloodFlyDistance { get; set; }
    public float BloodSpeed { get; set; }
  }
}
