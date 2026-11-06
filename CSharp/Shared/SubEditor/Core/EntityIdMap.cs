using System;

namespace PrimaryPackage.SubEditor;

public sealed class EntityIdMap
{
    private readonly int[] source;
    private readonly int first;

    private EntityIdMap(int[] source, int first) { this.source = source; this.first = first; }

    public int Count => source.Length;
    public ushort Destination(int index) => (ushort)(first == 0 ? source[index] : first + index);

    public ushort Resolve(int id)
    {
        if ((uint)(id - 1) >= ushort.MaxValue - 1u) { return 0; }
        int index = Array.BinarySearch(source, id);
        return index < 0 || first == 0 ? (ushort)id : (ushort)(first + index);
    }

    public static bool TryCreate(ReadOnlySpan<int> ids, int first, out EntityIdMap map)
    {
        map = null;
        if (ids.IsEmpty || first < 0 || first > ushort.MaxValue - ids.Length) { return false; }
        int[] sorted = ids.ToArray();
        Array.Sort(sorted);
        for (int i = 0; i < sorted.Length; i++)
        {
            if ((uint)(sorted[i] - 1) >= ushort.MaxValue - 1u || i > 0 && sorted[i] == sorted[i - 1]) { return false; }
        }
        map = new EntityIdMap(sorted, first);
        return true;
    }
}
