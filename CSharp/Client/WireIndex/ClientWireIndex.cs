using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public static class ClientWireIndex {
    public static void Attach(Harmony harmony, PatchSet patches) {
        patches.Patch(harmony,
            AccessTools.Method(typeof(ClientEntityEventManager), nameof(ClientEntityEventManager.Read),
                new[] { typeof(ServerNetSegment), typeof(IReadMessage), typeof(float) }),
            transpiler: ModuleHost.Bind(typeof(EventFrame), nameof(EventFrame.Mark)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) }),
            prefix: ModuleHost.Bind(typeof(ClientWireIndex), nameof(SkipAbsentComponent)),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.ItemComponents)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.UpgradeTargets)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.ClientEventWrite), new[] { typeof(IWriteMessage), typeof(NetEntityEvent.IData) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.ItemComponents)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.ReadSpawnData), new[] { typeof(IReadMessage), typeof(bool) }),
            transpiler: ModuleHost.Bind(typeof(TypedOrdinal), nameof(TypedOrdinal.SwapContainerList)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(CircuitBox), nameof(CircuitBox.SendMessage), new[] { typeof(CircuitBoxOpcode), typeof(INetSerializableStruct) }),
            transpiler: ModuleHost.Bind(typeof(TypedOrdinal), nameof(TypedOrdinal.SwapCircuitBoxIndex)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(ButtonTerminal), nameof(ButtonTerminal.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) }),
            prefix: ModuleHost.Bind(typeof(ClientWireIndex), nameof(SkipAbsentSignal)),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.TerminalSignals)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.ClientRead), new[] { typeof(IReadMessage) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.LimbHealths)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(LevelObjectManager), nameof(LevelObjectManager.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.LevelObjects)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(LevelObjectManager), nameof(LevelObjectManager.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) }),
            transpiler: ModuleHost.Bind(typeof(LevelObjectRead), nameof(LevelObjectRead.Safe)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(Character), nameof(Character.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.OrderOptions)));

        SharedWireIndex.Attach(harmony, patches);
    }

    public static bool SkipAbsentComponent(Item __instance, IReadMessage msg) =>
        WireGuard.ItemEventPresent(__instance, msg) || !EventFrame.SkipToEnd(msg);

    public static bool SkipAbsentSignal(ButtonTerminal __instance, IReadMessage msg) =>
        WireGuard.SignalPresent(__instance, msg) || !EventFrame.SkipToEnd(msg);
}
