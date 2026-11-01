using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

internal sealed class InteractionRecovery {
    public sealed class State {
        public List<PrivateMessage> Outbox { get; set; } = new();
        public Dictionary<string, string> Drafts { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Seen { get; set; } = new(StringComparer.Ordinal);
    }
    private readonly AtomicStore<State> store;
    public State Data { get; }
    public InteractionRecovery(string endpoint, string account) {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint + "\0" + account)));
        store = new AtomicStore<State>(ModAssets.Resolve("Data/Interactions/client/" + key + ".json"), Validate);
        Data = store.Load(() => new State(), error => ModLog.Error("Interaction client state recovered: " + error.Message));
    }
    private static void Validate(State state) {
        if (state == null || state.Outbox == null || state.Outbox.Count > 32 || state.Drafts == null || state.Drafts.Count > 256 || state.Seen == null || state.Seen.Count > 10000) { throw new InvalidDataException("Invalid interaction client state"); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.Outbox) {
            if (entry == null || !Guid.TryParseExact(entry.Id, "N", out _) || !ids.Add(entry.Id) || entry.Status != MessageStatus.Queued || entry.Kind is not (MessageKind.Private or MessageKind.Support) ||
                !InteractionProtocol.ValidText(entry.Text, InteractionProtocol.MaxMessageLength) || entry.From == null || entry.From.Length > 128 || entry.To == null || entry.To.Length > 128 || entry.Thread == null || entry.Thread.Length > 128) { throw new InvalidDataException("Invalid queued message"); }
        }
        foreach (var draft in state.Drafts) { if (draft.Key.Length > 160 || draft.Value == null || draft.Value.Length > 512) { throw new InvalidDataException("Invalid message draft"); } }
        foreach (string id in state.Seen) { if (id == null || id.Length > 164) { throw new InvalidDataException("Invalid message receipt"); } }
    }
    public void SaveOutbox(List<PrivateMessage> values) {
        var previous = Data.Outbox;
        Data.Outbox = new List<PrivateMessage>(values);
        try { store.Save(Data); } catch { Data.Outbox = previous; throw; }
    }
    public void SaveViews(Dictionary<string, string> drafts, HashSet<string> seen) {
        var previousDrafts = Data.Drafts; var previousSeen = Data.Seen;
        Data.Drafts = new Dictionary<string, string>(drafts, StringComparer.Ordinal);
        Data.Seen = new HashSet<string>(seen, StringComparer.Ordinal);
        try { store.Save(Data); } catch { Data.Drafts = previousDrafts; Data.Seen = previousSeen; throw; }
    }
}
