using System;
using System.Collections.Generic;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    internal static class EconomyNetIO {
        private static int ClampToInt(long v) => v > int.MaxValue ? int.MaxValue : (v < int.MinValue ? int.MinValue : (int)v);
        public static void WriteState(IWriteMessage msg) {
            msg.WriteInt64((long)StationEconomyState.Balance);
            int count = Math.Min(StationEconomyState.History.Count, StationEconomyState.SyncWindow);
            int start = StationEconomyState.History.Count - count;
            msg.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++) {
                LedgerEntry e = StationEconomyState.History[start + i];
                msg.WriteString(e.Date ?? "");
                msg.WriteInt64((long)e.Amount);
                msg.WriteString(e.Comment ?? "");
            }
        }
        public static void ReadState(IReadMessage msg, out int balance, out List<LedgerEntry> history) {
            balance = ClampToInt(msg.ReadInt64());
            int count = msg.ReadUInt16();
            history = new List<LedgerEntry>(count);
            for (int i = 0; i < count; i++) {
                string date = msg.ReadString();
                int amount = ClampToInt(msg.ReadInt64());
                history.Add(new LedgerEntry(date, amount, msg.ReadString()));
            }
        }
    }
}
