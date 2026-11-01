using System.Diagnostics;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using IEngine.Core;
using Microsoft.Xna.Framework;

namespace IEngine.Diagnostics;

static partial class Probes
{
    static long lastFrameEnd;
    public static event System.Action FrameDrawn;

    static partial void InstallSide(Harmony harmony)
    {
        perFrame = true;
        Wrap(harmony, AccessTools.Method(typeof(GameClient), nameof(GameClient.Update)), nameof(NetworkPost));
        Wrap(harmony, AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Draw)), nameof(DrawPost));
        Wrap(harmony, AccessTools.Method(typeof(Microsoft.Xna.Framework.Graphics.GraphicsDevice), "PlatformPresent"), nameof(PresentPost));
        Wrap(harmony, AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }), nameof(FramePost));
        CameraOverride.Install(harmony);
    }

    static void PresentPost(long __state) => Recorder.Add(Phase.Present, __state);

    static void FramePost(long __state)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastFrameEnd != 0) { Recorder.AddTicks(Phase.Frame, now - lastFrameEnd); }
        lastFrameEnd = now;
        FrameDrawn?.Invoke();
        EndSample();
    }
}
