using System;
using System.Collections;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.Netcode;

public readonly struct StringKeys : IKeySequence {
    private readonly string[] values;

    public StringKeys(string[] values) => this.values = values;

    public int Count => values?.Length ?? 0;

    public uint KeyAt(int index) => (uint)index;
}

public readonly struct NamedKeys : IKeySequence {
    private readonly IList items;
    private readonly Func<object, string> name;

    public NamedKeys(IList items, Func<object, string> name) {
        this.items = items;
        this.name = name;
    }

    public int Count => items?.Count ?? 0;

    public uint KeyAt(int index) => ComponentKey.Hash(name(items[index]) ?? string.Empty);
}

public static class PowerGroupName {
    private static FieldInfo powerOut;
    private static PropertyInfo connectionName;

    public static string Of(object group) {
        if (group == null) { return string.Empty; }
        powerOut ??= AccessTools.Field(group.GetType(), "PowerOut");
        object connection = powerOut?.GetValue(group);
        if (connection == null) { return string.Empty; }
        connectionName ??= AccessTools.Property(connection.GetType(), "Name");
        return connectionName?.GetValue(connection) as string ?? string.Empty;
    }

    public static void Clear() {
        powerOut = null;
        connectionName = null;
    }
}
