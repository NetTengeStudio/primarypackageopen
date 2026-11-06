using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace IEngine.Core;

public sealed class SlotBitSet
{
    ulong[] words = new ulong[64];
    ulong[] occupied = new ulong[1];

    public int Capacity => words.Length << 6;

    public void EnsureCapacity(int slots)
    {
        const int maxWords = int.MaxValue / 64;
        if (slots < 0 || slots > maxWords * 64) { throw new ArgumentOutOfRangeException(nameof(slots)); }
        int need = (slots + 63) >> 6;
        if (need <= words.Length) { return; }
        int size = Math.Min(maxWords, Math.Max(need, words.Length * 2));
        var expanded = new ulong[size];
        words.CopyTo(expanded, 0);
        ulong[] summary = occupied;
        int groups = (size + 63) >> 6;
        if (groups > summary.Length)
        {
            summary = new ulong[groups];
            occupied.CopyTo(summary, 0);
        }
        words = expanded;
        occupied = summary;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int slot)
    {
        int w = slot >> 6;
        ref ulong bits = ref words[w];
        if (bits == 0) { occupied[w >> 6] |= 1UL << w; }
        bits |= 1UL << slot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(int slot)
    {
        int w = slot >> 6;
        if ((uint)w >= (uint)words.Length) { return; }
        ulong before = words[w], after = before & ~(1UL << slot);
        if (before == after) { return; }
        words[w] = after;
        if (after == 0) { occupied[w >> 6] &= ~(1UL << w); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int slot)
    {
        int w = slot >> 6;
        return (uint)w < (uint)words.Length && (words[w] & (1UL << slot)) != 0;
    }

    public int Next(int from)
    {
        int w = from >> 6;
        ulong[] ws = words;
        if ((uint)w >= (uint)ws.Length) { return -1; }
        ulong bits = ws[w] & (ulong.MaxValue << from);
        if (bits != 0) { return (w << 6) + BitOperations.TrailingZeroCount(bits); }
        if (++w >= ws.Length) { return -1; }
        ulong cur = ws[w];
        if (cur == 0)
        {
            w = NextOccupiedWord(w + 1);
            if (w < 0) { return -1; }
            cur = ws[w];
        }
        return (w << 6) + BitOperations.TrailingZeroCount(cur);
    }

    public int NextAndNot(SlotBitSet mask, int from)
    {
        int w = from >> 6;
        ulong[] ws = words;
        if ((uint)w >= (uint)ws.Length) { return -1; }
        ulong[] ms = mask.words;
        int msLen = ms.Length;
        ulong bits = ws[w] & ((uint)w < (uint)msLen ? ~ms[w] : ulong.MaxValue) & (ulong.MaxValue << from);
        if (bits != 0) { return (w << 6) + BitOperations.TrailingZeroCount(bits); }
        while (true)
        {
            if (++w >= ws.Length) { return -1; }
            ulong word = ws[w];
            if (word == 0)
            {
                w = NextOccupiedWord(w + 1);
                if (w < 0) { return -1; }
                word = ws[w];
            }
            bits = word & ((uint)w < (uint)msLen ? ~ms[w] : ulong.MaxValue);
            if (bits != 0)
            {
                return (w << 6) + BitOperations.TrailingZeroCount(bits);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int NextOccupiedWord(int from)
    {
        int group = from >> 6;
        ulong[] summary = occupied;
        if ((uint)group >= (uint)summary.Length) { return -1; }
        ulong bits = summary[group] & (ulong.MaxValue << from);
        while (bits == 0)
        {
            if (++group >= summary.Length) { return -1; }
            bits = summary[group];
        }
        return (group << 6) + BitOperations.TrailingZeroCount(bits);
    }

    public bool Any() => NextOccupiedWord(0) >= 0;

    public int PopCount()
    {
        int n = 0;
        for (int group = 0; group < occupied.Length; group++)
        {
            ulong bits = occupied[group];
            while (bits != 0)
            {
                n += BitOperations.PopCount(words[(group << 6) + BitOperations.TrailingZeroCount(bits)]);
                bits &= bits - 1;
            }
        }
        return n;
    }

    public void ClearAll()
    {
        for (int group = 0; group < occupied.Length; group++)
        {
            ulong bits = occupied[group];
            while (bits != 0)
            {
                words[(group << 6) + BitOperations.TrailingZeroCount(bits)] = 0;
                bits &= bits - 1;
            }
            occupied[group] = 0;
        }
    }
}