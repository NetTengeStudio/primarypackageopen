using System.Reflection;
using HarmonyLib;

namespace PrimaryPackage {
    public static class HarmonyPatchOrder {
        public const int ZoomKeyOrder = Priority.Normal;

        public const int ForeignPoolsFirst = Priority.First;

        public const int CameraPathLast = Priority.Last;

        public static HarmonyMethod Ordered(MethodInfo method, int priority) =>
            new HarmonyMethod(method) { priority = priority };
    }
}
