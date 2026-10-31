using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Messenger {
    public partial class PdaMessengerComponent {
        private static readonly Random Rng = new Random();
        private static readonly Dictionary<PairKey, List<(string from, string text)>> History = new Dictionary<PairKey, List<(string, string)>>();
        private static readonly Dictionary<string, double> LastSend = new Dictionary<string, double>();
        private static readonly Dictionary<(int session, byte op), double> LastQuery = new Dictionary<(int, byte), double>();
        private const double QueryCooldownSec = 0.5;
        private static object roundMarker;
        private sealed class GroupInfo {
            public readonly string Name;
            public readonly HashSet<string> Members;
            public GroupInfo(string name, HashSet<string> members) { Name = name; Members = members; }
        }
        private static readonly Dictionary<string, GroupInfo> Groups = new Dictionary<string, GroupInfo>();
        private static void RoundGate() {
            object lvl = Level.Loaded;
            if (!ReferenceEquals(lvl, roundMarker)) { History.Clear(); LastSend.Clear(); LastQuery.Clear(); Groups.Clear(); roundMarker = lvl; }
        }
        private static bool QueryThrottled(Client c, byte op) {
            var key = ((int)c.SessionId, op);
            double now = Timing.TotalTime;
            if (LastQuery.TryGetValue(key, out double last) && now - last < QueryCooldownSec) { return true; }
            LastQuery[key] = now;
            return false;
        }
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            if (GameMain.Server == null) { return; }
            string num = NormalizeNumber(DeviceNumber);
            if (string.IsNullOrEmpty(num) || IsGroupNumber(num)) { DeviceNumber = GenUnique(); }
        }
        public void ServerEventRead(IReadMessage msg, Client c) {
            RoundGate();
            byte op = msg.ReadByte();
            Character ch = c?.Character;
            if (ch == null || ch.Removed) { return; }
            if (item.GetRootInventoryOwner() != ch && !item.CanClientAccess(c)) { return; }
            string myNum = NormalizeNumber(DeviceNumber);
            switch (op) {
                case 1: {
                    string to = NormalizeNumber(msg.ReadString());
                    string text = Sanitize(msg.ReadString(), MaxMessageLen);
                    if (string.IsNullOrEmpty(myNum) || string.IsNullOrEmpty(to) || to == myNum || string.IsNullOrEmpty(text)) { return; }
                    string key = myNum + ">" + to;
                    double now = Timing.TotalTime;
                    if (LastSend.TryGetValue(key, out double last) && now - last < SendCooldownSec) { return; }
                    LastSend[key] = now;
                    if (IsGroupNumber(to)) {
                        if (!Groups.TryGetValue(to, out GroupInfo group) || !group.Members.Contains(myNum)) { return; }
                        string senderName = Sanitize(ch.Info?.Name ?? ch.Name ?? "", MaxNameLen);
                        if (string.IsNullOrEmpty(senderName)) { senderName = myNum; }
                        string vis = senderName + FieldSep + text;
                        Append(to, to, myNum, vis);
                        DeliverGroup(myNum, to, vis, group);
                    }
                    else {
                        Append(myNum, to, myNum, text);
                        Deliver(myNum, to, text);
                    }
                    break;
                }
                case 2: {
                    if (QueryThrottled(c, op)) { return; }
                    string num = NormalizeNumber(msg.ReadString());
                    string name = Sanitize(msg.ReadString(), MaxNameLen);
                    if (string.IsNullOrEmpty(num)) { return; }
                    ContactsData = UpsertContactData(ContactsData, num, name);
                    item.CreateServerEvent(this, new StateEvt());
                    break;
                }
                case 3:
                    if (QueryThrottled(c, op)) { return; }
                    item.CreateServerEvent(this, new StateEvt());
                    item.CreateServerEvent(this, new DirEvt());
                    break;
                case 4: {
                    if (QueryThrottled(c, op)) { return; }
                    string peer = NormalizeNumber(msg.ReadString());
                    item.CreateServerEvent(this, new HistEvt(peer));
                    break;
                }
                case 5: {
                    if (QueryThrottled(c, op)) { return; }
                    string name = Sanitize(msg.ReadString(), MaxNameLen);
                    string membersRaw = msg.ReadString() ?? "";
                    if (string.IsNullOrEmpty(myNum)) { return; }
                    var members = new HashSet<string> { myNum };
                    foreach (string part in membersRaw.Split(',')) {
                        if (members.Count >= MaxGroupMembers) { break; }
                        string num = NormalizeNumber(part);
                        if (!string.IsNullOrEmpty(num) && !IsGroupNumber(num)) { members.Add(num); }
                    }
                    if (members.Count < 2) { return; }
                    string groupNum = GenUniqueGroup();
                    if (groupNum == null) { return; }
                    Groups[groupNum] = new GroupInfo(string.IsNullOrEmpty(name)
                        ? PrimaryPackage.Economy.OrderText.Format("pp.order.messenger.default_group_name",
                            "Группа " + groupNum, ("[num]", groupNum))
                        : name, members);
                    item.CreateServerEvent(this, new DirEvt());
                    break;
                }
            }
        }
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            if (TryExtractEventData<DeliverEvt>(extraData, out var d)) {
                msg.WriteByte((byte)1);
                if (item.GetRootInventoryOwner() != c?.Character) { msg.WriteByte((byte)2); return; }
                if (d.AsSender) { msg.WriteByte((byte)1); msg.WriteString(d.From); msg.WriteString(d.To); msg.WriteString(d.Text); }
                else { msg.WriteByte((byte)0); msg.WriteString(d.To); msg.WriteString(d.From); msg.WriteString(d.Text); }
                return;
            }
            bool mine = item.GetRootInventoryOwner() == c?.Character;
            if (TryExtractEventData<StateEvt>(extraData, out _)) {
                msg.WriteByte((byte)2);
                msg.WriteBoolean(mine);
                if (mine) { msg.WriteString(DeviceNumber ?? ""); msg.WriteString(ContactsData ?? ""); }
                return;
            }
            if (TryExtractEventData<DirEvt>(extraData, out _)) {
                msg.WriteByte((byte)3);
                msg.WriteBoolean(mine);
                if (mine) {
                    var dir = BuildDirectory(c.Character, NormalizeNumber(DeviceNumber));
                    msg.WriteUInt16((ushort)dir.Count);
                    foreach (var e in dir) { msg.WriteString(e.num); msg.WriteString(e.name); msg.WriteUInt16((ushort)e.count); }
                }
                return;
            }
            if (TryExtractEventData<HistEvt>(extraData, out var h)) {
                msg.WriteByte((byte)4);
                msg.WriteBoolean(mine);
                if (mine) {
                    string myNum = NormalizeNumber(DeviceNumber);
                    msg.WriteString(h.Peer);
                    List<(string from, string text)> list = null;
                    if (IsGroupNumber(h.Peer)) {
                        if (Groups.TryGetValue(h.Peer, out GroupInfo group) && group.Members.Contains(myNum)) {
                            History.TryGetValue(new PairKey(h.Peer, h.Peer), out list);
                        }
                    }
                    else {
                        History.TryGetValue(new PairKey(myNum, h.Peer), out list);
                    }
                    int count = Math.Min(list?.Count ?? 0, ushort.MaxValue);
                    msg.WriteUInt16((ushort)count);
                    for (int i = 0; i < count; i++) { msg.WriteBoolean(list[i].from == myNum); msg.WriteString(list[i].text); }
                }
                return;
            }
        }
        private static void Append(string a, string b, string from, string text) {
            var key = new PairKey(a, b);
            if (!History.TryGetValue(key, out var list)) { History[key] = list = new List<(string, string)>(); }
            list.Add((from, text));
            if (list.Count > MaxHistoryPerPair) { list.RemoveRange(0, list.Count - MaxHistoryPerPair); }
        }
        private void Deliver(string from, string to, string text) {
            item.CreateServerEvent(this, new DeliverEvt(from, to, text, true));
            foreach (Item it in Item.ItemList) {
                var m = it.GetComponent<PdaMessengerComponent>();
                if (m == null || ReferenceEquals(m, this) || NormalizeNumber(m.DeviceNumber) != to) { continue; }
                it.CreateServerEvent(m, new DeliverEvt(from, to, text, false));
            }
        }
        private void DeliverGroup(string from, string groupNum, string text, GroupInfo group) {
            item.CreateServerEvent(this, new DeliverEvt(from, groupNum, text, true));
            foreach (Item it in Item.ItemList) {
                var m = it.GetComponent<PdaMessengerComponent>();
                if (m == null || ReferenceEquals(m, this)) { continue; }
                string num = NormalizeNumber(m.DeviceNumber);
                if (num == from || !group.Members.Contains(num)) { continue; }
                it.CreateServerEvent(m, new DeliverEvt(groupNum, groupNum, text, false));
            }
        }
        private static List<(string num, string name, int count)> BuildDirectory(Character requester, string myNum) {
            var result = new List<(string, string, int)>();
            if (requester == null) { return result; }
            var seen = new HashSet<string>();
            foreach (Character c in Character.CharacterList) {
                if (c == null || c.Removed || c.IsDead || !c.IsHuman || c.Inventory == null || c.TeamID != requester.TeamID) { continue; }
                if (c == requester) { continue; }
                foreach (Item it in c.Inventory.AllItems) {
                    var m = it.GetComponent<PdaMessengerComponent>();
                    if (m == null) { continue; }
                    string num = NormalizeNumber(m.DeviceNumber);
                    if (string.IsNullOrEmpty(num) || num == myNum || !seen.Add(num)) { continue; }
                    result.Add((num, c.Info?.Name ?? c.Name ?? "", 0));
                }
            }
            if (!string.IsNullOrEmpty(myNum)) {
                foreach (KeyValuePair<string, GroupInfo> kv in Groups) {
                    if (!kv.Value.Members.Contains(myNum) || !seen.Add(kv.Key)) { continue; }
                    result.Add((kv.Key, kv.Value.Name, kv.Value.Members.Count));
                }
                foreach (PairKey key in History.Keys) {
                    string peer = key.A == myNum ? key.B : key.B == myNum ? key.A : null;
                    if (peer == null || peer == myNum || !seen.Add(peer)) { continue; }
                    result.Add((peer, "", 0));
                }
            }
            return result;
        }
        private static string GenUnique() {
            var taken = new HashSet<string>();
            foreach (Item it in Item.ItemList) {
                var m = it.GetComponent<PdaMessengerComponent>();
                if (m != null && !string.IsNullOrEmpty(m.DeviceNumber)) { taken.Add(m.DeviceNumber); }
            }
            for (int i = 0; i < 10000; i++) {
                string cand = Rng.Next(0, GroupNumberFloor).ToString("D" + NumberDigits);
                if (!taken.Contains(cand)) { return cand; }
            }
            return (DateTime.Now.Ticks % GroupNumberFloor).ToString("D" + NumberDigits);
        }
        private static string GenUniqueGroup() {
            for (int i = 0; i < 2000; i++) {
                string cand = Rng.Next(GroupNumberFloor, 10000).ToString("D" + NumberDigits);
                if (!Groups.ContainsKey(cand)) { return cand; }
            }
            return null;
        }
        private readonly struct PairKey : IEquatable<PairKey> {
            public readonly string A, B;
            public PairKey(string a, string b) { if (string.CompareOrdinal(a, b) <= 0) { A = a; B = b; } else { A = b; B = a; } }
            public bool Equals(PairKey o) => A == o.A && B == o.B;
            public override bool Equals(object o) => o is PairKey k && Equals(k);
            public override int GetHashCode() => (A?.GetHashCode() ?? 0) ^ (B?.GetHashCode() ?? 0);
        }
    }
}
