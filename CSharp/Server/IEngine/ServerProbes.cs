using Barotrauma.Networking;
using HarmonyLib;

namespace IEngine.Diagnostics;

static partial class Probes
{
    static partial void InstallSide(Harmony harmony) =>
        Wrap(harmony, AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)), nameof(NetworkPost));
}
