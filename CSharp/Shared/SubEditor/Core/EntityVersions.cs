using System;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Dense per-entity revision table. Two slots per entity (latest writer, and the latest write by a different actor)
/// are enough to answer "did anyone other than me touch this after revision B" exactly, which lets one actor
/// pipeline several ops without false conflicts while still catching every foreign write.
/// </summary>
public sealed class EntityVersions
{
    public const int Capacity = ushort.MaxValue + 1;
    private readonly ulong[] latest = new ulong[Capacity];
    private readonly ulong[] otherActor = new ulong[Capacity];
    private readonly byte[] latestActor = new byte[Capacity];

    public ulong Latest(ushort id) => latest[id];

    public ulong ForeignRev(ushort id, byte actor) => latestActor[id] != actor ? latest[id] : otherActor[id];

    /// <summary>Returns the first id with a foreign write after baseRev, or -1.</summary>
    public int FirstConflict(ReadOnlySpan<ushort> ids, byte actor, ulong baseRev)
    {
        for (int i = 0; i < ids.Length; i++) { if (ForeignRev(ids[i], actor) > baseRev) { return ids[i]; } }
        return -1;
    }

    public void Stamp(ReadOnlySpan<ushort> ids, byte actor, ulong rev)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            ushort id = ids[i];
            if (latest[id] != 0 && latestActor[id] != actor) { otherActor[id] = latest[id]; }
            latest[id] = rev; latestActor[id] = actor;
        }
    }
}
