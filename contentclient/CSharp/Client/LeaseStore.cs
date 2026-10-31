using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using SubSeal.Wire;

namespace SubSeal.Client
{
    internal sealed class LeaseStore
    {
        private readonly Dictionary<uint, byte[]> keys = new Dictionary<uint, byte[]>();

        internal void Hold(uint leaseId, ReadOnlySpan<byte> contentKey)
        {
            Release(leaseId);
            keys[leaseId] = contentKey.ToArray();
        }

        internal int Count => keys.Count;

        internal bool TryGet(uint leaseId, out byte[] contentKey) => keys.TryGetValue(leaseId, out contentKey);

        internal void Release(uint leaseId)
        {
            if (leaseId == Protocol.AllLeases) { ReleaseAll(); return; }
            if (!keys.TryGetValue(leaseId, out byte[] key)) { return; }
            CryptographicOperations.ZeroMemory(key);
            keys.Remove(leaseId);
        }

        internal void ReleaseAll()
        {
            foreach (byte[] key in keys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }
            keys.Clear();
        }
    }
}
