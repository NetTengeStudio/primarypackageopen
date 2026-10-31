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
  public class AdvancedDecalPrefab
  {
    public static string DefaultPrefabsPath = "Content/Effects/Blood/Prefabs";
    public static string DefaultBasePrefab = "blood";
    public static AdvancedDecalPrefab Backup => Prefabs[DefaultBasePrefab];
    public static Dictionary<string, AdvancedDecalPrefab> Prefabs = new();

    public static AdvancedDecalPrefab GetPrefab(string name)
    {
      if (Prefabs.ContainsKey(name)) return Prefabs[name];
      return Backup;
    }

    private string basedOn; public string BasedOn
    {
      get => basedOn;
      set
      {
        basedOn = value;
        if (DecalManager.Prefabs.ContainsKey(value))
        {
          Sprites = DecalManager.Prefabs[value].Sprites;
        }
        else
        {
          Mod.Warning($"Couldn't steal sprites from vanilla decal prefab [{value}], using default blood sprites");
          Sprites = DecalManager.Prefabs[DefaultBasePrefab].Sprites;
        }
      }
    }
    public List<ColorPoint> Colors = new();
    public List<Sprite> Sprites;

    public Fluctuation SizeFluctuation { get; set; } = new Fluctuation(0.7f, 1.3f, 1.0f);
    public Fluctuation LifetimeFluctuation { get; set; } = new Fluctuation(1.0f, 4.0f, 4.0f);

    public float LifetimeExponent { get; set; } = 2.0f;
    public float MinSpriteSize { get; set; } = 0.3f;
    public float MaxSpriteSize { get; set; } = 1.8f;

    public float MinSize { get; set; } = 0.0f;
    public float MaxSize { get; set; } = 3.0f;
    public float MinLifetime { get; set; } = 2.0f;
    public float MaxLifetime { get; set; } = 30.0f;

    public Vector2 SLStart;
    public Vector2 SLEnd;

    public static void SavePrefabs(string path)
    {
      if (!Directory.Exists(path))
      {
        Mod.Warning($"Guh? {path}");
        return;
      }

      foreach (string key in Prefabs.Keys)
      {
        Prefabs[key].Save(Path.Combine(path, key + ".xml"));
      }
    }

    public static void LoadPrefabs(string path)
    {
      if (!Directory.Exists(path))
      {
        Mod.Warning($"Guh? {path}");
        return;
      }

      Prefabs.Clear();
      foreach (string file in Directory.GetFiles(path, "*.xml"))
      {
        Prefabs[Path.GetFileNameWithoutExtension(file)] = AdvancedDecalPrefab.Load(file);
      }
    }

    public void Save(string path)
    {
      XDocument xdoc = new XDocument();
      xdoc.Add(this.ToXML());
      xdoc.Save(path);
    }

    public static AdvancedDecalPrefab Load(string path)
    {
      if (!File.Exists(path))
      {
        Mod.Warning($"Couldn't load AdvancedDecalPrefab from {path}");
        return Backup;
      }
      XDocument xdoc = XDocument.Load(path);
      return FromXML(xdoc.Root);
    }
    public XElement ToXML()
    {
      XElement element = new XElement("AdvancedDecalPrefab");

      foreach (PropertyInfo pi in typeof(AdvancedDecalPrefab).GetProperties(BindingFlags.Instance | BindingFlags.Public))
      {
        element.Add(new XAttribute(pi.Name, Parser.Serialize(pi.GetValue(this))));
      }

      foreach (ColorPoint cp in Colors)
      {
        element.Add(cp.ToXML());
      }

      return element;
    }

    public static AdvancedDecalPrefab FromXML(XElement element) => new AdvancedDecalPrefab(element);

    public AdvancedDecalPrefab(XElement element)
    {
      foreach (XAttribute attribute in element.Attributes())
      {
        PropertyInfo pi = typeof(AdvancedDecalPrefab).GetProperty(attribute.Name.ToString());
        pi?.SetValue(this, Parser.Parse(attribute.Value, pi.PropertyType));
      }

      this.BasedOn ??= DefaultBasePrefab;

      foreach (XElement cp in element.Elements("ColorPoint"))
      {
        this.Colors.Add(ColorPoint.FromXML(cp));
      }

      SLStart = new Vector2(MinSize, MinLifetime);
      SLEnd = new Vector2(MaxSize, MaxLifetime);
    }

    public static void PrintPrefabs()
    {
      foreach (string key in Prefabs.Keys)
      {
        Mod.Log($"Prefabs[{key}] = {Prefabs[key].ToXML()}");
      }
    }

    public override string ToString() => ToXML().ToString();
  }
}
