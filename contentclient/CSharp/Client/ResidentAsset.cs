using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Linq;
using Barotrauma;

namespace SubSeal.Client
{
    internal static class ResidentAsset
    {
        private const int UnreadableRetries = 3;
        private const int UnreadableRetryDelayMilliseconds = 100;

        private sealed class Origin
        {
            internal string FilePath;
        }

        private static readonly ConditionalWeakTable<XElement, Origin> reduced = new ConditionalWeakTable<XElement, Origin>();

        private static SubmarineInfo previewing;

        internal static void Evict(SubmarineInfo info)
        {
            if (info?.SubmarineElement is null) { return; }
            if (SealedFile.Probe(info.FilePath) != SealProbe.Present) { return; }

            SubmarineSkeleton.ReduceInPlace(info.SubmarineElement);
            reduced.Remove(info.SubmarineElement);
            reduced.Add(info.SubmarineElement, new Origin { FilePath = info.FilePath });
        }

        internal static bool Rehydrate(SubmarineInfo info)
        {
            if (info?.SubmarineElement is null) { return true; }
            if (!reduced.TryGetValue(info.SubmarineElement, out Origin origin)) { return true; }
            if (!string.Equals(origin.FilePath, info.FilePath, System.StringComparison.OrdinalIgnoreCase)) { return true; }

            if (!Restore(info))
            {
                Condemn(info);
                return false;
            }

            return true;
        }

        internal static void HoldPreviewed(SubmarineInfo info) => previewing = info;

        internal static void EvictPreviewed()
        {
            SubmarineInfo info = previewing;
            previewing = null;
            Evict(info);
        }

        private static bool Restore(SubmarineInfo info)
        {
            for (int attempt = 0; ; attempt++)
            {
                SealProbe probe = SealedFile.Probe(info.FilePath);

                if (probe == SealProbe.Unreadable && attempt < UnreadableRetries)
                {
                    Thread.Sleep(UnreadableRetryDelayMilliseconds);
                    continue;
                }

                if (probe != SealProbe.Present) { return false; }
                break;
            }

            XElement skeleton = info.SubmarineElement;
            info.Reload();

            if (info.IsFileCorrupted || info.SubmarineElement is null) { return false; }
            if (ReferenceEquals(info.SubmarineElement, skeleton)) { return false; }
            if (SubmarineSkeleton.IsReduced(info.SubmarineElement)) { return false; }

            return true;
        }

        private static void Condemn(SubmarineInfo info)
        {
            EngineSymbols.MarkFileCorrupted(info);
            info.SubmarineElement?.RemoveNodes();
        }
    }
}
