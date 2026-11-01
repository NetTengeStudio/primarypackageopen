using System.Numerics;
using System.Runtime.CompilerServices;

namespace IEngine.Core;

public static class KeyedIntroSort
{
    const int SizeThreshold = 16;

    public static void Sort<T>(float[] keys, T[] items, int count) where T : class
    {
        if (count > 1) { IntroSort(keys, items, 0, count, 2 * (BitOperations.Log2((uint)count) + 1)); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Less(float a, float b) => a < b || (float.IsNaN(a) && !float.IsNaN(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Greater(float a, float b) => a > b || (float.IsNaN(b) && !float.IsNaN(a));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Swap<T>(float[] keys, T[] items, int i, int j)
    {
        (keys[i], keys[j]) = (keys[j], keys[i]);
        (items[i], items[j]) = (items[j], items[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void SwapIfGreater<T>(float[] keys, T[] items, int i, int j)
    {
        if (Greater(keys[i], keys[j])) { Swap(keys, items, i, j); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void IntroSort<T>(float[] keys, T[] items, int lo, int length, int depthLimit)
    {
        int size = length;
        while (size > 1)
        {
            if (size <= SizeThreshold)
            {
                if (size == 2) { SwapIfGreater(keys, items, lo, lo + 1); return; }
                if (size == 3)
                {
                    SwapIfGreater(keys, items, lo, lo + 1);
                    SwapIfGreater(keys, items, lo, lo + 2);
                    SwapIfGreater(keys, items, lo + 1, lo + 2);
                    return;
                }
                InsertionSort(keys, items, lo, size);
                return;
            }
            if (depthLimit == 0) { HeapSort(keys, items, lo, size); return; }
            depthLimit--;
            int p = Partition(keys, items, lo, size);
            IntroSort(keys, items, lo + p + 1, size - p - 1, depthLimit);
            size = p;
        }
    }

    static int Partition<T>(float[] keys, T[] items, int lo, int length)
    {
        int last = lo + length - 1, middle = lo + ((length - 1) >> 1);
        SwapIfGreater(keys, items, lo, middle);
        SwapIfGreater(keys, items, lo, last);
        SwapIfGreater(keys, items, middle, last);
        int nextToLast = lo + length - 2;
        float pivot = keys[middle];
        Swap(keys, items, middle, nextToLast);
        int left = lo, right = nextToLast;
        while (left < right)
        {
            while (left < nextToLast && Greater(pivot, keys[++left])) { }
            while (right > lo && Less(pivot, keys[--right])) { }
            if (left >= right) { break; }
            Swap(keys, items, left, right);
        }
        if (left != nextToLast) { Swap(keys, items, left, nextToLast); }
        return left - lo;
    }

    static void HeapSort<T>(float[] keys, T[] items, int lo, int n)
    {
        for (int i = n >> 1; i >= 1; i--) { DownHeap(keys, items, lo, i, n); }
        for (int i = n; i > 1; i--)
        {
            Swap(keys, items, lo, lo + i - 1);
            DownHeap(keys, items, lo, 1, i - 1);
        }
    }

    static void DownHeap<T>(float[] keys, T[] items, int lo, int i, int n)
    {
        float dKey = keys[lo + i - 1];
        T dItem = items[lo + i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && Less(keys[lo + child - 1], keys[lo + child])) { child++; }
            if (!Less(dKey, keys[lo + child - 1])) { break; }
            keys[lo + i - 1] = keys[lo + child - 1];
            items[lo + i - 1] = items[lo + child - 1];
            i = child;
        }
        keys[lo + i - 1] = dKey;
        items[lo + i - 1] = dItem;
    }

    static void InsertionSort<T>(float[] keys, T[] items, int lo, int length)
    {
        for (int i = 0; i < length - 1; i++)
        {
            float tKey = keys[lo + i + 1];
            T tItem = items[lo + i + 1];
            int j = i;
            while (j >= 0 && Less(tKey, keys[lo + j]))
            {
                keys[lo + j + 1] = keys[lo + j];
                items[lo + j + 1] = items[lo + j];
                j--;
            }
            keys[lo + j + 1] = tKey;
            items[lo + j + 1] = tItem;
        }
    }
}
