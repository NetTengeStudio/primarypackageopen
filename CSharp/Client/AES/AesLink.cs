using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;

namespace PrimaryPackage.Aes {
    public static class AesLink {
        public static List<Item> GatherVents(Item source) {
            Submarine sub = source.Submarine;
            var list = new List<Item>();
            var items = Item.ItemList;
            for (int i = 0; i < items.Count; i++) {
                Item it = items[i];
                if (it.Submarine != sub || it == source) { continue; }
                if (it.GetComponent<Vent>() != null) { list.Add(it); }
            }
            return list;
        }

        public static void Link(MapEntity a, MapEntity b) {
            if (a == b) { return; }
            if (!a.linkedTo.Contains(b)) { a.linkedTo.Add(b); }
            if (b.Linkable && !b.linkedTo.Contains(a)) { b.linkedTo.Add(a); }
        }

        public static void LinkAll(Item source, List<Item> targets) {
            for (int i = 0; i < targets.Count; i++) { Link(source, targets[i]); }
        }
    }
}
