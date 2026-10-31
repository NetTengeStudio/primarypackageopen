using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Barotrauma;
using Barotrauma.Items.Components;

namespace IEngine.Execution;

static class PerformanceFixBridge
{
    const string WorkshopId = "2701251094";
#if SERVER
    const bool Server = true;
#else
    const bool Server = false;
#endif

    static readonly (string Event, string Id)[] itemHooks =
    {
        ("item.created", "addToPriority"),
        ("item.equip", "highPriorityHands"),
        ("think", "signalUpdatePerformanceFix"),
        ("roundStart", "initRoundStart")
    };

    static readonly HashSet<Identifier> priorityPrefabs = new();
    static readonly HashSet<string> priorityComponents = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<Identifier> priorityCharacters = new();
    static readonly List<Item> signalItems = new();
    static bool hands = true, shadowLights, behindSubLights, hideWires, hideLogic, permanentInSingleplayer;
    static readonly Identifier highPriorityTag = "highpriority".ToIdentifier();

    public static bool Present { get; private set; }
    public static string Source { get; private set; } = "absent";
    public static int HooksRemoved { get; private set; }

    public static void Detect()
    {
        Present = false;
        Source = "absent";
        foreach (ContentPackage p in ContentPackageManager.EnabledPackages.All)
        {
            bool match = p.Name == "Performance Fix" || (p.Dir ?? "").Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimEnd('/').EndsWith("/" + WorkshopId);
            if (!match) { continue; }
            Present = true;
            Source = p.Dir;
            LoadConfig(Path.Combine(p.Dir, "config.json"));
            return;
        }
    }

    static void LoadConfig(string path)
    {
        priorityPrefabs.Clear();
        priorityComponents.Clear();
        priorityCharacters.Clear();
        string side = Server ? "server" : "client";
        string[] prefabs = Server ? new[] { "battery", "delaycomponent", "acidmistemitter" }
            : new[] { "door", "doorwbuttons", "windoweddoorwbuttons", "windoweddoor", "hatchwbuttons", "sonartransducer", "divingsuit", "combatdivingsuit", "abyssdivingsuit", "pucs", "slipsuit", "battery", "delaycomponent", "acidmistemitter" };
        string[] components = { "Engine", "Pump", "Sonar", "Fabricator", "Deconstructor", "Reactor", "Turret", "Controller" };
        string[] characters = { "Human" };
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;
                prefabs = Strings(root, $"{side}ItemHighPriority") ?? prefabs;
                components = Strings(root, $"{side}ComponentPriority") ?? components;
                characters = Strings(root, "highPriorityCharacters") ?? characters;
                hands = Bool(root, $"{side}HighPriorityHands") ?? hands;
                shadowLights = Bool(root, "disableShadowCastingLights") ?? false;
                behindSubLights = Bool(root, "disableDrawBehindSubsLights") ?? false;
                hideWires = Bool(root, "hideInGameWires") ?? false;
                hideLogic = Bool(root, "hideInGameComponents") ?? false;
                permanentInSingleplayer = Bool(root, "allowSingleplayerPermanentConfigs") ?? false;
            }
        }
        catch (Exception e) { DebugConsole.ThrowError("IEngine: Performance Fix config unreadable, using its defaults", e); }
        foreach (string s in prefabs) { priorityPrefabs.Add(s.ToIdentifier()); }
        foreach (string s in components) { priorityComponents.Add(s); }
        foreach (string s in characters) { priorityCharacters.Add(s.ToIdentifier()); }
    }

    static string[] Strings(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.Array) { return null; }
        var list = new List<string>();
        foreach (JsonElement v in e.EnumerateArray()) { if (v.ValueKind == JsonValueKind.String) { list.Add(v.GetString()); } }
        return list.ToArray();
    }

    static bool? Bool(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;

    public static void Neutralize()
    {
        if (!Present || LuaCsSetup.Instance?.Hook is not { } hook) { return; }
        foreach (var (ev, id) in itemHooks)
        {
            try
            {
                hook.Remove(ev, id);
                HooksRemoved++;
            }
            catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("PerformanceFixBridge.1", noted); }
        }
        LuaCsSetup.Instance.Game.UpdatePriorityItems.Clear();
    }

    public static void RoundStarted()
    {
        if (!Present) { return; }
        Neutralize();
        signalItems.Clear();
        foreach (Character c in Character.CharacterList)
        {
            if (priorityCharacters.Contains(c.SpeciesName)) { LuaCsSetup.Instance.Game.AddPriorityCharacter(c); }
        }
#if CLIENT
        if (permanentInSingleplayer && GameMain.IsSingleplayer) { return; }
        foreach (Item item in Item.ItemList)
        {
            if (item.Tags.Contains("performancefix")) { signalItems.Add(item); }
            if (item.GetComponent<LightComponent>() is { } light)
            {
                if (shadowLights) { light.CastShadows = false; }
                if (behindSubLights) { light.DrawBehindSubs = false; }
            }
            if (hideWires && item.GetComponent<Wire>() is { } wire && wire.Connections.Length != 0) { item.HiddenInGame = true; }
            if (hideLogic && item.HasTag("logic".ToIdentifier())) { item.HiddenInGame = true; }
        }
#endif
    }

    public static void Think()
    {
        if (signalItems.Count == 0) { return; }
        string value = MapEntity.MapEntityUpdateInterval.ToString();
        for (int i = signalItems.Count - 1; i >= 0; i--)
        {
            Item item = signalItems[i];
            if (item.Removed) { signalItems.RemoveAt(i); continue; }
            item.SendSignal(value, "signal_out");
        }
    }

    public static bool IsStaticPriority(Item item)
    {
        if (item.HasTag(highPriorityTag) || priorityPrefabs.Contains(item.Prefab.Identifier)) { return true; }
        foreach (ItemComponent c in item.Components) { if (priorityComponents.Contains(c.Name)) { return true; } }
        return false;
    }

    public static bool Hands => hands;
}
