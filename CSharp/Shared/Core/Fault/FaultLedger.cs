using System;
using System.Collections.Generic;

namespace PrimaryPackage.Core.Fault;

public enum FaultLevel : byte {
    Info,
    Warning,
    Error
}

public sealed class EntityFault {
    public readonly FaultBreaker Breaker = new FaultBreaker();
    public string Label;
    internal long EnteredFaults;
}

public sealed class FaultSite {
    internal const int SignatureLimit = 32;

    public readonly string Id;
    public readonly FaultPolicy Policy;
    public readonly bool PerEntity;
    public readonly FaultBreaker Breaker = new FaultBreaker();
    internal readonly Dictionary<object, EntityFault> Entities;
    internal readonly Dictionary<string, long> Signatures = new Dictionary<string, long>();
    internal long pendingRepeats;
    internal double lastSummary;
    internal string lastSignature;

    public long Contained { get; internal set; }

    public FaultSite(string id, FaultPolicy policy, bool perEntity) {
        Id = id;
        Policy = policy;
        PerEntity = perEntity;
        if (perEntity) { Entities = new Dictionary<object, EntityFault>(ReferenceEqualityComparer.Instance); }
    }

    public bool Active => PerEntity ? Entities.Count > 0 : Breaker.State != BreakerState.Closed;

    public string LastSignature => lastSignature;
}

public readonly struct FaultOutcome {
    public readonly bool Tripped;
    public readonly bool Reported;

    public FaultOutcome(bool tripped, bool reported) {
        Tripped = tripped;
        Reported = reported;
    }
}

public sealed class FaultLedger {
    private const int EntityLimit = 256;
    private const int TraceFrames = 7;
    private const double SummarySeconds = 30.0;

    private readonly Dictionary<string, FaultSite> sites = new Dictionary<string, FaultSite>(StringComparer.Ordinal);
    private readonly List<FaultSite> order = new List<FaultSite>();
    private readonly Action<FaultLevel, string> sink;
    private readonly RateLimiter limiter = new RateLimiter(24.0, 2.0);
    private readonly object gate = new object();
    private int globalActive;
    private int entityActive;

    public FaultLedger(Action<FaultLevel, string> sink) {
        this.sink = sink;
    }

    public int GlobalActive => globalActive;

    public int EntityActive => entityActive;

    public long TotalContained { get; private set; }

    public IReadOnlyList<FaultSite> Sites => order;

    public FaultSite Register(string id, FaultPolicy policy, bool perEntity) {
        lock (gate) {
            if (sites.TryGetValue(id, out FaultSite existing)) { return existing; }
            FaultSite site = new FaultSite(id, policy, perEntity);
            sites.Add(id, site);
            order.Add(site);
            return site;
        }
    }

    public bool Enter(FaultSite site, object entity, double now) => Enter(site, entity, now, out _);

    public bool Enter(FaultSite site, object entity, double now, out bool recovered) {
        recovered = false;
        if (site.PerEntity) {
            if (entity == null || site.Entities.Count == 0) { return true; }
            if (!site.Entities.TryGetValue(entity, out EntityFault fault)) { return true; }
            bool survived = fault.EnteredFaults == fault.Breaker.Faults;
            bool probing = fault.Breaker.State == BreakerState.Probing;
            bool run = fault.Breaker.Enter(now);
            if (run && survived && fault.Breaker.State == BreakerState.Closed && fault.Breaker.Level == 0 &&
                (probing || now - fault.Breaker.LastFault > site.Policy.WindowSeconds)) {
                site.Entities.Remove(entity);
                entityActive--;
                recovered = true;
            }
            if (run) { fault.EnteredFaults = fault.Breaker.Faults; }
            return run;
        }
        bool wasActive = site.Breaker.State != BreakerState.Closed;
        bool allowed = site.Breaker.Enter(now);
        Track(site, wasActive);
        return allowed;
    }

    public bool HasEntityFault(FaultSite site, object entity) {
        if (entity == null || !site.PerEntity || entityActive == 0) { return false; }
        lock (gate) { return site.Entities.ContainsKey(entity); }
    }

    public FaultOutcome Record(FaultSite site, object entity, string label, Exception error, double now) {
        lock (gate) {
            TotalContained++;
            site.Contained++;
            bool tripped;
            string scope = "";
            double cooldown = 0.0;
            if (site.PerEntity && entity != null) {
                if (!site.Entities.TryGetValue(entity, out EntityFault fault)) {
                    if (site.Entities.Count >= EntityLimit) { EvictOldest(site); }
                    fault = new EntityFault { Label = label };
                    site.Entities.Add(entity, fault);
                    entityActive++;
                }
                tripped = fault.Breaker.Fault(now, site.Policy);
                if (tripped) { cooldown = fault.Breaker.Cooldown(site.Policy); }
                scope = string.IsNullOrEmpty(fault.Label) ? "" : " [" + fault.Label + "]";
            } else {
                bool wasActive = site.Breaker.State != BreakerState.Closed;
                tripped = site.Breaker.Fault(now, site.Policy);
                Track(site, wasActive);
                if (tripped) { cooldown = site.Breaker.Cooldown(site.Policy); }
            }

            string signature = FaultSignature.Of(error);
            site.lastSignature = signature;
            bool first = !site.Signatures.TryGetValue(signature, out long seen);
            if (first && site.Signatures.Count >= FaultSite.SignatureLimit) {
                site.Signatures.Clear();
            }
            site.Signatures[signature] = seen + 1;

            bool reported = false;
            if (first) {
                reported = Emit(FaultLevel.Error, now,
                    $"{site.Id}{scope} fault contained: {signature} | {FaultSignature.Trace(error, TraceFrames)}");
            } else {
                site.pendingRepeats++;
                if (now - site.lastSummary >= SummarySeconds && site.pendingRepeats > 0) {
                    site.lastSummary = now;
                    reported = Emit(FaultLevel.Warning, now,
                        $"{site.Id}: {site.pendingRepeats} more fault(s) contained, latest: {signature}");
                    site.pendingRepeats = 0;
                }
            }
            if (tripped) {
                Emit(FaultLevel.Warning, now,
                    $"{site.Id}{scope} paused for {cooldown:0.#}s after repeated faults, it will be probed again afterwards");
            }
            return new FaultOutcome(tripped, reported);
        }
    }

    public void ResetEntities() {
        lock (gate) {
            for (int i = 0; i < order.Count; i++) { order[i].Entities?.Clear(); }
            entityActive = 0;
        }
    }

    public void ResetAll() {
        lock (gate) {
            sites.Clear();
            order.Clear();
            globalActive = 0;
            entityActive = 0;
            TotalContained = 0;
        }
    }

    public void Describe(double now, List<string> lines) {
        lock (gate) {
            lines.Add($"faults contained: {TotalContained}, sites: {order.Count}");
            for (int i = 0; i < order.Count; i++) {
                FaultSite site = order[i];
                if (site.Contained == 0 && !site.Active) { continue; }
                string state = site.PerEntity
                    ? $"{site.Entities.Count} entity record(s)"
                    : site.Breaker.State.ToString().ToLowerInvariant();
                lines.Add($"  {site.Id}: contained {site.Contained}, {state}"
                    + (site.PerEntity ? "" : $", skipped {site.Breaker.Skipped}, trips {site.Breaker.Trips}")
                    + (site.lastSignature == null ? "" : $", last: {site.lastSignature}"));
                if (!site.PerEntity) { continue; }
                foreach (KeyValuePair<object, EntityFault> pair in site.Entities) {
                    FaultBreaker b = pair.Value.Breaker;
                    string until = b.State == BreakerState.Open ? $", paused {Math.Max(0.0, b.OpenUntil - now):0.#}s more" : "";
                    lines.Add($"    {pair.Value.Label}: {b.Faults} fault(s), {b.State.ToString().ToLowerInvariant()}{until}");
                }
            }
        }
    }

    private bool Emit(FaultLevel level, double now, string text) {
        if (!limiter.TryTake(now, out int suppressed)) { return false; }
        if (suppressed > 0) { sink?.Invoke(FaultLevel.Warning, $"[fault] {suppressed} message(s) suppressed by the rate limit"); }
        sink?.Invoke(level, "[fault] " + text);
        return true;
    }

    private void Track(FaultSite site, bool wasActive) {
        bool active = site.Breaker.State != BreakerState.Closed;
        if (active == wasActive) { return; }
        globalActive += active ? 1 : -1;
    }

    private void EvictOldest(FaultSite site) {
        object oldest = null;
        double stamp = double.MaxValue;
        foreach (KeyValuePair<object, EntityFault> pair in site.Entities) {
            if (pair.Value.Breaker.LastFault < stamp) {
                stamp = pair.Value.Breaker.LastFault;
                oldest = pair.Key;
            }
        }
        if (oldest != null) {
            site.Entities.Remove(oldest);
            entityActive--;
        }
    }
}
