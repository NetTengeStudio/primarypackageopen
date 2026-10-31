using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IEngine.Core;

public static class ListOps
{
    public static bool RemoveReference<T>(List<T> list, T item) where T : class
    {
        var span = CollectionsMarshal.AsSpan(list);
        int lo = 0, hi = span.Length - 1;
        while (lo <= hi)
        {
            if (ReferenceEquals(span[hi], item)) { list.RemoveAt(hi); return true; }
            if (ReferenceEquals(span[lo], item)) { list.RemoveAt(lo); return true; }
            hi--;
            lo++;
        }
        return false;
    }
}
