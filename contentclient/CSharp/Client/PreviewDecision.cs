namespace SubSeal.Client
{
    // Pure decision state: all filesystem, cryptographic and UI work is kept
    // in PreviewLock. Evaluate each possible loaded/cached state explicitly.
    internal static class PreviewDecision
    {
        internal static bool ShouldLock(
            bool protectedContent, bool fileCorrupted, bool fileExists,
            bool sealedFile, bool sealUnreadable,
            bool elementLoaded, bool reducedElement, bool hasLeaseKey)
        {
            if (!protectedContent) { return false; }
            if (fileCorrupted || !fileExists || sealUnreadable) { return true; }
            if (!sealedFile) { return false; }
            if (!elementLoaded) { return true; }
            return reducedElement && !hasLeaseKey;
        }
    }
}
