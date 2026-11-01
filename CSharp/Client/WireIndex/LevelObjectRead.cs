using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.Netcode;

public static class LevelObjectRead {
    public static void Into(LevelObject target, IReadMessage msg) => target?.ClientRead(msg);

    public static IEnumerable<CodeInstruction> Safe(IEnumerable<CodeInstruction> instructions) {
        var code = IlSwap.ListItem(instructions, "Barotrauma.LevelObject",
            AccessTools.Method(typeof(WireSites), nameof(WireSites.LevelObjectAt)));
        return IlSwap.Call(code, "Barotrauma.LevelObject", "ClientRead",
            AccessTools.Method(typeof(LevelObjectRead), nameof(Into)));
    }
}
