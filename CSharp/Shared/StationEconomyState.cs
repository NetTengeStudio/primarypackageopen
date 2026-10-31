using System;
using System.Collections.Generic;
namespace PrimaryPackage.Economy {
    public readonly struct LedgerEntry {
        public readonly string Date;
        public readonly int Amount;
        public readonly string Comment;
        public LedgerEntry(string date, int amount, string comment) { Date = date; Amount = amount; Comment = comment; }
    }
    internal static class StationEconomyState {
        public const int MaxHistory = 500;
        public const int SyncWindow = 60;
        public static int Balance { get; private set; }
        public static readonly List<LedgerEntry> History = new List<LedgerEntry>();
        public static event Action Changed;
        public static void ApplyTransaction(string date, int amount, string comment) {
            if (string.IsNullOrWhiteSpace(date)) date = DateTime.Now.ToString("dd.MM.yyyy");
            comment = string.IsNullOrWhiteSpace(comment) ? OrderText.Get("pp.order.ledger.no_comment", "Без комментария") : comment.Trim();
            History.Add(new LedgerEntry(date, amount, comment));
            if (History.Count > MaxHistory) History.RemoveAt(0);
            Balance += amount;
            Changed?.Invoke();
        }
        public static void SetBalance(int v) { Balance = v; Changed?.Invoke(); }
        public static void ClearHistory() { History.Clear(); Changed?.Invoke(); }
        public static void ReplaceFromSync(int balance, List<LedgerEntry> history) {
            Balance = balance;
            History.Clear();
            History.AddRange(history);
            Changed?.Invoke();
        }
    }
}
