using System;
using System.Collections.Generic;

namespace INet;

public static class ComponentKey {
    private const uint Offset = 2166136261;
    private const uint Prime = 16777619;

    private static readonly Dictionary<Type, uint> keys = new Dictionary<Type, uint>(ReferenceEqualityComparer.Instance);

    public static uint Of(Type type) {
        if (keys.TryGetValue(type, out uint key)) { return key; }
        key = Hash(type.Name);
        keys[type] = key;
        return key;
    }

    public static uint Hash(ReadOnlySpan<char> name) {
        uint hash = Offset;
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            hash = (hash ^ (byte)c) * Prime;
            hash = (hash ^ (byte)(c >> 8)) * Prime;
        }
        return hash;
    }

    public static void Clear() => keys.Clear();
}
