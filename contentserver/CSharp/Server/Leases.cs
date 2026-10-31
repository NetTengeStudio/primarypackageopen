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
        Armed = 1,
        Consumed = 2
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

        private readonly struct TransferKey : IEquatable<TransferKey>
        {
            internal readonly NetworkConnection Connection;
            internal readonly FileTransferType Type;
            internal readonly string Path;

            internal TransferKey(NetworkConnection connection, FileTransferType type, string path)
            {
                Connection = connection;
                Type = type;
                Path = path;
            }

            public bool Equals(TransferKey other)
                => EqualityComparer<NetworkConnection>.Default.Equals(Connection, other.Connection)
                   && Type == other.Type && string.Equals(Path, other.Path, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is TransferKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(Connection, Type, StringComparer.Ordinal.GetHashCode(Path));
        }

        private readonly Dictionary<uint, Lease> byId = new Dictionary<uint, Lease>();
        private readonly Dictionary<TransferKey, Lease> pending = new Dictionary<TransferKey, Lease>();
        private readonly Dictionary<NetworkConnection, int> outstanding = new Dictionary<NetworkConnection, int>();
        private readonly List<uint> expired = new List<uint>();
        private uint nextId = 1;

        internal IEnumerable<Lease> All => byId.Values;

        internal int Outstanding(NetworkConnection connection)
            => connection is not null && outstanding.TryGetValue(connection, out int count) ? count : 0;

        internal Lease Issue(NetworkConnection connection, FileTransferType fileType, string filePath)
        {
            if (connection is null || string.IsNullOrEmpty(filePath)) { return null; }
            TransferKey key = new TransferKey(connection, fileType, filePath);
            if (pending.TryGetValue(key, out Lease existing)) { return existing; }
            int active = Outstanding(connection);
            if (active >= MaxOutstandingPerConnection) { return null; }

            uint id;
            do
            {
                id = nextId++;
            }
            while (id == Protocol.AllLeases || byId.ContainsKey(id));

            byte[] keyMaterial = new byte[Protocol.KeyLength];
            RandomNumberGenerator.Fill(keyMaterial);

            Lease lease = new Lease
            {
                Id = id,
                Connection = connection,
                FilePath = filePath,
                FileType = fileType,
                ContentKey = keyMaterial,
                State = LeaseState.Issued,
                IssuedAt = Timing.TotalTime,
                LastGrantAt = 0.0
            };

            byId.Add(id, lease);
            pending.Add(key, lease);
            outstanding[connection] = active + 1;
            return lease;
        }

        internal Lease FindPending(NetworkConnection connection, FileTransferType fileType, string filePath)
        {
            if (connection is null || string.IsNullOrEmpty(filePath)) { return null; }
            return pending.TryGetValue(new TransferKey(connection, fileType, filePath), out Lease lease)
                ? lease : null;
        }

        internal Lease Arm(NetworkConnection connection, uint id)
        {
            if (connection is null || !byId.TryGetValue(id, out Lease lease) ||
                lease.Connection != connection || lease.Armed || lease.Consumed) { return null; }
            lease.State |= LeaseState.Armed;
            return lease;
        }

        internal void MarkGranted(Lease lease) => lease.LastGrantAt = Timing.TotalTime;

        internal void MarkConsumed(Lease lease)
        {
            if (lease.Consumed) { return; }
            lease.State |= LeaseState.Consumed;
            pending.Remove(new TransferKey(lease.Connection, lease.FileType, lease.FilePath));
            Decrement(lease.Connection);
        }

        private void Decrement(NetworkConnection connection)
        {
            if (!outstanding.TryGetValue(connection, out int count)) { return; }
            if (count <= 1) { outstanding.Remove(connection); }
            else { outstanding[connection] = count - 1; }
        }

        internal void Revoke(NetworkConnection connection)
        {
            if (connection is null) { return; }
            expired.Clear();
            foreach (Lease lease in byId.Values)
            {
                if (lease.Connection == connection) { expired.Add(lease.Id); }
            }
            Forget(expired);
        }

        internal void Sweep(double retentionSeconds)
        {
            double now = Timing.TotalTime;
            double armedRetentionSeconds = retentionSeconds * ArmedGraceMultiplier;
            expired.Clear();
            foreach (Lease lease in byId.Values)
            {
                double age = now - lease.IssuedAt;
                if (age <= (lease.Armed && !lease.Consumed ? armedRetentionSeconds : retentionSeconds)) { continue; }
                expired.Add(lease.Id);
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
            pending.Clear();
            outstanding.Clear();
            expired.Clear();
        }

        private void Forget(List<uint> ids)
        {
            for (int index = 0; index < ids.Count; index++)
            {
                uint id = ids[index];
                if (!byId.Remove(id, out Lease lease)) { continue; }
                pending.Remove(new TransferKey(lease.Connection, lease.FileType, lease.FilePath));
                if (!lease.Consumed) { Decrement(lease.Connection); }
                CryptographicOperations.ZeroMemory(lease.ContentKey);
            }
            ids.Clear();
        }
    }
}
