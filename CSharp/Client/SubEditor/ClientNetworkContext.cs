using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.SubEditor;

/// <summary>The live editor still belongs to the running round. Keep native SyncIds,
/// chat, camera and entity acknowledgements on the ingame update path while it is open.</summary>
internal static class ClientNetworkContext
{
    private static Screen overriddenScreen;

    public static void Attach(Harmony harmony)
    {
        MethodInfo updateMethod = AccessTools.Method(typeof(GameClient), nameof(GameClient.Update), new[] { typeof(float) });
        if (updateMethod == null) { return; }
        try
        {
            harmony.Patch(updateMethod, transpiler: new HarmonyMethod(typeof(ClientNetworkContext), nameof(RoundUpdate)));
        }
        catch
        {
            harmony.Patch(updateMethod,
                prefix: new HarmonyMethod(typeof(ClientNetworkContext), nameof(BeforeUpdate)),
                finalizer: new HarmonyMethod(typeof(ClientNetworkContext), nameof(AfterUpdate)));
        }
    }

    private static void BeforeUpdate()
    {
        if (Screen.Selected is LiveEditorScreen { IsOpen: true })
        {
            overriddenScreen = Screen.Selected;
            Screen.Selected = GameMain.GameScreen;
        }
        else
        {
            overriddenScreen = null;
        }
    }

    private static void AfterUpdate()
    {
        if (overriddenScreen != null)
        {
            Screen.Selected = overriddenScreen;
            overriddenScreen = null;
        }
    }

    private static Screen RoundScreen() => Screen.Selected is LiveEditorScreen { IsOpen: true } ? GameMain.GameScreen : Screen.Selected;

    private static IEnumerable<CodeInstruction> RoundUpdate(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo selected = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.Selected));
        MethodInfo replacement = AccessTools.Method(typeof(ClientNetworkContext), nameof(RoundScreen));
        int replaced = 0;
        for (int i = 0; i + 1 < code.Count; i++)
        {
            if (!code[i].Calls(selected)) { continue; }
            object next = code[i + 1].operand;
            if (!(next is FieldInfo field && field.DeclaringType == typeof(GameMain) && field.Name == nameof(GameMain.GameScreen)) &&
                !(next is MethodInfo getter && getter.DeclaringType == typeof(GameMain) && getter.Name == "get_" + nameof(GameMain.GameScreen))) { continue; }
            code[i].operand = replacement;
            replaced++;
        }
        if (replaced == 0)
        {
            throw new InvalidOperationException("No round screen call sites found for transpiler");
        }
        return code;
    }
}
