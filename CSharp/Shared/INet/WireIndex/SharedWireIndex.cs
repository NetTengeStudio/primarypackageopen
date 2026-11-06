using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace INet;

public static class SharedWireIndex {
    public static void Attach(Harmony harmony, PatchSet patches) {
        patches.Patch(harmony,
            AccessTools.Method(typeof(CircuitBox), nameof(CircuitBox.FindCircuitBox), new[] { typeof(ushort), typeof(byte) }),
            prefix: ModuleHost.Bind(typeof(TypedOrdinal), nameof(TypedOrdinal.FindCircuitBox)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(ButtonTerminal), "Write", new[] { typeof(IWriteMessage), typeof(NetEntityEvent.IData) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.TerminalSignals)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(PowerDistributor), "SharedEventWrite", new[] { typeof(IWriteMessage), typeof(NetEntityEvent.IData) }),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.PowerGroups)));
        patches.Patch(harmony,
            AccessTools.Method(typeof(PowerDistributor), "SharedEventRead"),
            transpiler: ModuleHost.Bind(typeof(Rewrites), nameof(Rewrites.PowerGroups)));
    }
}
