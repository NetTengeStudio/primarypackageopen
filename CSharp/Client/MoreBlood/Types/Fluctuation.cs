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
  public struct Fluctuation
  {
    public static Fluctuation One => new Fluctuation(0, 1, 1);
    public float Min { get; set; } = 0.0f;
    public float Max { get; set; } = 1.0f;
    public float Exponent { get; set; } = 1.0f;

    public float Next()
    {
      double rand = Utils.Random.NextDouble();
      rand = Math.Pow(rand, Exponent);
      return (float)(Min + rand * (Max - Min));
    }

    public static Fluctuation Parse(string raw)
    {
      try
      {
        string content = raw.Split('[', ']')[1];
        if (content.Trim() == "") return Fluctuation.One;
        var pairs = content.Split(',').Select(s => s.Split(':').Select(sub => sub.Trim()).ToArray());

        float min = 0.0f;
        float max = 1.0f;
        float exp = 1.0f;

        foreach (var pair in pairs)
        {
          if (string.Equals(pair[0], "min", StringComparison.OrdinalIgnoreCase))
          {
            float.TryParse(pair[1], out min);
            continue;
          }

          if (string.Equals(pair[0], "max", StringComparison.OrdinalIgnoreCase))
          {
            float.TryParse(pair[1], out max);
            continue;
          }

          if (string.Equals(pair[0], "exp", StringComparison.OrdinalIgnoreCase))
          {
            float.TryParse(pair[1], out exp);
            continue;
          }
        }

        return new Fluctuation(min, max, exp);
      }
      catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("Fluctuation.Parse", noted); return Fluctuation.One; }

    }

    public Fluctuation(float min, float max, float exp) => (Min, Max, Exponent) = (min, max, exp);

    public override string ToString() => $"[min:{Min}, max:{Max}, exp:{Exponent}]";
  }
}
