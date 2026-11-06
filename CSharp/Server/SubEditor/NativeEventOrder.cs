using System;
using System.Collections.Generic;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.SubEditor;

/// <summary>Merge the native event stream with editor revisions separately for each peer.
/// Events through a revision's pre-mutation cut remain deliverable; later events wait for application, not receipt, of that revision.</summary>
internal static class NativeEventOrder
{
    public static void Attach(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(ServerEntityEventManager), "GetEventsToSync", new[] { typeof(Client) }) ??
            throw new MissingMethodException(typeof(ServerEntityEventManager).FullName, "GetEventsToSync"),
        postfix: new HarmonyMethod(typeof(NativeEventOrder), nameof(Filter)));

    private static void Filter(Client client, List<NetEntityEvent> __result) => SubEditorServer.FilterNativeEvents(client, __result);
}
