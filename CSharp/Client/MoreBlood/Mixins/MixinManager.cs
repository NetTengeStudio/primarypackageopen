using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Diagnostics;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace MoreBlood
{
  public static class Mixins
  {
    public static ConcurrentDictionary<ushort, HullMixin> HullMixins = new();
    public static HullMixin GetHullMixin(Hull hull)
    {
      return HullMixins.GetOrAdd(hull.ID, _ => new HullMixin(hull));
    }

    public static void Clear()
    {
      HullMixins.Clear();
    }
  }

}
