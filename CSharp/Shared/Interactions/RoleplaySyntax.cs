using System;

namespace PrimaryPackage.Interactions;

public static class RoleplaySyntax {
    public static bool IsAction(string text) {
        if (text == null) { return false; }
        ReadOnlySpan<char> value = text.AsSpan().Trim();
        if (value.Length < 5 || value[0] != '*' || value[1] != '*' || value[^1] != '*' || value[^2] != '*') { return false; }
        return !value.Slice(2, value.Length - 4).Trim().IsEmpty;
    }
}
