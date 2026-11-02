using System;

namespace PrimaryPackage.Interactions;

internal enum CatchupResult : byte { Ignore, Tail, Release }

// The transfer ACK and the world-application ACK are separate. Release is sticky:
// losing its response cannot accidentally start a new baseline after live edits resume.
internal sealed class WorldCatchup {
    internal Guid Flow { get; }
    internal int Part { get; private set; }
    internal ulong Cut { get; private set; }
    internal bool Released { get; private set; }
    internal bool Applied { get; private set; }
    internal ulong Replayed { get; private set; }
    internal WorldCatchup(Guid flow) { if (flow == Guid.Empty) { throw new ArgumentException("Empty bootstrap flow"); } Flow = flow; }
    internal void Captured(ulong cut) {
        if (Applied || Released || cut < Cut) { throw new InvalidOperationException("Baseline cut cannot move backwards or reopen an applied stream"); }
        Cut = Replayed = cut;
    }
    internal CatchupResult Confirm(Guid flow, int part, ulong cut, ulong head) {
        if (flow != Flow || part != Part || cut != Cut) { return CatchupResult.Ignore; }
        if (Released) { return CatchupResult.Release; }
        if (head < Cut) { throw new InvalidOperationException("World epoch changed during bootstrap"); }
        if (Applied) { return CatchupResult.Ignore; }
        Applied = true; return CatchupResult.Tail;
    }
    internal void Advance(ulong sequence) {
        if (!Applied || Released || sequence < Replayed) { throw new InvalidOperationException("Invalid catch-up replay order"); }
        Replayed = sequence;
    }
    internal bool CanRelease(ulong head) => Applied && !Released && Replayed == head;
    internal void Release(ulong head) {
        if (!CanRelease(head)) { throw new InvalidOperationException("Native events require a complete ordered catch-up stream"); }
        Released = true;
    }
}
