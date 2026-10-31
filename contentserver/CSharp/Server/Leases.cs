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

        private readonly Dictionary<uint, Lease> byId = new Dictionary<uint, Lease>();
        private readonly List<uint> expired = new List<uint>();
        private uint nextId = 1;

        internal IEnumerable<Lease> All => byId.Values;

        internal int Outstanding(NetworkConnection connection)
        {
            int count = 0;
            foreach (Lease lease in byId.Values)
            {
                if (lease.Connection == connection && !lease.Consumed) { count++; }
            }
            return count;
        }


        internal Lease Issue(NetworkConnection connection, FileTransferType fileType, string filePath)
        {
            if (Outstanding(connection) >= MaxOutstandingPerConnection) { return null; }

            uint id = nextId++;
            if (id == Protocol.AllLeases) { id = nextId++; }

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

            byId[id] = lease;
            return lease;
        }

        internal Lease FindPending(NetworkConnection connection, FileTransferType fileType, string filePath)
        {
            foreach (Lease lease in byId.Values)
            {
                if (lease.Consumed) { continue; }
                if (lease.Connection == connection && lease.FileType == fileType && lease.FilePath == filePath)
                {
                    return lease;
                }
            }
            return null;
        }

        internal Lease Arm(NetworkConnection connection, uint id)
        {
            if (!byId.TryGetValue(id, out Lease lease) || lease.Connection != connection || lease.Armed) { return null; }
            lease.State |= LeaseState.Armed;
            return lease;
        }

        internal void MarkGranted(Lease lease) => lease.LastGrantAt = Timing.TotalTime;

        internal void MarkConsumed(Lease lease) => lease.State |= LeaseState.Consumed;

        internal void Revoke(NetworkConnection connection)
        {
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
                if (lease.Consumed || !lease.Armed)
                {
                    if (age > retentionSeconds) { expired.Add(lease.Id); }
                    continue;
                }

                if (age > armedRetentionSeconds) { expired.Add(lease.Id); }
            }

            foreach (uint id in expired)
            {
                Diagnostics.Note($"lease {id} dropped");
            }

            Forget(expired);
        }

        internal void RevokeAll()
        {
            foreach (Lease lease in byId.Values)
            {
                CryptographicOperations.ZeroMemory(lease.ContentKey);
            }
            byId.Clear();
        }

        private void Forget(List<uint> ids)
        {
            foreach (uint id in ids)
            {
                if (!byId.TryGetValue(id, out Lease lease)) { continue; }
                CryptographicOperations.ZeroMemory(lease.ContentKey);
                byId.Remove(id);
            }
        }
    }
}
