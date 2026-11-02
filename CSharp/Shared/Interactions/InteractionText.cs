using System;

namespace PrimaryPackage.Interactions;

internal static class InteractionText {
    internal static bool Valid(string value, int max) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) { return false; }
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (char.IsControl(c) || c is '<' or '>') { return false; }
            if (char.IsHighSurrogate(c)) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) { return false; } }
            else if (char.IsLowSurrogate(c)) { return false; }
        }
        return true;
    }
}
