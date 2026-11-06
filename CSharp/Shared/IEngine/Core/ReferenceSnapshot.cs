using System;
using System.Collections.Generic;

namespace IEngine.Core;

/// <summary>
/// Tracks membership changes among selected references on the simulation thread.
/// Only selected entries are retained. Buffers grow to the observed high-water
/// mark and are reused; callbacks run after source enumeration has finished.
/// </summary>
public sealed class ReferenceSnapshot<T> where T : class
{
    List<T> previous = new(), next = new();
    readonly HashSet<T> seen = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Visits removals before additions, in their source order. A null callback
    /// advances the snapshot without notifications. Reordering and duplicate
    /// references do not change membership. Callbacks must not reenter this module.
    /// An exception leaves the previous snapshot intact.
    /// </summary>
    public void Update(List<T> source, Predicate<T> include, Action<T> changed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(include);
        try
        {
            foreach (T entry in source)
            {
                if (include(entry)) { next.Add(entry); }
            }
            bool same = previous.Count == next.Count;
            for (int i = 0; same && i < next.Count; i++)
            {
                same = ReferenceEquals(previous[i], next[i]);
            }
            if (same) { return; }
            if (changed != null)
            {
                foreach (T entry in next) { seen.Add(entry); }
                foreach (T entry in previous) { if (!seen.Contains(entry)) { changed(entry); } }
                seen.Clear();
                foreach (T entry in previous) { seen.Add(entry); }
                foreach (T entry in next) { if (!seen.Contains(entry)) { changed(entry); } }
            }
            (previous, next) = (next, previous);
        }
        finally
        {
            seen.Clear();
            next.Clear();
        }
    }

    public void Clear()
    {
        previous.Clear();
        next.Clear();
        seen.Clear();
    }
}
