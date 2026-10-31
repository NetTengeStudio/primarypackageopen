using Barotrauma;

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
            __state.Locked = PreviewLock.ShowLocked(submarineInfo);
            PreviewLock.BeginCreate(__state.Locked);
            ResidentAsset.EvictPreviewed();

            if (__state.Locked)
            {
                if (!EngineSymbols.PreviewUiAvailable)
                {
                    PreviewLock.Release();
                    return false;
                }

                return true;
            }

            // Ordinary, unprotected previews must remain usable even if no
            // in-memory sealed payload needed rehydration.
            __state.Rehydrated = ResidentAsset.Rehydrate(submarineInfo);
            return true;
        }

        public static void AfterPreviewCreate(SubmarineInfo submarineInfo, PreviewLoadState __state)
        {
            PreviewLock.EndCreate();
            if (__state.Locked || !__state.Rehydrated) { return; }
            ResidentAsset.HoldPreviewed(submarineInfo);
        }

        public static void AfterPreviewClose()
        {
            if (!PreviewLock.IsCreating) { PreviewLock.Release(); }
            ResidentAsset.EvictPreviewed();
        }
    }
}
