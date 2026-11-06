using System;
using Barotrauma;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// The engine numbers a new submarine "ReservedIDStart minus the number of loaded submarines". That is only free while submarines are
/// removed in the reverse order they were loaded: with submarines A, B loaded, removing A and loading C gives C the id B already has, and
/// the entity constructor throws. Removing a submarine is exactly what the live editor adds, so the id is taken from the highest free one.
/// With no holes this is the engine's own number, so rounds without editor removals are unchanged and every peer agrees.
/// </summary>
public static class SubIds
{
    [ThreadStatic] private static ushort requested;

    public readonly struct Reservation : IDisposable
    {
        private readonly ushort previous;
        public Reservation(ushort id) { previous = requested; requested = id; }
        public void Dispose() => requested = previous;
    }

    public static void Attach(Harmony harmony) =>
        ModuleHost.Guard("Submarine.DetermineID", () => harmony.Patch(
            AccessTools.DeclaredMethod(typeof(Submarine), "DetermineID", new[] { typeof(ushort), typeof(Submarine) }),
            prefix: ModuleHost.Bind(typeof(SubIds), nameof(Prefix))));

    private static bool Prefix(ref ushort __result)
    {
        if (requested != 0)
        {
            ushort wanted = requested;
            requested = 0;
            if (Entity.FindEntityByID(wanted) != null) { throw new InvalidOperationException($"Submarine id {wanted} is occupied"); }
            __result = wanted;
            return false;
        }
        int id = IdWindow.HighestFree(used => Entity.FindEntityByID((ushort)used) != null, Entity.ReservedIDStart);
        if (id == 0) { return true; }
        __result = (ushort)id;
        return false;
    }
}
