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
  public static class Utils
  {
    public static float HumanVitality = 100.0f;
    public static Random Random = new Random();
    public static float Pi2 = (float)(Math.PI * 2.0);
    public static float Lambda(float start, float end, float value) => (value - start) / (end - start);

    public static float ExpRandom(float Start, float End, float Exponent)
    {
      double rand = Utils.Random.NextDouble();
      rand = Math.Pow(rand, Exponent);
      return (float)(Start + rand * (End - Start));
    }
    public static float ExpSegment(Vector2 Start, Vector2 End, float Exponent, float x)
    {
      if (x < Start.X) return Start.Y;
      if (x > End.X) return End.Y;
      return (float)(Start.Y + Math.Pow((x - Start.X) / (End.X - Start.X), Exponent) * (End.Y - Start.Y));
    }
  }
}
