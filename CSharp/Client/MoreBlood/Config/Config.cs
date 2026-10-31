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
  public class Config : ConfigBase
  {
    public static string DebugConfigPath = "Debug Config.xml";
    public static string DefaultConfigPath = "Config/MoreBlood.xml";
    public static string DefaultOldConfigPath = "Data/MoreBlood_Legacy.xml";
    public BleedingConfig FromBleeding { get; set; } = new BleedingConfig();
    public FromImpactConfig FromImpact { get; set; } = new FromImpactConfig();
    public float DecalDrawDepth { get; set; } = 0.6f;
    public float GlobalBloodAmount { get; set; } = 2.0f;
    public float GlobalDecalLifetime { get; set; } = 1.0f;
    public float DecalCleaningSpeed { get; set; } = 0.1f;
    public float VitalityMultiplier { get; set; } = 0.9f;
    public string Version { get; set; } = "0.0.0";
  }

  public class ConfigBase
  {
    private IEnumerable<PropertyInfo> Props
    {
      get
      {
        foreach (PropertyInfo pi in this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
          yield return pi;
        }
        yield break;
      }
    }

    private XElement PackProps(XElement element)
    {
      List<PropertyInfo> props = new List<PropertyInfo>(this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance));

      foreach (PropertyInfo pi in Props)
      {
        if (pi.PropertyType.IsSubclassOf(typeof(ConfigBase)))
        {
          XElement prop = new XElement(pi.Name);
          (pi.GetValue(this) as ConfigBase).PackProps(prop);
          element.Add(prop);
        }
      }

      foreach (PropertyInfo pi in Props)
      {
        if (!pi.PropertyType.IsSubclassOf(typeof(ConfigBase)))
        {
          element.Add(new XElement(pi.Name, pi.GetValue(this).ToString()));
        }
      }

      return element;
    }

    public XElement ToXML()
    {
      return PackProps(new XElement(this.GetType().Name));
    }

    public void Save(string path)
    {
      XDocument xdoc = new XDocument();
      xdoc.Add(this.ToXML());
      xdoc.Save(path);
    }

    public void FromXML(XElement element)
    {
      foreach (XElement child in element.Elements())
      {
        PropertyInfo pi = this.GetType().GetProperty(child.Name.ToString());
        if (pi is null) continue;

        if (pi.PropertyType.IsSubclassOf(typeof(ConfigBase)))
        {
          ConfigBase subConfig = (ConfigBase)pi.GetValue(this);
          if (subConfig is null)
          {
            subConfig = (ConfigBase)Activator.CreateInstance(pi.PropertyType);
            pi.SetValue(this, subConfig);
          }

          subConfig.FromXML(child);
        }
        else
        {
          pi.SetValue(this, Parser.Parse(child.Value, pi.PropertyType));
        }
      }
    }

    public bool Load(string path)
    {
      if (!File.Exists(path))
      {
        return false;
      }
      XDocument xdoc = XDocument.Load(path);
      this.FromXML(xdoc.Root);
      return true;
    }

    public void Print()
    {
      Mod.Log(this.ToXML());
    }

  }
}
