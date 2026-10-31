using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Wire;

namespace SubSeal.Server
{
    [Flags]
    internal enum LeaseState : byte
    {
        Issued = 0,
        Armed = 1 << 0,
        Consumed = 1 << 1
    }

    // The file type and exact path form a lease's transfer identity. Do not
    // normalize paths here: the wire-transfer layer also compares them exactly.
    internal readonly struct TransferIdentity : IEquatable<TransferIdentity>
    {
        internal readonly FileTransferType Type;
        internal readonly string Path;

        internal TransferIdentity(FileTransferType type, string path)
        {
            Type = type;
            Path = path;
        }

        public bool Equals(TransferIdentity other)
            => Type == other.Type && string.Equals(Path, other.Path, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is TransferIdentity other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine((int)Type, StringComparer.Ordinal.GetHashCode(Path ?? string.Empty));
    }

    internal sealed class Leases
    {
        internal const int MaxOutstandingPerConnection = 16;
        internal const double ArmedGraceMultiplier = 4.0;

        internal sealed class Lease
        {
            internal uint Id;
            internal NetworkConnection Connection;
            internal string FilePath;
            internal FileTransferType FileType;
            internal byte[] ContentKey;
            internal LeaseState State;
            internal double IssuedAt;
            internal double LastGrantAt;
            internal double LastRetryAt;

            internal bool Armed => (State & LeaseState.Armed) != 0;
            internal bool Consumed => (State & LeaseState.Consumed) != 0;
        }

        // Each connection owns its own pending-transfer set, which bounds
        // request cost independently of other connected users.
        private sealed class ConnectionState
        {
            internal readonly HashSet<uint> Owned = new HashSet<uint>();
            internal readonly Dictionary<TransferIdentity, Lease> Pending =
                new Dictionary<TransferIdentity, Lease>();
            internal int Outstanding;
        }

        private readonly Dictionary<uint, Lease> byId = new Dictionary<uint, Lease>();
        private readonly Dictionary<NetworkConnection, ConnectionState> byConnection =
            new Dictionary<NetworkConnection, ConnectionState>(ReferenceEqualityComparer.Instance);
        private readonly List<uint> expired = new List<uint>();
        private uint nextId = 1;

        internal IEnumerable<Lease> All => byId.Values;
        internal int Count => byId.Count;
        internal int Connections => byConnection.Count;

        internal int Outstanding(NetworkConnection connection)
            => connection != null && byConnection.TryGetValue(connection, out ConnectionState state)
                ? state.Outstanding : 0;

        internal Lease Issue(NetworkConnection connection, FileTransferType fileType, string filePath)
        {
            if (connection == null || filePath == null) { return null; }

            if (!byConnection.TryGetValue(connection, out ConnectionState state))
            {
                state = new ConnectionState();
                byConnection.Add(connection, state);
            }
            if (state.Outstanding >= MaxOutstandingPerConnection) { return null; }

            TransferIdentity identity = new TransferIdentity(fileType, filePath);
            // One outstanding lease per exact transfer. Retries use FindPending.
            if (state.Pending.ContainsKey(identity)) { return null; }

            // Never replace a live lease after uint wraparound, and reserve
            // Protocol.AllLeases for the global revocation control message.
            uint id;
            do { id = nextId++; }
            while (id == Protocol.AllLeases || byId.ContainsKey(id));

            byte[] key = new byte[Protocol.KeyLength];
            RandomNumberGenerator.Fill(key);

            Lease lease = new Lease
            {
                Id = id,
                Connection = connection,
                FilePath = filePath,
                FileType = fileType,
                ContentKey = key,
                State = LeaseState.Issued,
                IssuedAt = Timing.TotalTime,
                LastGrantAt = 0.0
            };

            byId.Add(id, lease);
            state.Owned.Add(id);
            state.Pending.Add(identity, lease);
            state.Outstanding++;
            return lease;
        }

        internal Lease FindPending(NetworkConnection connection, FileTransferType fileType, string filePath)
        {
            if (connection == null || filePath == null ||
                !byConnection.TryGetValue(connection, out ConnectionState state)) { return null; }
            return state.Pending.TryGetValue(new TransferIdentity(fileType, filePath), out Lease lease)
                ? lease : null;
        }

        internal Lease Arm(NetworkConnection connection, uint id)
        {
            if (!byId.TryGetValue(id, out Lease lease) ||
                !ReferenceEquals(lease.Connection, connection) || lease.Armed || lease.Consumed)
            {
                return null;
            }
            lease.State |= LeaseState.Armed;
            return lease;
        }

        internal void MarkGranted(Lease lease) => lease.LastGrantAt = Timing.TotalTime;

        internal void MarkConsumed(Lease lease)
        {
            if (lease == null || lease.Consumed) { return; }
            if (!byId.TryGetValue(lease.Id, out Lease live) || !ReferenceEquals(live, lease)) { return; }

            lease.State |= LeaseState.Consumed;
            ConnectionState state = byConnection[lease.Connection];
            TransferIdentity identity = new TransferIdentity(lease.FileType, lease.FilePath);
            if (state.Pending.TryGetValue(identity, out Lease pending) && ReferenceEquals(pending, lease))
            {
                state.Pending.Remove(identity);
            }
            state.Outstanding--;
        }

        internal void Revoke(NetworkConnection connection)
        {
            if (connection == null || !byConnection.TryGetValue(connection, out ConnectionState state)) { return; }
            expired.Clear();
            expired.AddRange(state.Owned);
            Forget(expired);
        }

        internal void Sweep(double retentionSeconds)
        {
            double now = Timing.TotalTime;
            double armedRetentionSeconds = retentionSeconds * ArmedGraceMultiplier;
            expired.Clear();

            // Time-based expiry is global; pending-transfer lookups are not.
            foreach (Lease lease in byId.Values)
            {
                double age = now - lease.IssuedAt;
                if (lease.Consumed || !lease.Armed)
                {
                    if (age > retentionSeconds) { expired.Add(lease.Id); }
                    continue;
                }
                if (age > armedRetentionSeconds) { expired.Add(lease.Id); }
            }

            foreach (uint id in expired) { Diagnostics.Note($"lease {id} dropped"); }
            Forget(expired);
        }

        internal void RevokeAll()
        {
            foreach (Lease lease in byId.Values)
            {
                CryptographicOperations.ZeroMemory(lease.ContentKey);
            }
            byId.Clear();
            byConnection.Clear();
            expired.Clear();
        }

        private void Forget(List<uint> ids)
        {
            foreach (uint id in ids)
            {
                if (!byId.Remove(id, out Lease lease)) { continue; }

                if (byConnection.TryGetValue(lease.Connection, out ConnectionState state))
                {
                    state.Owned.Remove(id);
                    if (!lease.Consumed)
                    {
                        TransferIdentity identity = new TransferIdentity(lease.FileType, lease.FilePath);
                        if (state.Pending.TryGetValue(identity, out Lease pending) && ReferenceEquals(pending, lease))
                        {
                            state.Pending.Remove(identity);
                        }
                        state.Outstanding--;
                    }
                    if (state.Owned.Count == 0) { byConnection.Remove(lease.Connection); }
                }

                CryptographicOperations.ZeroMemory(lease.ContentKey);
            }
        }
    }
}
