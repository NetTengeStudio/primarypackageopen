using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;

namespace INet;

public sealed class RateLimit {
    private const int PruneThreshold = 64;

    private readonly Dictionary<ulong, double> last = new Dictionary<ulong, double>();
    private readonly HashSet<ulong> live = new();
    private readonly List<ulong> stale = new();
    private double nextPrune;

    public RateLimit(double intervalSeconds) => Interval = intervalSeconds;

    public double Interval { get; set; }

    public double Remaining(Client sender) {
        if (sender == null || Interval <= 0.0) { return 0.0; }
        if (!last.TryGetValue(sender.SessionId, out double previous)) { return 0.0; }
        double remaining = Interval - (Timing.TotalTime - previous);
        return remaining > 0.0 ? remaining : 0.0;
    }

    public void Record(Client sender) {
        if (sender == null) { return; }
        last[sender.SessionId] = Timing.TotalTime;
        Prune();
    }

    public bool Allow(Client sender) {
        if (sender == null) { return false; }
        if (Remaining(sender) > 0.0) { return false; }
        Record(sender);
        return true;
    }

    public void Forget(Client sender) {
        if (sender != null) { last.Remove(sender.SessionId); }
    }

    public void Reset() { last.Clear(); live.Clear(); stale.Clear(); nextPrune = 0; }

    private void Prune() {
        if (GameMain.Server == null || last.Count < PruneThreshold || Timing.TotalTime < nextPrune) { return; }
        nextPrune = Timing.TotalTime + Interval;
        live.Clear(); stale.Clear();
        IReadOnlyList<Client> connected = GameMain.Server.ConnectedClients;
        for (int i = 0; i < connected.Count; i++) { live.Add(connected[i].SessionId); }
        foreach (KeyValuePair<ulong, double> pair in last) {
            if (!live.Contains(pair.Key)) { stale.Add(pair.Key); }
        }
        for (int i = 0; i < stale.Count; i++) { last.Remove(stale[i]); }
        live.Clear(); stale.Clear();
    }
}
