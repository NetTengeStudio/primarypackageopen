using System;
using System.Runtime.CompilerServices;

namespace IEngine.Core;

public sealed class StableKeyOrder
{
    long[] packed = new long[256];
    int count;

    public int Count => count;

    public void Reset(int n)
    {
        if (packed.Length < n) { packed = new long[Math.Max(n, packed.Length * 2)]; }
        count = n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKey(int index, int key) => packed[index] = ((long)key << 32) | (uint)index;

    public void Sort() => packed.AsSpan(0, count).Sort();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexAt(int position) => (int)(uint)packed[position];
}
