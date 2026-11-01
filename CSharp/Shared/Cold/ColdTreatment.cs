using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using PrimaryPackage.Core;

namespace PrimaryPackage.Cold {
    public static class ColdTreatment {
        private static readonly ConditionalWeakTable<Item, object> injected = new ConditionalWeakTable<Item, object>();
        private static readonly object marker = new object();

        public static void Inject(Item item) {
            if (item?.Prefab == null || injected.TryGetValue(item, out _)) { return; }
            injected.Add(item, marker);
            ColdConfig.EnsureLoaded();
            if (!ColdConfig.Treatments.TryGetValue(item.Prefab.Identifier, out XElement template)) { return; }

            var element = new ContentXElement(item.Prefab.ContentPackage ?? ModAssets.Package, new XElement(template));
            StatusEffect effect = StatusEffect.Load(element, "pp_cold:" + item.Prefab.Identifier);
            if (effect == null) { return; }

            List<ItemComponent> components = item.Components;
            for (int i = 0; i < components.Count; i++) {
                Dictionary<ActionType, List<StatusEffect>> lists = components[i].statusEffectLists;
                if (lists == null) { continue; }
                if (!lists.TryGetValue(effect.type, out List<StatusEffect> list)) {
                    list = new List<StatusEffect>();
                    lists.Add(effect.type, list);
                }
                list.Add(effect);
                return;
            }
            DebugConsole.AddWarning("pp_cold: у предмета " + item.Prefab.Identifier +
                                    " нет компонента со статус-эффектами, лечение холода не подключено.");
        }
    }
}
