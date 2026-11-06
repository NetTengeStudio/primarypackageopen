using System;
using System.IO;
using System.Xml.Linq;
using Barotrauma;

namespace IEngine;

static class Settings
{
    const string FileName = "Config/IEngine.xml";
    static string root = "";

    public static bool Takeover;
    public const string DefaultMode = "all";
    public static string Mode = DefaultMode;
    public static float SpatialRadius = 50, SpatialHysteresis = 10, SpatialNpcRadius = 8;

    static string PathOf() => System.IO.Path.Combine(root, FileName);

    public static void Load(string modRoot)
    {
        root = modRoot ?? "";
        if (Environment.GetEnvironmentVariable("IE_TAKEOVER") is { Length: > 0 } env) { Takeover = env == "1"; }
        try
        {
            string path = PathOf();
            if (!File.Exists(path)) { return; }
            XElement root = XDocument.Load(path).Root;
            if (root == null) { return; }
            if (Environment.GetEnvironmentVariable("IE_TAKEOVER") is not { Length: > 0 }) { Takeover = (bool?)root.Attribute("takeover") ?? Takeover; }
            Mode = (string)root.Attribute("mode") ?? Mode;
            SpatialRadius = (float?)root.Attribute("spatialRadius") ?? SpatialRadius;
            SpatialHysteresis = (float?)root.Attribute("spatialHysteresis") ?? SpatialHysteresis;
            SpatialNpcRadius = (float?)root.Attribute("spatialNpcRadius") ?? SpatialNpcRadius;
        }
        catch (Exception e)
        {
            DebugConsole.ThrowError("IEngine: settings unreadable", e);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("IE_MODE") is { Length: > 0 } mode) { Mode = mode; }
        }
    }

    public static void Save()
    {
        if (Environment.GetEnvironmentVariable("IE_OUT") is { Length: > 0 }) { return; }
        try { new XDocument(new XElement("IEngine", new XAttribute("takeover", Takeover), new XAttribute("mode", Mode),
            new XAttribute("spatialRadius", SpatialRadius), new XAttribute("spatialHysteresis", SpatialHysteresis), new XAttribute("spatialNpcRadius", SpatialNpcRadius))).Save(PathOf()); }
        catch (Exception e) { DebugConsole.ThrowError("IEngine: settings not saved", e); }
    }
}
