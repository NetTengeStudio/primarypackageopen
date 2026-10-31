using System;
using System.Collections.Generic;
using Barotrauma.Items.Components;
using Barotrauma.Networking;

namespace PrimaryPackage.Netcode;

public interface IKeySequence {
    int Count { get; }

    uint KeyAt(int index);
}

public readonly struct ComponentKeys : IKeySequence {
    private readonly List<ItemComponent> components;

    public ComponentKeys(List<ItemComponent> components) => this.components = components;

    public int Count => components.Count;

    public uint KeyAt(int index) => ComponentKey.Of(components[index].GetType());
}

public readonly struct ArrayKeys : IKeySequence {
    private readonly uint[] keys;

    public ArrayKeys(uint[] keys) => this.keys = keys;

    public int Count => keys.Length;

    public uint KeyAt(int index) => keys[index];
}

public readonly struct ComponentAddress : IEquatable<ComponentAddress> {
    public const int WireBits = 40;

    public readonly uint Key;
    public readonly byte Ordinal;

    public ComponentAddress(uint key, byte ordinal) {
        Key = key;
        Ordinal = ordinal;
    }

    public static bool TryAt<TKeys>(in TKeys keys, int index, out ComponentAddress address) where TKeys : struct, IKeySequence {
        address = default;
        if ((uint)index >= (uint)keys.Count) { return false; }
        uint key = keys.KeyAt(index);
        int ordinal = 0;
        for (int i = 0; i < index; i++) {
            if (keys.KeyAt(i) == key) { ordinal++; }
        }
        if (ordinal > byte.MaxValue) { return false; }
        address = new ComponentAddress(key, (byte)ordinal);
        return true;
    }

    public int IndexIn<TKeys>(in TKeys keys) where TKeys : struct, IKeySequence {
        int remaining = Ordinal;
        for (int i = 0; i < keys.Count; i++) {
            if (keys.KeyAt(i) != Key) { continue; }
            if (remaining == 0) { return i; }
            remaining--;
        }
        return -1;
    }

    public static bool TryOf(List<ItemComponent> components, ItemComponent component, out ComponentAddress address) =>
        TryAt(new ComponentKeys(components), components.IndexOf(component), out address);

    public ItemComponent Resolve(List<ItemComponent> components) {
        int index = IndexIn(new ComponentKeys(components));
        return index < 0 ? null : components[index];
    }

    public void Write(IWriteMessage msg) {
        msg.WriteUInt32(Key);
        msg.WriteByte(Ordinal);
    }

    public static ComponentAddress Read(IReadMessage msg) => new ComponentAddress(msg.ReadUInt32(), msg.ReadByte());

    public bool Equals(ComponentAddress other) => Key == other.Key && Ordinal == other.Ordinal;

    public override bool Equals(object obj) => obj is ComponentAddress other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Ordinal);
}
