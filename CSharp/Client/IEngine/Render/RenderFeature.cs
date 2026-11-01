using System.Reflection;
using HarmonyLib;
using IEngine.Execution;
using Microsoft.Xna.Framework.Graphics;

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("MonoGame.Framework.Windows.NetStandard")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("MonoGame.Framework.Linux.NetStandard")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("MonoGame.Framework.MacOS.NetStandard")]

namespace IEngine.Render;

sealed class RenderFeature : Feature
{
    static RenderFeature instance;
    static SpriteSubmitter submitter;
    static MethodBase drawBatch, flush, end;
    static string conflict;
    static int recheckIn;

    public override string Name => "render";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        drawBatch = AccessTools.Method(typeof(SpriteBatcher), nameof(SpriteBatcher.DrawBatch));
        flush = AccessTools.Method(typeof(SpriteBatcher), "FlushVertexArray");
        end = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.End));
        if (drawBatch == null || flush == null || end == null) { return "SpriteBatch.End/SpriteBatcher.DrawBatch/FlushVertexArray not found in this MonoGame build"; }
        harmony.Patch(end, prefix: new HarmonyMethod(typeof(RenderFeature), nameof(EndPre)) { priority = Priority.Last });
        harmony.Patch(drawBatch, prefix: new HarmonyMethod(typeof(RenderFeature), nameof(DrawBatchPre)) { priority = Priority.Last });
        return null;
    }

    static bool Usable()
    {
        if (!instance.Active) { return false; }
        if (--recheckIn <= 0)
        {
            recheckIn = 4096;
            var a = PatchCensus.Of(drawBatch);
            var b = PatchCensus.Of(flush);
            var c = PatchCensus.Of(end);
            conflict = a.Any || b.Any || c.Any ? $"End {c} DrawBatch {a} FlushVertexArray {b}" : null;
        }
        return conflict == null;
    }

    static bool EndPre(SpriteBatch __instance, bool __runOriginal)
    {
        if (!__runOriginal || !__instance._beginCalled || !Usable()) { return true; }
        __instance._beginCalled = false;
        if (__instance._sortMode != SpriteSortMode.Immediate) { __instance.Setup(); }
        Ends++;
        Submit(__instance._batcher, __instance._sortMode, __instance._spritePass);
        return false;
    }

    static bool DrawBatchPre(SpriteBatcher __instance, SpriteSortMode sortMode, EffectPass defaultSpritePass, bool __runOriginal)
    {
        if (!__runOriginal || !Usable()) { return true; }
        Submit(__instance, sortMode, defaultSpritePass);
        return false;
    }

    static long Ends;

    static void Submit(SpriteBatcher batcher, SpriteSortMode sortMode, EffectPass pass)
    {
        if (batcher._batchItemCount == 0) { return; }
        GraphicsDevice device = batcher._device;
        if (submitter == null || submitter.Device != device || !submitter.Usable)
        {
            submitter?.Dispose();
            submitter = new SpriteSubmitter(device);
        }
        submitter.Submit(batcher, sortMode, pass);
    }

    protected override void OnToggled() => recheckIn = 0;

    internal override void Uninstall()
    {
        submitter?.Dispose();
        submitter = null;
    }

    public override string Report() =>
        (conflict != null ? $"yielding to foreign patches: {conflict} " : "") +
        (submitter == null ? "idle" : $"ends={Ends} batches={submitter.Batches} sprites={submitter.Sprites} draws={submitter.Draws} sorted={submitter.Sorted} discards={submitter.Discards}");
}
