using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.SubEditor;

public static class SubMeta
{
    private static XElement TryParse(string v)
    {
        if (string.IsNullOrEmpty(v)) { return null; }
        try { return XElement.Parse(v); }
        catch { return null; }
    }

    private static readonly (string Key, Func<SubmarineInfo, string> Get, Action<SubmarineInfo, string> Set)[] Fields =
    {
        ("beacon", i => SaveExtra(i.BeaconStationInfo), (i, v) => { var x = TryParse(v); i.BeaconStationInfo = x == null ? null : new BeaconStationInfo(i, x); }),
        ("class", i => i.SubmarineClass.ToString(), (i, v) => { if (Enum.TryParse(v, out SubmarineClass c)) { i.SubmarineClass = c; } }),
        ("crewexp", i => i.RecommendedCrewExperience.ToString(), (i, v) => { if (Enum.TryParse(v, out SubmarineInfo.CrewExperienceLevel c)) { i.RecommendedCrewExperience = c; } }),
        ("crewmax", i => Int(i.RecommendedCrewSizeMax), (i, v) => { if (TryInt(v, out int n)) { i.RecommendedCrewSizeMax = n; } }),
        ("crewmin", i => Int(i.RecommendedCrewSizeMin), (i, v) => { if (TryInt(v, out int n)) { i.RecommendedCrewSizeMin = n; } }),
        ("description", i => i.Description?.Value ?? "", (i, v) => i.Description = new RawLString(v)),
        ("enemy", i => SaveExtra(i.EnemySubmarineInfo), (i, v) => { var x = TryParse(v); i.EnemySubmarineInfo = x == null ? null : new EnemySubmarineInfo(i, x); }),
        ("hidden", i => List(i.LayersHiddenByDefault.Select(l => l.Value)), (i, v) => i.LayersHiddenByDefault = new HashSet<Identifier>(Split(v).Select(s => s.ToIdentifier()))),
        ("module", i => SaveModule(i.OutpostModuleInfo), (i, v) => { var x = TryParse(v); i.OutpostModuleInfo = x == null ? null : new OutpostModuleInfo(i, x); }),
        ("name", i => i.Name ?? "", (i, v) => { i.Name = v; i.DisplayName = new RawLString(v); }),
        ("outfitted", i => i.IsManuallyOutfitted ? "1" : "0", (i, v) => i.IsManuallyOutfitted = v == "1"),
        ("outposttags", i => List(i.OutpostTags.Select(t => t.Value)), (i, v) => i.OutpostTags = IdentifierSet(v)),
        ("packages", i => List(i.RequiredContentPackages), (i, v) => { i.RequiredContentPackages.Clear(); foreach (string s in Split(v)) { i.RequiredContentPackages.Add(s); } }),
        ("price", i => Int(i.Price), (i, v) => { if (TryInt(v, out int n)) { i.Price = n; } }),
        ("tags", i => Int((int)i.Tags), (i, v) => { if (TryInt(v, out int n)) { ApplyTags(i, (SubmarineTag)n); } }),
        ("tier", i => Int(i.Tier), (i, v) => { if (TryInt(v, out int n)) { i.Tier = n; } }),
        ("triggerevents", i => List(i.TriggerOutpostMissionEvents.Select(t => t.Value)), (i, v) => i.TriggerOutpostMissionEvents = IdentifierSet(v)),
        ("type", i => i.Type.ToString(), (i, v) => { if (Enum.TryParse(v, out SubmarineType t)) { i.Type = t; } }),
        ("wreck", i => SaveExtra(i.WreckInfo), (i, v) => { var x = TryParse(v); i.WreckInfo = x == null ? null : new WreckInfo(i, x); }),
    };

    private static string SaveExtra(ExtraSubmarineInfo info)
    {
        if (info == null) { return ""; }
        var element = new XElement("Metadata");
        info.Save(element);
        return element.ToString(SaveOptions.DisableFormatting);
    }

    private static string SaveModule(OutpostModuleInfo info)
    {
        if (info == null) { return ""; }
        var element = new XElement("Metadata");
        info.Save(element);
        return element.ToString(SaveOptions.DisableFormatting);
    }

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    private static string List(IEnumerable<string> values) => string.Join(';', values.OrderBy(s => s, StringComparer.Ordinal));
    private static IEnumerable<string> Split(string v) => v.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static System.Collections.Immutable.ImmutableHashSet<Identifier> IdentifierSet(string v) =>
        System.Collections.Immutable.ImmutableHashSet.CreateRange(Split(v).Select(s => s.ToIdentifier()));

    private static void ApplyTags(SubmarineInfo info, SubmarineTag wanted)
    {
        foreach (SubmarineTag tag in Enum.GetValues<SubmarineTag>())
        {
            if (wanted.HasFlag(tag)) { info.AddTag(tag); } else { info.RemoveTag(tag); }
        }
    }

    public static MetaPayload.Pair[] Capture(SubmarineInfo info)
    {
        var pairs = new MetaPayload.Pair[Fields.Length];
        for (int i = 0; i < pairs.Length; i++) { pairs[i] = new MetaPayload.Pair(Fields[i].Key, Fields[i].Get(info)); }
        return pairs;
    }

    public static MetaPayload.Pair[] CaptureKeys(SubmarineInfo info, ReadOnlySpan<MetaPayload.Pair> keys)
    {
        var result = new List<MetaPayload.Pair>(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            foreach (var f in Fields) { if (f.Key == keys[i].Key) { result.Add(new MetaPayload.Pair(f.Key, f.Get(info))); break; } }
        }
        return result.ToArray();
    }

    public static MetaPayload.Pair[] CaptureKeysFrom(ReadOnlySpan<MetaPayload.Pair> values, ReadOnlySpan<MetaPayload.Pair> keys)
    {
        var result = new MetaPayload.Pair[keys.Length];
        int at = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            while (at < values.Length && string.CompareOrdinal(values[at].Key, keys[i].Key) < 0) { at++; }
            if (at >= values.Length || values[at].Key != keys[i].Key) { throw new ArgumentException("Unknown metadata key", nameof(keys)); }
            result[i] = values[at];
        }
        return result.ToArray();
    }

    public static bool KnowsAll(ReadOnlySpan<MetaPayload.Pair> pairs)
    {
        for (int i = 0; i < pairs.Length; i++)
        {
            string key = pairs[i].Key;
            if (!Fields.Any(f => f.Key == key)) { return false; }
        }
        return true;
    }

    public static void Apply(SubmarineInfo info, ReadOnlySpan<MetaPayload.Pair> pairs)
    {
        for (int i = 0; i < pairs.Length; i++)
        {
            foreach (var f in Fields) { if (f.Key == pairs[i].Key) { f.Set(info, pairs[i].Value); break; } }
        }
    }

    public static MetaPayload.Pair[] Diff(MetaPayload.Pair[] before, MetaPayload.Pair[] after)
    {
        var changed = new List<MetaPayload.Pair>();
        for (int i = 0; i < after.Length; i++)
        {
            if (i >= before.Length || before[i].Key != after[i].Key || before[i].Value != after[i].Value) { changed.Add(after[i]); }
        }
        return changed.ToArray();
    }
}
