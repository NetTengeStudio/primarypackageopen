using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Economy {
    internal static class EconomyPersistence {
        private const string RelativePath = "Data/economy_state.xml";
        private const double SaveInterval = 10.0;
        private static bool dirty;
        private static double nextSaveAt;
        private static bool subscribed;

        public static void Initialize() {
            if (!subscribed) {
                StationEconomyState.Changed += () => dirty = true;
                subscribed = true;
            }
            Load();
        }

        private static void Load() {
            PrimaryPackage.Core.ModPrefs.Read(RelativePath, root => {
                int balance = (int?)root.Attribute("balance") ?? 0;
                var history = new List<LedgerEntry>();
                foreach (XElement e in root.Elements("Entry")) {
                    history.Add(new LedgerEntry(
                        (string)e.Attribute("date") ?? "",
                        (int?)e.Attribute("amount") ?? 0,
                        (string)e.Attribute("comment") ?? ""));
                }
                StationEconomyState.ReplaceFromSync(balance, history);
                dirty = false;
                DebugConsole.NewMessage($"economy balance {balance:N0}, ledger entries {history.Count}");
            });
        }

        public static void SaveIfDue(double now) {
            if (!dirty || now < nextSaveAt) { return; }
            nextSaveAt = now + SaveInterval;
            Save();
        }

        public static void Flush() {
            if (dirty) { Save(); }
        }

        private static void Save() {
            var root = new XElement("EconomyState", new XAttribute("balance", StationEconomyState.Balance));
            foreach (LedgerEntry e in StationEconomyState.History) {
                root.Add(new XElement("Entry",
                    new XAttribute("date", e.Date ?? ""),
                    new XAttribute("amount", e.Amount),
                    new XAttribute("comment", e.Comment ?? "")));
            }
            if (PrimaryPackage.Core.ModPrefs.Write(RelativePath, root)) { dirty = false; }
        }
    }
}
