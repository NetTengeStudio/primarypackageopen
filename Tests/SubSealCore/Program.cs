using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Server;
using SubSeal.Wire;

internal static class Program
{
    static int passed;

    static void Main()
    {
        Test(SealRoundTrip);
        Test(WrappedKey);
        Test(ProtocolContract);
        Test(ConnectionIsolation);
        Test(Expiry);
        Test(IdWraparound);
        Test(TransactionalSessionReplacement);
        Console.WriteLine($"PASS={passed} FAIL=0 (offline SubSeal source tests)");
    }

    static void Test(Action action)
    {
        Timing.TotalTime = 0;
        action();
        passed++;
        Console.WriteLine($"PASS {action.Method.Name}");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    static void SealRoundTrip()
    {
        byte[] key = RandomNumberGenerator.GetBytes(Protocol.KeyLength);
        byte[] plain = Encoding.UTF8.GetBytes("Barotrauma / isolated sealed transport regression");
        uint lease = 178u;
        byte[] sealedPayload = new byte[TransferSeal.SealedLength(plain.Length)];

        TransferSeal.Seal(plain, key, lease, Protocol.KindSubmarine, sealedPayload);
        Require(TransferSeal.IsSealed(sealedPayload), "valid header must be detected");
        Require(TransferSeal.ReadLeaseId(sealedPayload) == lease, "lease id roundtrip");
        Require(TransferSeal.ReadKind(sealedPayload) == Protocol.KindSubmarine, "payload kind roundtrip");

        byte[] opened = new byte[plain.Length];
        Require(TransferSeal.TryOpen(sealedPayload, key, opened, out int length), "valid decrypt");
        Require(length == plain.Length && opened.SequenceEqual(plain), "plaintext equality");

        sealedPayload[Protocol.PayloadOffset] ^= 1;
        Require(!TransferSeal.TryOpen(sealedPayload, key, new byte[plain.Length], out _),
            "tampering must invalidate authenticated ciphertext");
        sealedPayload[Protocol.PayloadOffset] ^= 1;
        byte[] wrongKey = RandomNumberGenerator.GetBytes(Protocol.KeyLength);
        Require(!TransferSeal.TryOpen(sealedPayload, wrongKey, new byte[plain.Length], out _),
            "wrong content key must fail");
    }

    static void WrappedKey()
    {
        byte[] contentKey = RandomNumberGenerator.GetBytes(Protocol.KeyLength);
        byte[] sessionKey = RandomNumberGenerator.GetBytes(Protocol.KeyLength);
        byte[] wrapped = new byte[TransferSeal.WrappedKeyLength];
        TransferSeal.WrapKey(contentKey, sessionKey, 15, wrapped);
        byte[] restored = new byte[Protocol.KeyLength];
        Require(TransferSeal.TryUnwrapKey(wrapped, sessionKey, 15, restored), "valid wrapped key");
        Require(restored.SequenceEqual(contentKey), "key contents preserved");
        Require(!TransferSeal.TryUnwrapKey(wrapped, sessionKey, 16, new byte[Protocol.KeyLength]),
            "wrong lease id must fail");
        Require(!TransferSeal.TryUnwrapKey(wrapped, RandomNumberGenerator.GetBytes(Protocol.KeyLength), 15,
            new byte[Protocol.KeyLength]), "wrong session key must fail");
    }

    static void ProtocolContract()
    {
        Require(Protocol.ContractHash.Length == Protocol.ContractHashLength, "hash length");
        Require(Protocol.Version == 4, "contract version changed: review client and server together");
        Require(Protocol.AllLeases == 0, "lease revoke sentinel");
        Require(Protocol.SealOverhead == TransferSeal.SealedLength(0), "sealed size");
    }

    static void ConnectionIsolation()
    {
        var a = new NetworkConnection();
        var b = new NetworkConnection();
        var leases = new Leases();
        var issued = new List<Leases.Lease>();
        for (int i = 0; i < Leases.MaxOutstandingPerConnection; i++)
        {
            var lease = leases.Issue(a, FileTransferType.Submarine, $"sub_{i}.sub");
            Require(lease != null, "lease issue within limit");
            issued.Add(lease!);
        }
        Require(leases.Outstanding(a) == Leases.MaxOutstandingPerConnection, "A outstanding count");
        Require(leases.Issue(a, FileTransferType.Submarine, "overflow.sub") == null, "limit 17th");
        Require(leases.Issue(a, FileTransferType.Submarine, "sub_0.sub") == null, "duplicate pending");
        var independent = leases.Issue(b, FileTransferType.Submarine, "sub_0.sub");
        Require(independent != null && leases.Outstanding(b) == 1, "B independent");
        Require(leases.FindPending(a, FileTransferType.Submarine, "SUB_0.sub") == null,
            "file identities are ordinal");
        Require(ReferenceEquals(leases.FindPending(a, FileTransferType.Submarine, "sub_0.sub"), issued[0]),
            "same pending identity");

        Require(leases.Arm(b, issued[0].Id) == null, "cannot ACK other connection");
        Require(ReferenceEquals(leases.Arm(a, issued[0].Id), issued[0]), "valid ACK");
        Require(leases.Arm(a, issued[0].Id) == null, "double ACK");
        Require(leases.RetryCandidateCount == 1, "armed candidate");
        var retry = new List<uint>();
        leases.CaptureRetryCandidates(retry);
        Require(retry.Count == 1 && retry[0] == issued[0].Id, "correct retry set");
        leases.MarkConsumed(issued[0]);
        leases.MarkConsumed(issued[0]);
        Require(leases.RetryCandidateCount == 0, "consumed removed from retry");
        Require(leases.Outstanding(a) == Leases.MaxOutstandingPerConnection - 1,
            "single decrement on consume");
        Require(leases.FindPending(a, FileTransferType.Submarine, "sub_0.sub") == null,
            "consumed not pending");
        Require(leases.Issue(a, FileTransferType.Submarine, "sub_0.sub") != null,
            "re-issue after consume");

        byte[] oldKey = issued[0].ContentKey;
        leases.Revoke(a);
        Require(leases.Outstanding(a) == 0 && leases.Connections == 1, "A completely removed");
        Require(leases.Outstanding(b) == 1 && leases.Count == 1, "B retained");
        Require(oldKey.All(x => x == 0), "A key securely zeroed");
        leases.RevokeAll();
        Require(leases.Count == 0 && leases.Connections == 0, "all cleared");
        Require(independent!.ContentKey.All(x => x == 0), "B key zeroed");
    }

    static void Expiry()
    {
        var a = new NetworkConnection();
        var store = new Leases();
        var pending = store.Issue(a, FileTransferType.Submarine, "pending.sub")!;
        var armed = store.Issue(a, FileTransferType.Submarine, "armed.sub")!;
        store.Arm(a, armed.Id);
        Timing.TotalTime = 31;
        store.Sweep(30);
        Require(store.Count == 1 && store.RetryCandidateCount == 1,
            "only pending expires at base retention");
        Require(pending.ContentKey.All(x => x == 0), "expired key erased");
        Require(ReferenceEquals(store.FindRetryCandidate(armed.Id), armed), "armed survives grace");
        Timing.TotalTime = 121;
        store.Sweep(30);
        Require(store.Count == 0 && store.RetryCandidateCount == 0 && store.Outstanding(a) == 0,
            "armed expires after grace");
        Require(armed.ContentKey.All(x => x == 0), "armed expiry zeroed");
    }

    static void TransactionalSessionReplacement()
    {
        var store = new SessionKeys();
        var connection = new NetworkConnection();
        using ECDiffieHellman client = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = client.ExportSubjectPublicKeyInfo();

        byte[] responseKey = new byte[256];
        byte[] salt = new byte[Protocol.SaltLength];
        var original = store.Establish(connection, 1, Protocol.CapabilityCanOpenSealed,
            publicKey, responseKey, out int responseLength, salt);
        Require(original != null && responseLength > 0 && store.Find(connection) == original,
            "initial ECDH established");
        byte[] originalKey = original!.Key;

        Require(store.Establish(connection, 2, Protocol.CapabilityCanOpenSealed,
            new byte[] { 0x01, 0x02, 0x03 }, responseKey, out _, salt) == null,
            "invalid point rejected");
        Require(ReferenceEquals(store.Find(connection), original), "invalid key preserves session");
        Require(originalKey.Any(b => b != 0), "active key remains usable");

        byte[] trailingBytes = publicKey.Concat(new byte[] { 0 }).ToArray();
        Require(store.Establish(connection, 3, Protocol.CapabilityCanOpenSealed,
            trailingBytes, responseKey, out _, salt) == null,
            "trailing SPKI bytes rejected");
        Require(ReferenceEquals(store.Find(connection), original), "malformed SPKI preserves session");

        var replaced = store.Establish(connection, 4, Protocol.CapabilityCanOpenSealed,
            publicKey, responseKey, out _, salt);
        Require(replaced != null && !ReferenceEquals(replaced, original),
            "valid second handshake replaces session");
        Require(originalKey.All(b => b == 0), "old key zeroed on replacement");
        Require(replaced!.Key.Any(b => b != 0), "new key derived");
        byte[] freshKey = replaced.Key;
        store.Drop(connection);
        Require(store.Find(connection) == null && freshKey.All(b => b == 0),
            "explicit drop wipes replacement key");
    }

    static void IdWraparound()
    {
        var a = new NetworkConnection();
        var store = new Leases();
        var first = store.Issue(a, FileTransferType.Submarine, "first.sub")!;
        Require(first.Id == 1, "initial id one");

        FieldInfo nextId = typeof(Leases).GetField("nextId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        nextId.SetValue(store, uint.MaxValue);
        var nearMax = store.Issue(a, FileTransferType.Submarine, "near_max.sub")!;
        var wrap = store.Issue(a, FileTransferType.Submarine, "wrapped.sub")!;
        Require(nearMax.Id == uint.MaxValue, "max id permitted");
        Require(wrap.Id != Protocol.AllLeases && wrap.Id != first.Id &&
                wrap.Id != nearMax.Id, "skip reserved and occupied ids");
        store.RevokeAll();
    }
}
