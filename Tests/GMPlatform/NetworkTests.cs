using System.Diagnostics;
using System.Text;
using PrimaryPackage.Networking;
using PrimaryPackage.Interactions;

internal static partial class Program {
    private static void DecodeBadBits(byte[] bytes, int kind) {
        var reader = new BitReader(bytes);
        if (kind == 0) { reader.UInt(); } else if (kind == 1) { reader.Bits(1); reader.Align(); } else { reader.Text(32); }
    }
    private static void NetworkBits() {
        Baselines();
        var random = new Random(78392); var bytes = new byte[1024];
        for (int i = 0; i < 20000; i++) {
            int prefix = i % 8, count = i % 65;
            ulong value = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
            if (count < 64) { value &= (1UL << count) - 1; }
            var writer = new BitWriter(bytes); writer.Bits(0, prefix); writer.Bits(value, count); writer.Align();
            var reader = new BitReader(bytes.AsSpan(0, writer.LengthBytes)); reader.Bits(prefix);
            Check(reader.Bits(count) == value, "unaligned bit field preserves all widths including zero and 64"); reader.Finish();
        }
        foreach (ulong value in new ulong[] { 0, 1, 127, 128, 16383, 16384, uint.MaxValue, (ulong)long.MaxValue, ulong.MaxValue }) {
            var writer = new BitWriter(bytes); writer.UInt(value); var reader = new BitReader(bytes.AsSpan(0, writer.LengthBytes));
            Check(reader.UInt() == value, "canonical varint roundtrip"); reader.Finish();
        }
        foreach (int value in new[] { int.MinValue, -10000000, -1, 0, 1, 10000000, int.MaxValue }) {
            var writer = new BitWriter(bytes); writer.Int(value); var reader = new BitReader(bytes.AsSpan(0, writer.LengthBytes));
            Check(reader.Int() == value, "zigzag preserves signed extrema"); reader.Finish();
        }
        Guid id = Guid.NewGuid(); var encoded = new BitWriter(bytes);
        encoded.Bits(5, 3); encoded.Guid(id); encoded.Text("Подлодка · 🚢", 64); encoded.Single(-13.5f); encoded.Align();
        var decoded = new BitReader(bytes.AsSpan(0, encoded.LengthBytes));
        Check(decoded.Bits(3) == 5 && decoded.Guid() == id && decoded.Text(64) == "Подлодка · 🚢" && decoded.Single() == -13.5f, "typed values and strict UTF8 roundtrip across alignment"); decoded.Finish();
        foreach (byte[] bad in new[] { new byte[] { 0x80 }, new byte[] { 0x80, 0 }, Enumerable.Repeat((byte)0xff, 10).ToArray() }) { Reject<ArgumentException>(() => DecodeBadBits(bad, 0), "varint rejects truncation, overlong encoding and overflow"); }
        Reject<ArgumentException>(() => DecodeBadBits(new byte[] { 0xfe }, 1), "padding must be canonical zero bits");
        Reject<ArgumentException>(() => DecodeBadBits(new byte[] { 2, 0xc0, 0x80 }, 2), "invalid UTF8 must not be replaced silently");
        Check(Crc32C.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xe3069283, "CRC32C standard check vector");
        Guid transfer = Guid.NewGuid(); Span<byte> packet = stackalloc byte[ChunkWire.MaxPacketBytes];
        var output = ChunkWire.Start(packet, ChunkPacket.Data, transfer); output.UInt(17); output.Bytes(bytes.AsSpan(0, 768)); int length = ChunkWire.Finish(ref output, packet);
        var input = ChunkWire.Open(packet[..length], out var kind, out var token);
        Check(kind == ChunkPacket.Data && token == transfer && input.UInt() == 17 && input.Bytes(768).SequenceEqual(bytes.AsSpan(0, 768)), "chunk packet header and payload roundtrip"); input.Finish();
        for (int i = 0; i < length; i++) {
            byte[] corrupt = packet[..length].ToArray(); corrupt[i] ^= 1;
            Reject<ArgumentException>(() => { ChunkWire.Open(corrupt, out _, out _); }, "packet corruption is rejected before delivery");
        }
    }
    private static void Baselines() {
        Guid epoch = Guid.NewGuid(), token = Guid.NewGuid(), frame = Guid.NewGuid();
        var original = new WorldBaseline(epoch, 25, new[] { new BaselineGraph(65532, new byte[32]) },
            new[] { new BaselineItem(token, 120, "junctionbox", 65532, true, frame, -10000000, 10000000, 24,
                new[] { new BaselineProperty(0, "Barotrauma.Items.Components.PowerTransfer", "Test", "значение 🚢", 25) }) });
        byte[] encoded = original.Encode(); var decoded = WorldBaseline.Decode(encoded);
        Check(decoded.Epoch == epoch && decoded.Cut == 25 && decoded.Items[0].Incarnation == token && decoded.Items[0].Properties[0] == original.Items[0].Properties[0] && encoded.AsSpan().SequenceEqual(decoded.Encode()), "baseline binary roundtrip preserves epoch, frame, typed address and revision");
        for (int n = 0; n < encoded.Length; n++) { byte[] truncated = encoded.AsSpan(0, n).ToArray(); Reject<ArgumentException>(() => WorldBaseline.Decode(truncated), "partial baselines never become an applicable world"); }
        var duplicate = original with { Items = new[] { original.Items[0], original.Items[0] } };
        Reject<ArgumentException>(() => WorldBaseline.Decode(duplicate.Encode()), "baseline duplicate native addresses/incarnations are rejected");
        var future = original with { Cut = 23 };
        Reject<ArgumentException>(() => WorldBaseline.Decode(future.Encode()), "future property/position cannot precede the baseline cut");
        var flow = new WorldCatchup(Guid.NewGuid()); flow.Captured(10);
        Check(flow.Confirm(flow.Flow, 0, 10, 12) == CatchupResult.Tail && flow.Applied && !flow.Released, "edits during snapshot transfer require an ordered tail before native release");
        Check(flow.Confirm(flow.Flow, 0, 10, 12) == CatchupResult.Ignore, "duplicate application ACK cannot restart the replay cursor");
        Check(flow.Confirm(Guid.NewGuid(), 0, 10, 12) == CatchupResult.Ignore && !flow.Released, "another connection/flow cannot release the barrier");
        Check(!flow.CanRelease(12), "native handoff cannot overtake unprojected changes");
        flow.Advance(11); flow.Advance(12); Check(flow.CanRelease(12), "replayed tail reaches an exact server cut");
        // Simulate a writer that continues while the network is in flight. The handoff
        // is queued at the send-side cut, never waits for a whole RTT with no edits.
        for (ulong sequence = 13; sequence <= 100; sequence++) { flow.Advance(sequence); }
        flow.Release(100);
        Check(flow.Confirm(flow.Flow, 0, 10, 999) == CatchupResult.Release && flow.Replayed == 100, "lost release response is idempotent despite continuous later edits");
        Reject<InvalidOperationException>(() => flow.Captured(999), "released native stream cannot reopen an old baseline");
        Reject<InvalidOperationException>(() => flow.Advance(1000), "the live stream owns changes after the handoff marker");
        Guid baseEpoch = Guid.NewGuid(); Guid first = BaseWorldIdentity.For(baseEpoch, 1), second = BaseWorldIdentity.For(baseEpoch, 2);
        Check(first != second && BaseWorldIdentity.TryResolve(baseEpoch, first, out ushort nativeId) && nativeId == 1, "verified original entities have compact round-scoped identities");
        Check(!BaseWorldIdentity.TryResolve(Guid.NewGuid(), first, out _) && !BaseWorldIdentity.TryResolve(Guid.Empty, first, out _), "native ID reuse across world epochs cannot bind an old incarnation");
    }
    private readonly record struct Datagram(double Due, int Index, ReadOnlyMemory<byte> Payload, int Prefix, ulong Following, bool Ack);
    private static object NetworkTransfers() {
        var cases = new List<object>();
        foreach (var scenario in new (double Loss, double Latency, int Rate, int Length)[] { (0.0, .025, 65536, 256 * 1024), (.25, .3, 8192, 64 * 1024), (.6, .15, 4096, 16 * 1024), (.2, 2.0, 4096, 16 * 1024) }) {
            var random = new Random(12083); byte[] source = new byte[scenario.Length]; random.NextBytes(source);
            var blob = new WorldBlob(Guid.NewGuid(), source); var sender = new ChunkSender(blob, scenario.Rate, 0);
            using var receiver = new ChunkReceiver(blob.Id, blob.Length, blob.Digest, 0); var packets = new List<Datagram>(128);
            double now = 0, nextAck = 0; int delivered = 0;
            for (int tick = 0; tick < 180000 && !sender.Complete; tick++) {
                now = tick * .01;
                for (int budget = 0; budget < 4 && sender.TryNext(now, out int index, out var payload); budget++) {
                    if (random.NextDouble() >= scenario.Loss) { packets.Add(new(now + scenario.Latency + random.NextDouble() * scenario.Latency, index, payload, 0, 0, false)); }
                }
                // Deliver in deliberately changing order, and allow duplicate retransmissions.
                for (int i = packets.Count - 1; i >= 0; i--) {
                    var packet = packets[i]; if (packet.Due > now) { continue; }
                    packets.RemoveAt(i);
                    if (packet.Ack) { sender.Acknowledge(packet.Prefix, packet.Following, now); }
                    else { receiver.Receive(packet.Index, packet.Payload.Span, now); delivered++; }
                }
                if (now >= nextAck) {
                    nextAck = now + .1;
                    if (random.NextDouble() >= scenario.Loss) { packets.Add(new(now + scenario.Latency + random.NextDouble() * scenario.Latency, 0, default, receiver.Prefix, receiver.Following, true)); }
                }
                if (sender.Expired(now) || receiver.Expired(now)) { break; }
            }
            Check(sender.Complete && receiver.Complete && receiver.Payload.Span.SequenceEqual(source), "loss/reorder/duplicates/slow transfer converges to the exact immutable snapshot");
            cases.Add(new { loss = scenario.Loss, oneWayLatencySeconds = scenario.Latency, bytesPerSecond = scenario.Rate, bytes = source.Length, elapsedSimulatedSeconds = now, sender.Retransmissions, delivered });
        }
        var checkBlob = new WorldBlob(Guid.NewGuid(), new byte[1024]); var badSender = new ChunkSender(checkBlob, 4096, 0);
        Reject<ArgumentException>(() => badSender.Acknowledge(1, 0, 0), "ACK of unsent data cannot open the native readiness barrier");
        Check(badSender.Expired(60), "a peer that makes no progress has a bounded resource lifetime");
        using var badReceiver = new ChunkReceiver(checkBlob.Id, checkBlob.Length, checkBlob.Digest, 0);
        badReceiver.Receive(0, checkBlob.Chunk(0).Span, 0);
        var corruptDuplicate = new byte[WorldBlob.ChunkBytes]; corruptDuplicate[0] = 1;
        Reject<ArgumentException>(() => badReceiver.Receive(0, corruptDuplicate, 1), "contradictory duplicate chunk faults the transfer");
        Check(badReceiver.Faulted, "faulted receiver cannot expose a snapshot");
        return new { cases, scope = "deterministic packet scheduler and production transfer state machines; not a game connection" };
    }
    private static object MeasureNetworkBits() {
        byte[] buffer = new byte[128]; Guid id = Guid.NewGuid(); ulong checksum = 0;
        const int samples = 100000;
        for (int i = 0; i < 1000; i++) { checksum ^= NetworkRoundtrip(buffer, id, i); }
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) { checksum ^= NetworkRoundtrip(buffer, id, i); }
        long ticks = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(bytes == 0, "warmed bit serializer and parser allocate zero bytes");
        return new { samples, nanosecondsPerRoundtrip = ticks * 1e9 / Stopwatch.Frequency / samples, allocatedBytes = bytes, checksum, scope = "production bit codec only, Guid/flags/revision/coordinates, excludes engine/network and string conversion" };
    }
    private static ulong NetworkRoundtrip(byte[] buffer, Guid id, int value) {
        var writer = new BitWriter(buffer); writer.Guid(id); writer.Bits(13, 5); writer.UInt((ulong)value); writer.Int(-value); writer.Int(value); writer.Align();
        var reader = new BitReader(buffer.AsSpan(0, writer.LengthBytes)); Guid got = reader.Guid(); ulong flags = reader.Bits(5), revision = reader.UInt(); int x = reader.Int(), y = reader.Int(); reader.Finish();
        return (got == id ? flags : 0) ^ revision ^ (uint)x ^ (uint)y;
    }
}
