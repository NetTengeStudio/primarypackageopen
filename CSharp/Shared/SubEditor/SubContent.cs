using System;
using Barotrauma;

namespace PrimaryPackage.SubEditor;

/// <summary>Owns access to a submarine blueprint while a content provider keeps its
/// decoded XML resident. The optional provider uses only engine and BCL types.</summary>
internal static class SubContent
{
    internal const string AcquireKey = "SubSeal.ResidentAsset.Acquire.v1";

    public static IDisposable Read(SubmarineInfo info)
    {
        if (info == null) { return null; }
        return AppDomain.CurrentDomain.GetData(AcquireKey) is Func<SubmarineInfo, IDisposable> acquire
            ? acquire(info) : null;
    }
}
