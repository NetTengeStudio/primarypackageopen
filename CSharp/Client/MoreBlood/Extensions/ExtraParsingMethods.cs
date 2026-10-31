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
  public static class ExtraParsingMethods
  {
    public static Dictionary<Type, MethodInfo> Parse = new()
    {
      [typeof(Vector2)] = typeof(ExtraParsingMethods).GetMethod("ParseVector2"),
    };
    public static Dictionary<Type, MethodInfo> CustomToString = new()
    {
      [typeof(Vector2)] = typeof(ExtraParsingMethods).GetMethod("Vector2ToString"),
    };

    public static string Vector2ToString(Vector2 v) => $"[{v.X},{v.Y}]";
    public static Vector2 ParseVector2(string raw)
    {
      if (raw == null || raw == "") return new Vector2(0, 0);

      string content = raw.Split('[', ']')[1];

      List<string> coords = content.Split(',').Select(s => s.Trim()).ToList();

      float x = 0;
      float y = 0;

      float.TryParse(coords.ElementAtOrDefault(0), out x);
      float.TryParse(coords.ElementAtOrDefault(1), out y);

      return new Vector2(x, y);
    }
  }
}
