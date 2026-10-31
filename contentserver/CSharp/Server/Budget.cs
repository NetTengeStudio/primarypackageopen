using System;
using System.Collections.Generic;
using Barotrauma;

namespace SubSeal.Server
{
    
    internal sealed class Budget
    {
        private sealed class Bucket
        {
            internal double Tokens;
            internal double UpdatedAt;
            internal readonly HashSet<string> Granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly double capacity;
        private readonly double refillPerSecond;
        private readonly double retentionSeconds;

        private readonly Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly List<string> stale = new List<string>();

        internal Budget(double capacity, double refillPerSecond, double retentionSeconds)
        {
            this.capacity = capacity;
            this.refillPerSecond = refillPerSecond;
            this.retentionSeconds = retentionSeconds;
        }

        internal bool TryTake(string identity, string filePath)
        {
            if (string.IsNullOrEmpty(identity)) { return false; }

            double now = Timing.TotalTime;

            if (!buckets.TryGetValue(identity, out Bucket bucket))
            {
                bucket = new Bucket { Tokens = capacity, UpdatedAt = now };
                buckets[identity] = bucket;
            }
            else
            {
                double elapsed = now - bucket.UpdatedAt;
                if (elapsed > 0.0) { bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsed * refillPerSecond); }
            }

            bucket.UpdatedAt = now;

            if (bucket.Granted.Contains(filePath)) { return true; }
            if (bucket.Tokens < 1.0) { return false; }

            bucket.Tokens -= 1.0;
            bucket.Granted.Add(filePath);
            return true;
        }

        internal void Sweep()
        {
            double now = Timing.TotalTime;
            stale.Clear();

            foreach (KeyValuePair<string, Bucket> entry in buckets)
            {
                if (now - entry.Value.UpdatedAt < retentionSeconds) { continue; }
                stale.Add(entry.Key);
            }

            foreach (string identity in stale)
            {
                buckets.Remove(identity);
            }
        }

        internal void Clear() => buckets.Clear();
    }
}
