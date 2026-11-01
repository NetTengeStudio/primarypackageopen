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
namespace MoreBlood
{
  public struct ColorPoint
  {
    public static Color Lerp(List<ColorPoint> Colors, double lambda)
    {
      if (Colors is null) return Color.Transparent;
      if (Colors.Count == 0) return Color.Transparent;
      if (Colors.Count == 1) return Colors[0].Color;
      if (lambda <= Colors[0].Lambda) return Colors[0].Color;

      for (int i = 0; i < Colors.Count - 1; i++)
      {
        if (Colors[i].Lambda < lambda && lambda < Colors[i + 1].Lambda)
        {
          return Color.Lerp(
            Colors[i].Color,
            Colors[i + 1].Color,
            (float)((lambda - Colors[i].Lambda) / (Colors[i + 1].Lambda - Colors[i].Lambda))
          );
        }
      }

      return Colors.Last().Color;
    }

    public Color Color;
    public double Lambda;

    public ColorPoint(Color color, double l) => (Color, Lambda) = (color, l);

    public XElement ToXML()
    {
      return new XElement("ColorPoint",
        new XAttribute("Color", XMLExtensions.ColorToString(Color)),
        new XAttribute("Lambda", Lambda)
      );
    }

    public static ColorPoint FromXML(XElement element)
    {
      return new ColorPoint(
        XMLExtensions.ParseColor(element.Attribute("Color")?.Value ?? "0,0,0,0"),
        Parser.Parse<double>(element.Attribute("Lambda")?.Value)
      );
    }
  }

}
