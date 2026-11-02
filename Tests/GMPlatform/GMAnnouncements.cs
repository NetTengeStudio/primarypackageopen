using System.Globalization;
using System.Text.Json;
using PrimaryPackage.Interactions;

internal static partial class Program {
    private static void Announcements(string root) {
        var draft = new GMMessageDraft { Text = "Проверка связи", Target = "account-2", Sender = GMMessageSender.Custom, CustomSender = "Капитан", R = "12", G = "34", B = "56", A = "78", Hold = "", FadeIn = ".", FadeOut = "-" };
        draft.Validate();
        var message = draft.Create();
        Check(message.Hold == 5 && message.ResolveSender("server", "self", null) == "Капитан" && message.R == 12 && message.A == 78, "hidden toast timings do not block ordinary messages; custom name and RGBA preserved");
        draft.Format = GMAnnouncement.PlainMsg;
        message = draft.Create(); Check(message.NativeFormat == 3 && message.ResolveSender(null, null, null) == "", "ordinary msg uses native Server format and never a sender name");
        draft.Format = GMAnnouncement.Toast;
        Reject<ArgumentException>(() => draft.Create(), "unfinished toast timings reject sending, not saving");
        draft.Hold = "3,5"; draft.FadeIn = "0.1"; draft.FadeOut = "0.9";
        message = draft.Create(); Check(message.Hold == 3.5f && message.ResolveSender(null, null, null) == "", "toast parses decimal comma and does not impersonate a native chat message");
        draft.R = ""; draft.Validate(); Reject<ArgumentException>(() => draft.Create(), "unfinished RGBA remains a draft and cannot be sent"); draft.R = "256";
        Reject<ArgumentException>(() => draft.Create(), "RGBA cannot overflow a byte"); draft.R = "12";
        foreach (string bad in new[] { "NaN", "Infinity", "61", "0.2", "-1" }) { draft.Hold = bad; Reject<ArgumentException>(() => draft.Create(), "invalid toast hold " + bad); }
        draft.Hold = "3.5";
        draft.Format = 11; draft.Sender = GMMessageSender.Player; draft.SenderAccount = "player-1";
        message = draft.Create(); Check(message.ResolveSender("Server", "GMName", "PlayerName") == "PlayerName", "player sender uses the authoritative roster name");
        Check(message.NativeText("PlayerName") == "PlayerName:\nПроверка связи" && message.NativeText("") == message.Text && message.NativeFormat == 11, "native popup ignores the separate name field, so shared presentation formats its sender as the first line");
        Reject<ArgumentException>(() => message.ResolveSender("Server", "GMName", null), "missing player sender never silently becomes GM");
        message.Sender = GMMessageSender.Server; Check(message.ResolveSender("Server", "self", null) == "Server", "server name resolved by authority");
        message.Sender = GMMessageSender.Self; Check(message.ResolveSender("Server", "self", null) == "self", "self name resolved by authority");
        message.Sender = GMMessageSender.None; Check(message.ResolveSender(null, null, null) == "", "explicit empty sender supported");
        message.Sender = (GMMessageSender)128; Reject<ArgumentException>(message.Validate, "unknown sender choice rejected");
        message.Sender = GMMessageSender.Custom;
        foreach (string bad in new[] { "", "<color=red>GM</color>", "A\nB", "\ud800" }) { message.CustomSender = bad; Reject<ArgumentException>(message.Validate, "invalid custom sender"); }
        message.CustomSender = "Капитан"; message.Validate();
        var receipt = new GMAnnouncementReceipt { Id = message.Id, Sender = "Капитан", Recipients = 4, Timestamp = 1700000000000 };
        var state = new GMMessageState { Draft = draft.Copy() }; Check(state.Record(message, receipt), "confirmed message recorded");
        message.Text = "later mutation"; receipt.Sender = "later mutation";
        Check(state.History[0].Message.Text == "Проверка связи" && state.History[0].Receipt.Sender == "Капитан", "confirmed history owns both request and receipt");
        Check(!state.Record(state.History[0].Message, state.History[0].Receipt), "duplicate confirmation does not duplicate history");
        var repeat = GMMessageDraft.From(state.History[0].Message).Create();
        Check(repeat.Id != state.History[0].Message.Id && repeat.Text == state.History[0].Message.Text && repeat.R == 12 && repeat.Sender == GMMessageSender.Custom, "intentional repeat preserves message options and uses a fresh request identity");
        var rejected = new GMAnnouncementReceipt { Id = repeat.Id, Error = "Denied", Timestamp = 1700000000000 };
        Reject<ArgumentException>(() => state.Record(repeat, rejected), "server rejection cannot enter confirmed history");
        rejected.Error = ""; rejected.Id = Guid.NewGuid(); Reject<ArgumentException>(() => state.Record(repeat, rejected), "receipt must refer to its exact request");
        for (int i = 0; i < 50; i++) { var next = repeat.Copy(); next.Id = Guid.NewGuid(); next.Text = i.ToString(CultureInfo.InvariantCulture); state.Record(next, new() { Id = next.Id, Timestamp = 1700000000000 + i, Recipients = 1 }); }
        Check(state.History.Count == GMMessageState.HistoryCapacity && state.History[0].Message.Text == "18" && state.History[^1].Message.Text == "49", "confirmed history is bounded and evicts oldest");
        var copy = state.Copy(); copy.Draft.Text = "other draft"; copy.History[0].Message.Text = "other message";
        Check(state.Draft.Text != copy.Draft.Text && state.History[0].Message.Text == "18", "recovery snapshots do not share mutable drafts or history entries");
        state.Draft.R = ""; state.Draft.Hold = "-"; state.Draft.CustomSender = "";
        var decoded = JsonSerializer.Deserialize<GMMessageState>(JsonSerializer.Serialize(state)); decoded.Validate();
        Check(decoded.Draft.R == "" && decoded.Draft.Hold == "-" && decoded.History[^1].Message.Text == "49", "persistent state roundtrips incomplete input and confirmed history");
        var legacy = JsonSerializer.Deserialize<GMMessageState>("{}"); legacy.Validate(); Check(legacy.Draft.Format == 3 && legacy.History.Count == 0, "missing persisted message state receives compatible defaults");
        decoded.History[0].Receipt.Error = "denied"; Reject<InvalidDataException>(decoded.Validate, "corrupt persisted history rejected");

        // Real AtomicStore, ordinary workspace directory; no special temporary-directory ACLs.
        string directory = Path.Combine(root, "Tests", "obj", "gm-message-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "draft.json");
        Directory.CreateDirectory(directory);
        try {
            var store = new AtomicStore<GMMessageState>(path, value => value.Validate()); store.Save(state);
            var second = state.Copy(); second.Draft.Text = "second"; store.Save(second);
            var loaded = new AtomicStore<GMMessageState>(path, value => value.Validate()).Load(() => new(), _ => Check(false, "unexpected recovery"));
            Check(loaded.Draft.Text == "second" && loaded.Draft.R == "" && loaded.History.Count == 32, "actual atomic store restores complete message state");
            File.WriteAllText(path, "corrupt"); int recovered = 0;
            loaded = new AtomicStore<GMMessageState>(path, value => value.Validate()).Load(() => new(), _ => recovered++);
            Check(recovered == 1 && loaded.Draft.Text == state.Draft.Text && loaded.History.Count == 32, "corrupt current message state recovers verified backup");
            var invalidState = state.Copy(); invalidState.History[0].Message.Text = "";
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(invalidState);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), Payload = payload }));
            loaded = new AtomicStore<GMMessageState>(path, value => value.Validate()).Load(() => new(), _ => recovered++);
            Check(recovered == 2 && loaded.History[0].Message.Text == "18", "semantic corruption with a valid checksum also falls back to the verified backup");
        } finally {
            foreach (string suffix in new[] { "", ".bak", ".pending", ".bak.pending" }) { File.Delete(path + suffix); }
            Directory.Delete(directory);
        }
    }
}
