using System;

namespace PrimaryPackage.Core;

public sealed class DirtyBitSet
{
    private const int WordCount = (ushort.MaxValue + 1) / 64;

    private readonly long[] words = new long[WordCount];
    private ushort[] touched = new ushort[64];
    private int count;

    public bool Contains(ushort id) => (words[id >> 6] & (1L << (id & 63))) != 0L;

    public bool Add(ushort id)
    {
        long bit = 1L << (id & 63);
        int word = id >> 6;
        if ((words[word] & bit) != 0L) { return false; }
        words[word] |= bit;
        if (count == touched.Length) { Array.Resize(ref touched, touched.Length * 2); }
        touched[count++] = id;
        return true;
    }

    public void Clear()
    {
        for (int i = 0; i < count; i++)
        {
            ushort id = touched[i];
            words[id >> 6] &= ~(1L << (id & 63));
        }
        count = 0;
    }
}
