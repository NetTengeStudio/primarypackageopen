using System;
using Barotrauma;

namespace PrimaryPackage.WallCollision {
    // Stored in Structure.SpecialTag because Structure.Save serializes this property into .sub.
    // An isolated, versioned suffix preserves user-defined SpecialTag data.
    internal static class WallConfiguration {
        private const string Marker = " [PP_EC_V1:";
        private const char Terminator = ']';

        internal static bool TryRead(Structure wall, out WallMode mode, out WallFace face) {
            mode = WallMode.None;
            face = WallFace.PlusX;
            if (wall == null || string.IsNullOrEmpty(wall.SpecialTag)) { return false; }

            string tag = wall.SpecialTag;
            int marker = tag.LastIndexOf(Marker, StringComparison.Ordinal);
            if (marker < 0) { return false; }

            int start = marker + Marker.Length;
            if (start + 3 >= tag.Length || tag[start + 1] != ':' ||
                tag[start + 3] != Terminator) { return false; }

            int modeValue = tag[start] - '0';
            int faceValue = tag[start + 2] - '0';
            if (modeValue < (int)WallMode.Solid || modeValue > (int)WallMode.OneWay ||
                faceValue < (int)WallFace.PlusX || faceValue > (int)WallFace.MinusY) {
                return false;
            }

            mode = (WallMode)modeValue;
            face = (WallFace)faceValue;
            return true;
        }

        internal static void Prepare(Structure wall, WallMode mode, WallFace face) {
            if (wall == null) { return; }
            string original = RemoveMarker(wall.SpecialTag ?? string.Empty);
            wall.SpecialTag = original + Marker + (char)('0' + (int)mode) + ":" +
                              (char)('0' + (int)face) + Terminator;
        }

        internal static void Remove(Structure wall) {
            if (wall != null) { wall.SpecialTag = RemoveMarker(wall.SpecialTag ?? string.Empty); }
        }

        private static string RemoveMarker(string original) {
            int marker = original.LastIndexOf(Marker, StringComparison.Ordinal);
            if (marker < 0) { return original; }
            int terminator = original.IndexOf(Terminator, marker + Marker.Length);
            // Never remove arbitrary text if an incomplete marker is present.
            if (terminator < 0) { return original; }
            return original.Remove(marker, terminator - marker + 1);
        }
    }
}
