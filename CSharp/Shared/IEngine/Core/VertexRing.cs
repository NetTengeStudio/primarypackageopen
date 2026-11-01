namespace IEngine.Core;

public struct VertexRing
{
    readonly int capacity;
    int head;

    public VertexRing(int capacity)
    {
        this.capacity = capacity;
        head = capacity;
    }

    public int Capacity => capacity;
    public long Discards { get; private set; }

    public int Reserve(int count, out bool discard)
    {
        discard = head + count > capacity;
        if (discard)
        {
            head = 0;
            Discards++;
        }
        int start = head;
        head += count;
        return start;
    }

    public void Invalidate() => head = capacity;
}
