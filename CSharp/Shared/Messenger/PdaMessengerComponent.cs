using System.Collections.Generic;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Messenger {
    public partial class PdaMessengerComponent : ItemComponent, IClientSerializable, IServerSerializable {
        public const int MaxMessageLen = 240, MaxNameLen = 40, NumberDigits = 4, MaxContacts = 64, MaxHistoryPerPair = 120;
        public const double SendCooldownSec = 0.4;
        public const int MaxGroupMembers = 16;
        public const int GroupNumberFloor = 9000;
        public static bool IsGroupNumber(string num) =>
            !string.IsNullOrEmpty(num) && num.Length == NumberDigits && num[0] == '9';
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string DeviceNumber { get; set; } = "";
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string ContactsData { get; set; } = "";
        [Serialize(false, IsPropertySaveable.No)]
        public bool OpenSignal { get; set; }
        [Serialize("", IsPropertySaveable.No)]
        public string SrpTag { get; set; } = "";
        public readonly record struct SendReq(string To, string Text) : ItemComponent.IEventData;
        public readonly record struct ContactReq(string Num, string Name) : ItemComponent.IEventData;
        public readonly record struct GroupReq(string Name, string Members) : ItemComponent.IEventData;
        public readonly record struct OpenReq : ItemComponent.IEventData;
        public readonly record struct HistReq(string Peer) : ItemComponent.IEventData;
        public readonly record struct DeliverEvt(string From, string To, string Text, bool AsSender) : ItemComponent.IEventData;
        public readonly record struct StateEvt : ItemComponent.IEventData;
        public readonly record struct DirEvt : ItemComponent.IEventData;
        public readonly record struct HistEvt(string Peer) : ItemComponent.IEventData;
        public PdaMessengerComponent(Item item, ContentXElement element) : base(item, element) {
            CanBeSelected = true;
            InitProjSpecific();
        }
        partial void InitProjSpecific();
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<SendReq>(data, out _) || TryExtractEventData<ContactReq>(data, out _)
            || TryExtractEventData<GroupReq>(data, out _)
            || TryExtractEventData<OpenReq>(data, out _) || TryExtractEventData<HistReq>(data, out _)
            || TryExtractEventData<DeliverEvt>(data, out _) || TryExtractEventData<StateEvt>(data, out _)
            || TryExtractEventData<DirEvt>(data, out _) || TryExtractEventData<HistEvt>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => ClientEventWriteProjSpecific(msg, extraData);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData);
        public readonly record struct Contact(string Number, string Name);
        private const char FieldSep = '\x1f', RecordSep = '\x1e';
        private static bool TrySplitRecord(string rec, out string number, out string rest) {
            int sep = rec.IndexOf(FieldSep);
            number = sep < 0 ? rec : rec.Substring(0, sep);
            rest = sep < 0 ? "" : rec.Substring(sep + 1);
            return sep >= 0;
        }
        public List<Contact> GetContacts() {
            var list = new List<Contact>();
            if (string.IsNullOrEmpty(ContactsData)) { return list; }
            foreach (string rec in ContactsData.Split(RecordSep)) {
                if (string.IsNullOrEmpty(rec)) { continue; }
                if (!TrySplitRecord(rec, out string num, out string name)) { continue; }
                if (!string.IsNullOrEmpty(num)) { list.Add(new Contact(num, name)); }
            }
            return list;
        }
        public static string UpsertContactData(string existing, string number, string name) {
            number = NormalizeNumber(number);
            if (string.IsNullOrEmpty(number)) { return existing; }
            name = Sanitize(name, MaxNameLen);
            var records = new List<string>();
            bool replaced = false;
            if (!string.IsNullOrEmpty(existing)) {
                foreach (string rec in existing.Split(RecordSep)) {
                    if (string.IsNullOrEmpty(rec)) { continue; }
                    TrySplitRecord(rec, out string recNum, out _);
                    if (recNum == number) { records.Add(number + FieldSep + name); replaced = true; }
                    else { records.Add(rec); }
                }
            }
            if (!replaced) {
                if (records.Count >= MaxContacts) { return existing; }
                records.Add(number + FieldSep + name);
            }
            return string.Join(RecordSep.ToString(), records);
        }
        public static string NormalizeNumber(string raw) {
            if (string.IsNullOrEmpty(raw)) { return ""; }
            var sb = new StringBuilder(NumberDigits);
            foreach (char c in raw) {
                if (char.IsDigit(c)) { sb.Append(c); }
                if (sb.Length >= NumberDigits) { break; }
            }
            return sb.ToString();
        }
        public static string Sanitize(string text, int maxLen) {
            if (string.IsNullOrEmpty(text)) { return ""; }
            RichTextData.GetRichTextData(text, out string stripped);
            var sb = new StringBuilder(stripped.Length);
            foreach (char c in stripped) {
                if (c is '\r' or '\n' or '\t') { sb.Append(' '); continue; }
                if (c == '‖' || c == FieldSep || c == RecordSep || char.IsControl(c)) { continue; }
                sb.Append(c);
            }
            string result = sb.ToString().Trim();
            return result.Length > maxLen ? result.Substring(0, maxLen).TrimEnd() : result;
        }
    }
}
