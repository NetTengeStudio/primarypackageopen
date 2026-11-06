using System;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Picks id ranges for entities the server creates on behalf of editors. Every peer keeps its own low ids for things it
/// creates alone (the editor's dummy character, a wire still in the hand), and the engine fills free ids from the bottom,
/// so the server allocates from the top of the id space: a block placed there cannot collide with either.
/// The engine's loader (<c>IdRemap</c>) renumbers a block of entities contiguously from one destination id,
/// whatever gaps the saved ids had, so a block of N entities needs N consecutive free ids and is addressed by the first of them.
/// </summary>
public static class IdWindow
{
    /// <summary>
    /// The highest free id in [1, top], or 0 when there is none. The engine gives a submarine "top minus the number of loaded submarines",
    /// which collides with a live one as soon as a submarine other than the last loaded is removed; this rule gives the same id while the
    /// used ones are contiguous from the top and the highest hole afterwards, the same on every peer that holds the same ids.
    /// </summary>
    public static int HighestFree(Func<int, bool> isUsed, int top)
    {
        for (int id = top; id >= 1; id--) { if (!isUsed(id)) { return id; } }
        return 0;
    }

    /// <summary>The first id of the highest run of <paramref name="count"/> free ids inside [1, limit), or false when there is none.</summary>
    public static bool TryFindFromTop(Func<int, bool> isUsed, int limit, int count, out int first)
    {
        first = 0;
        if (count < 1 || limit < 2 || count >= limit) { return false; }
        int run = 0;
        for (int id = limit - 1; id >= 1; id--)
        {
            run = isUsed(id) ? 0 : run + 1;
            if (run == count) { first = id; return true; }
        }
        return false;
    }
}
