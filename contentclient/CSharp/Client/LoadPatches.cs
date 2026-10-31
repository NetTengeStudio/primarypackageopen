using Barotrauma;
using System;

namespace SubSeal.Client
{
    internal struct PreviewLoadState
    {
        internal bool Rehydrated;
        internal bool Locked;
    }

    internal static class LoadPatches
    {
        public static void BeforeSubmarineConstructed(SubmarineInfo info) => ResidentAsset.Rehydrate(info);

        public static void AfterSubmarineConstructed(Submarine __instance) => ResidentAsset.Evict(__instance?.Info);

        public static bool BeforePreviewCreate(SubmarineInfo submarineInfo, ref PreviewLoadState __state)
        {
            if (submarineInfo is null) { __state = default; return false; }
            __state.Locked = PreviewLock.ShowLocked(submarineInfo);
            PreviewLock.BeginCreate(__state.Locked);
            ResidentAsset.EvictPreviewed();

            if (__state.Locked)
            {
                if (!PreviewLock.PreviewUiReady)
                {
                    PreviewLock.Release();
                    return false;
                }

                return true;
            }

            __state.Rehydrated = ResidentAsset.Rehydrate(submarineInfo);
            if (__state.Rehydrated) { return true; }
            PreviewLock.Release();
            return false;
        }

        public static void AfterPreviewCreate(SubmarineInfo submarineInfo, PreviewLoadState __state)
        {
            PreviewLock.EndCreate();
            if (__state.Locked || !__state.Rehydrated) { return; }
            ResidentAsset.HoldPreviewed(submarineInfo);
        }

        public static void AfterPreviewCreateFailure(Exception __exception)
        {
            if (__exception is null) { return; }
            PreviewLock.Release();
            ResidentAsset.EvictPreviewed();
        }

        public static void AfterPreviewClose()
        {
            if (!PreviewLock.IsCreating) { PreviewLock.Release(); }
            ResidentAsset.EvictPreviewed();
        }
    }
}
