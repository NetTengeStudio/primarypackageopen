using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PrimaryPackage.Core;

public sealed class EntityBits {
    private const int WordCount = (ushort.MaxValue + 1) / 64;

    private readonly long[] words = new long[WordCount];

    public bool this[ushort id] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Volatile.Read(ref words[id >> 6]) & (1L << (id & 63))) != 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(Entity entity) => entity != null && this[entity.ID];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ushort id) => Interlocked.Or(ref words[id >> 6], 1L << (id & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(Entity entity) {
        if (entity != null) { Set(entity.ID); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(ushort id) => Interlocked.And(ref words[id >> 6], ~(1L << (id & 63)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(Entity entity) {
        if (entity != null) { Clear(entity.ID); }
    }

    public void ClearAll() => Array.Clear(words, 0, words.Length);
}
