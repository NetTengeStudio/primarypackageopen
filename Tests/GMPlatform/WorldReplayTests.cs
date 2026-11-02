using PrimaryPackage.Interactions;

internal static partial class Program {
    private static void ReplayWindow() {
        var window = new WorldReplayWindow(10, 262144, 0);
        for (ulong sequence = 11; sequence <= 26; sequence++) { window.Enqueue(sequence, 1024, 0); }
        Check(window.Count == 16 && window.Bytes == 16384 && !window.CanSend(1, 0), "application window bounds queued operations even when transport accepts writes");
        Reject<InvalidOperationException>(() => window.Enqueue(27, 1, 0), "overflow cannot enter the reliable replay queue");
        Check(window.Acknowledge(18, 1) && window.Count == 8 && window.Bytes == 8192 && window.Acknowledged == 18, "coalesced application acknowledgement releases exactly a complete operation prefix");
        Check(!window.Acknowledge(15, 2) && window.Count == 8, "reordered or duplicate application ACK cannot release later edits");
        Reject<ArgumentException>(() => window.Acknowledge(99, 2), "client cannot acknowledge an operation that was never sent");
        Check(window.Acknowledge(26, 3) && window.Count == 0 && window.Bytes == 0 && !window.Expired(1000), "drained application window has no false timeout");
        window = new WorldReplayWindow(0, 262144, 0); window.Enqueue(1, WorldReplayWindow.ByteCapacity, 0);
        Check(!window.CanSend(1, 1), "application window bounds bytes independently of operation count");
        Check(!window.Expired(59.999) && window.Expired(60), "no application progress has a bounded sixty-second lifetime");
        window.Acknowledge(1, 60); Check(window.CanSend(1024, 60) && !window.Expired(120), "acknowledgement drains expired outstanding bytes");
        window = new WorldReplayWindow(0, 2048, 0); window.Enqueue(1, 2048, 0); window.Acknowledge(1, 0);
        Check(!window.CanSend(2048, .99) && window.CanSend(2048, 1), "slow peer replay pacing respects negotiated bytes per second");
        window.Enqueue(2, 2048, 1); Reject<ArgumentOutOfRangeException>(() => window.CanSend(1, .5), "replay clock cannot move backwards");
        Reject<ArgumentOutOfRangeException>(() => window.CanSend(WorldReplayWindow.ByteCapacity + 1, 1), "single oversized operation cannot bypass queue bounds");
        Reject<InvalidOperationException>(() => window.Enqueue(2, 1, 1), "operation sequences are strictly increasing");

        // Receiver ACKs intermittently under sustained edits; the sender can fill only 16 slots.
        window = new WorldReplayWindow(100, 8192, 0);
        ulong sent = 100, confirmed = 100; int maximumOutstanding = 0;
        for (int tick = 0; tick < 10000; tick++) {
            double now = tick * .01;
            if (window.CanSend(512, now)) { window.Enqueue(++sent, 512, now); }
            maximumOutstanding = Math.Max(maximumOutstanding, window.Count);
            if (tick % 127 == 0 && window.Count > 0) { confirmed = window.Sent; window.Acknowledge(confirmed, now); }
            Check(window.Count <= 16 && window.Bytes <= WorldReplayWindow.ByteCapacity && !window.Expired(now), "sustained edit replay keeps a bounded application queue");
        }
        Check(sent > 500 && confirmed > 500 && maximumOutstanding == 16, "slow application acknowledgements permit continued progress without an unbounded reliable backlog");
        var catchup = new WorldCatchup(Guid.NewGuid()); catchup.Captured(100); catchup.Confirm(catchup.Flow, 0, 100, 101);
        window = new WorldReplayWindow(100, 262144, 0);
        for (ulong sequence = 101; sequence <= 108; sequence++) { window.Enqueue(sequence, 512, 0); catchup.Advance(sequence); }
        Check(window.Count == 8 && catchup.CanRelease(108), "ordered handoff can be queued at the current send-side cut with acknowledgements still in flight");
        catchup.Release(108);
        Check(catchup.Released && window.Count > 0 && catchup.Confirm(catchup.Flow, 0, 100, 999) == CatchupResult.Release,
            "continuous writers never require a quiet roundtrip; local ordered release follows all progress markers and duplicate completion cannot reopen the stream");
    }
}
