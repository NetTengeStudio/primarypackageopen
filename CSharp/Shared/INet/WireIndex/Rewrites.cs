using System.Collections.Generic;
using HarmonyLib;

namespace INet;

public static class Rewrites {
    public const string ItemType = "Barotrauma.Item";
    public const string TerminalType = "Barotrauma.Items.Components.ButtonTerminal";
    public const string DistributorType = "Barotrauma.Items.Components.PowerDistributor";
    public const string HealthType = "Barotrauma.CharacterHealth";
    public const string LevelObjectsType = "Barotrauma.LevelObjectManager";
    public const string OrderType = "Barotrauma.OrderPrefab";

    public static IEnumerable<CodeInstruction> ItemComponents(IEnumerable<CodeInstruction> instructions) =>
        WidthRewriter.Keyed(instructions, ItemType, "components",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.WriteComponent)),
            AccessTools.Method(typeof(WireSites), nameof(WireSites.ReadComponent)));

    public static IEnumerable<CodeInstruction> TerminalSignals(IEnumerable<CodeInstruction> instructions) =>
        WidthRewriter.Keyed(instructions, TerminalType, "Signals",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.WriteSignal)),
            AccessTools.Method(typeof(WireSites), nameof(WireSites.ReadSignal)));

    public static IEnumerable<CodeInstruction> PowerGroups(IEnumerable<CodeInstruction> instructions) =>
        WidthRewriter.Keyed(instructions, DistributorType, "powerGroups",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.WritePowerGroup)),
            AccessTools.Method(typeof(WireSites), nameof(WireSites.ReadPowerGroup)));

    public static IEnumerable<CodeInstruction> LimbHealths(IEnumerable<CodeInstruction> instructions) =>
        WidthRewriter.Bounded(instructions, HealthType, "limbHealths");

    public static IEnumerable<CodeInstruction> LevelObjects(IEnumerable<CodeInstruction> instructions) =>
        WidthRewriter.Bounded(instructions, LevelObjectsType, "objects");

    public static IEnumerable<CodeInstruction> OrderOptions(IEnumerable<CodeInstruction> instructions) =>
        WidthRewriter.Bounded(instructions, OrderType, "AllOptions");

    public static IEnumerable<CodeInstruction> UpgradeTargets(IEnumerable<CodeInstruction> instructions) {
        var code = IlSwap.Call(instructions, "System.Linq.Enumerable", "ElementAt",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.UpgradeTargetAt)));
        code = IlSwap.Call(code, "Barotrauma.PropertyReference", "SetOriginalValue",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.SetIfPresent)));
        return IlSwap.Element(code, "Barotrauma.PropertyReference",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.PropertyAt)));
    }
}
