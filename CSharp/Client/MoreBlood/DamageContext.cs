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

  public enum DamageSource
  {
    Unknown,
    Projectile,
    Bite,
    Explosion,
    MeleeWeapon,
  }

  public class DamageContext
  {
    public static DamageContext Current;
    public static DamageContext Unknown = new DamageContext(DamageSource.Unknown);
    public DamageSource DamageSource;
    public DamageContext(DamageSource source)
      => DamageSource = source;

    public override string ToString() => DamageSource.ToString();
  }

  public class ProjectileDamageContext : DamageContext
  {
    public Item Item;
    public ProjectileDamageContext(Item item)
      : base(DamageSource.Projectile) => Item = item;
  }

  public class MeleeDamageContext : DamageContext
  {
    public Item Item;
    public MeleeDamageContext(Item item)
      : base(DamageSource.MeleeWeapon) => Item = item;
  }

}
