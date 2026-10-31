using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using PrimaryPackage.Core;

namespace PrimaryPackage.Economy {
    public readonly struct OrderItem {
        public readonly Identifier ItemId;
        public readonly int Count;
        public OrderItem(Identifier itemId, int count) {
            ItemId = itemId;
            Count = count;
        }
    }
    public sealed class OrderSet {
        public const float DefaultBuyMarkup = 1.5f;
        public readonly Identifier Id;
        public readonly string DisplayName;
        public readonly int Reward;
        public readonly int BuyPrice;
        public readonly IReadOnlyList<OrderItem> Items;
        public OrderSet(Identifier id, string displayName, int reward, int buyPrice, IReadOnlyList<OrderItem> items) {
            Id = id;
            DisplayName = displayName;
            Reward = reward;
            BuyPrice = buyPrice > 0 ? buyPrice : (int)Math.Ceiling(Math.Max(0, reward) * DefaultBuyMarkup);
            Items = items;
        }
    }
    public static class OrderSets {
        private const string PackageName = "Primary package";
        private const string RelativePath = "Data/pp_ordersets.xml";
        private static readonly Dictionary<Identifier, OrderSet> sets = new();
        public static IReadOnlyCollection<OrderSet> All => sets.Values;
        public static bool TryGet(Identifier id, out OrderSet set) => sets.TryGetValue(id, out set);
        public static void Load() {
            sets.Clear();
            PrimaryPackage.Core.ModPrefs.Read(RelativePath, LoadFrom);
        }

        private static void LoadFrom(XElement root) {
            foreach (XElement el in root.Elements()) {
                if (!el.Name.LocalName.Equals("OrderSet", StringComparison.OrdinalIgnoreCase)) { continue; }
                Identifier id = (el.Attribute("id")?.Value ?? "").ToIdentifier();
                if (id.IsEmpty) { continue; }
                string name = el.Attribute("name")?.Value ?? id.Value;
                int.TryParse(el.Attribute("reward")?.Value, out int reward);
                int.TryParse(el.Attribute("buyprice")?.Value, out int buyPrice);
                var items = new List<OrderItem>();
                foreach (XElement itemEl in el.Elements()) {
                    if (!itemEl.Name.LocalName.Equals("Item", StringComparison.OrdinalIgnoreCase)) { continue; }
                    Identifier itemId = (itemEl.Attribute("identifier")?.Value ?? "").ToIdentifier();
                    if (itemId.IsEmpty) { continue; }
                    if (!int.TryParse(itemEl.Attribute("count")?.Value, out int count) || count < 1) { count = 1; }
                    items.Add(new OrderItem(itemId, count));
                }
                sets[id] = new OrderSet(id, name, reward, buyPrice, items);
            }
        }
    }
}
