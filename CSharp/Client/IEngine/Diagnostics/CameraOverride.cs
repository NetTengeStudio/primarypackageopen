using System.Globalization;
using Barotrauma;
using HarmonyLib;

namespace IEngine.Diagnostics;

static class CameraOverride
{
    static float zoom;

    public static void Install(Harmony harmony) =>
        harmony.Patch(AccessTools.Method(typeof(Camera), nameof(Camera.MoveCamera)), postfix: new HarmonyMethod(typeof(CameraOverride), nameof(Post)));

    static void Post(Camera __instance)
    {
        if (zoom > 0 && __instance == GameMain.GameScreen?.Cam) { __instance.Zoom = zoom; }
    }

    public static string Command(string[] args)
    {
        zoom = args.Length > 0 && float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ? z : 0;
        return zoom > 0 ? $"camera zoom forced to {zoom}" : "camera zoom released";
    }
}
