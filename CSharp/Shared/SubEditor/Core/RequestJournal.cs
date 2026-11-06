using System;
using System.Collections.Generic;

namespace PrimaryPackage.SubEditor;

public sealed class RequestJournal
{
    public const int Capacity = 256;
    private readonly Dictionary<uint, SubmitResult> results = new();
    private readonly Queue<uint> order = new();
    private uint last;
    private bool started;
    public bool TryGet(uint id, out SubmitResult result) => results.TryGetValue(id, out result);
    public bool Begin(uint id)
    {
        if (started && unchecked((int)(id - last)) <= 0) { return false; }
        last = id; started = true;
        return true;
    }
    public void Complete(uint id, SubmitResult result)
    {
        if (!results.TryAdd(id, result)) { throw new InvalidOperationException("Request already completed"); }
        order.Enqueue(id);
        if (order.Count > Capacity) { results.Remove(order.Dequeue()); }
    }
}
