using System.Collections.Generic;

namespace IEngine.Core;

static class ListAppend
{
    public static void Copy<T>(IEnumerable<T> source, List<T> destination, bool specialize)
    {
        if (specialize && source is List<T> list && list.GetType() == typeof(List<T>) && !ReferenceEquals(list, destination))
        {
            destination.AddRange(list);
            return;
        }
        foreach (T value in source) { destination.Add(value); }
    }
}
