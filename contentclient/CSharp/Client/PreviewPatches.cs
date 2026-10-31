using System.Threading.Tasks;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SubSeal.Client
{
    internal static class PreviewPatches
    {
        public static void BeforeCreatePreviewWindow(GUIComponent parent, ref int __state)
            => __state = parent is null ? -1 : parent.CountChildren;

        public static void AfterPreviewDisposed()
        {
            if (PreviewLock.IsCreating) { return; }
            PreviewLock.Release();
            ResidentAsset.EvictPreviewed();
        }

        public static bool BeforeCampaignRetry() => !PreviewLock.ConfirmedProtected;

        public static void AfterCreatePreviewWindow(SubmarineInfo __instance,
            GUIComponent parent, int __state)
        {
            if (__instance is null || parent is null || __state < 0) { return; }
            GUIComponent content = parent.GetChild(__state);
            GUIComponent button = content?.GetChild(0);
            PreviewSurfacePatches.Track(button);
        }

        public static bool BeforeGeneratePreviewMeshes(
            SubmarinePreview __instance, ref Task __result)
        {
            if (!PreviewLock.IsHeldLocked()) { return true; }
            EngineSymbols.SetPreviewBounds(__instance);
            __result = Task.CompletedTask;
            return false;
        }

        public static bool BeforeRenderSubmarine(
            SpriteBatch spriteBatch, Rectangle scissorRectangle)
        {
            if (!PreviewLock.IsHeldLocked()) { return true; }
            PreviewLock.Draw(spriteBatch, scissorRectangle);
            return false;
        }
    }
}
