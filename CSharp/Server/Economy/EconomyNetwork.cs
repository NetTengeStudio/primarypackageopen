using System.Collections.Generic;
using Barotrauma;
namespace PrimaryPackage.Economy {
    internal static class EconomyNetwork {
        private static readonly List<Item> registered = new List<Item>();
        public static void Register(Item item) {
            Prune();
            if (item != null && !registered.Contains(item)) registered.Add(item);
        }
        public static void BroadcastAll() {
            if (GameMain.Server == null) return;
            Prune();
            foreach (Item item in registered) {
                item.GetComponent<StationBalanceComponent>()?.PushFullSync();
                item.GetComponent<StationAtmComponent>()?.PushFullSync();
            }
        }
        private static void Prune() => registered.RemoveAll(it => it == null || it.Removed);
    }
}
