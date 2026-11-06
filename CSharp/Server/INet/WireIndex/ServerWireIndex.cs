using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace INet;

public static class ServerWireIndex {
    public static void Attach(Harmony harmony, PatchSet patches) {
        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.ServerEventWrite), new[] { typeof(IWriteMessage), typeof(Client), typeof(NetEntityEvent.IData) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.ItemComponents)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.ServerEventRead), new[] { typeof(IReadMessage), typeof(Client) }),
            prefix: ModuleHost.Bind(typeof(ServerWireIndex), nameof(SkipAbsentComponent)),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.ItemComponents)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(Item), nameof(Item.WriteSpawnData), new[] { typeof(IWriteMessage), typeof(ushort), typeof(ushort), typeof(byte), typeof(int) }),
            prefix: ModuleHost.Bind(typeof(ServerWireIndex), nameof(AddressSpawnContainer)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(CircuitBox), "PrepareToSend", new[] { typeof(CircuitBoxOpcode), typeof(INetSerializableStruct) }),
            transpiler: ModuleHost.Bind(typeof(TypedOrdinal), nameof(TypedOrdinal.SwapCircuitBoxIndex)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(ButtonTerminal), nameof(ButtonTerminal.ServerEventRead), new[] { typeof(IReadMessage), typeof(Client) }),
            prefix: ModuleHost.Bind(typeof(ServerWireIndex), nameof(SkipAbsentSignal)),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.TerminalSignals)));

        patches.Patch(harmony,
            AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.ServerWrite), new[] { typeof(IWriteMessage) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.LimbHealths)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(LevelObjectManager), nameof(LevelObjectManager.ServerEventWrite), new[] { typeof(IWriteMessage), typeof(Client), typeof(NetEntityEvent.IData) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.LevelObjects)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(Character), nameof(Character.ServerEventWrite), new[] { typeof(IWriteMessage), typeof(Client), typeof(NetEntityEvent.IData) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.OrderOptions)));

        SharedWireIndex.Attach(harmony, patches);
    }

    public static bool SkipAbsentComponent(Item __instance, IReadMessage msg) => WireGuard.ItemEventPresent(__instance, msg);

    public static bool SkipAbsentSignal(ButtonTerminal __instance, IReadMessage msg) => WireGuard.SignalPresent(__instance, msg);

    public static void AddressSpawnContainer(ushort originalInventoryID, ref byte originalItemContainerIndex) {
        if (originalInventoryID == Entity.NullEntityID || Entity.FindEntityByID(originalInventoryID) is not Item parent) { return; }
        var components = parent.Components;
        if (originalItemContainerIndex >= components.Count) { return; }
        int ordinal = TypedOrdinal.OrdinalOf<ItemContainer>(components, components[originalItemContainerIndex]);
        originalItemContainerIndex = ordinal < 0 ? byte.MaxValue : (byte)ordinal;
    }
}
