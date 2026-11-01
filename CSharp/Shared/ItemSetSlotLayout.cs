using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace PrimaryPackage.SlotLayout {
    public sealed class SlotLayoutPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(HumanPrefab), nameof(HumanPrefab.InitializeItem)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(InitializeItemReplacement), nameof(InitializeItemReplacement.Prefix))));
            harmony.Patch(
                AccessTools.Method(typeof(Item), nameof(Item.Clone)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ContainerCloneLayout), nameof(ContainerCloneLayout.Postfix))));
            harmony.Patch(
                AccessTools.Method(typeof(Item), nameof(Item.Clone)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TerminalCloneText), nameof(TerminalCloneText.Postfix))));
        }


        public void Release() {
        }
    }

    internal static class ContainerCloneLayout {
        public static void Postfix(Item __instance, MapEntity __result) {
            if (__result is not Item clone) { return; }
            var origContainers = new List<ItemContainer>(__instance.GetComponents<ItemContainer>());
            var cloneContainers = new List<ItemContainer>(clone.GetComponents<ItemContainer>());
            int pairs = origContainers.Count < cloneContainers.Count ? origContainers.Count : cloneContainers.Count;
            for (int c = 0; c < pairs; c++) {
                Reseat(origContainers[c].Inventory, cloneContainers[c].Inventory);
            }
        }

        private static void Reseat(Inventory origInv, Inventory cloneInv) {
            var origItems = new List<Item>(origInv.AllItems);
            var cloneItems = new List<Item>(cloneInv.AllItems);
            int count = origItems.Count < cloneItems.Count ? origItems.Count : cloneItems.Count;
            for (int k = count - 1; k >= 0; k--) {
                int target = origInv.FindIndex(origItems[k]);
                Item cloned = cloneItems[k];
                if (target < 0 || cloneInv.FindIndex(cloned) == target) { continue; }
                cloneInv.TryPutItem(cloned, target, allowSwapping: true, allowCombine: true, user: null,
                    createNetworkEvent: false, ignoreCondition: true, triggerOnInsertedEffects: false);
            }
        }
    }

    internal static class TerminalCloneText {
        public static void Postfix(Item __instance, MapEntity __result) {
            if (__result is not Item clone) { return; }
            var origTerminals = new List<Terminal>(__instance.GetComponents<Terminal>());
            var cloneTerminals = new List<Terminal>(clone.GetComponents<Terminal>());
            int pairs = origTerminals.Count < cloneTerminals.Count ? origTerminals.Count : cloneTerminals.Count;
            for (int i = 0; i < pairs; i++) {
                XElement saved = origTerminals[i].Save(new XElement("root"));
                cloneTerminals[i].Load(saved.FromPackage(clone.Prefab.ContentPackage), usePrefabValues: false, IdRemap.DiscardId, isItemSwap: false);
            }
        }
    }

    internal static class InitializeItemReplacement {
        private static readonly FieldInfo InfiniteItemsField = AccessTools.Field(typeof(HumanPrefab), "infiniteItems");

        public static bool Prefix(Character character, ContentXElement itemElement, Submarine submarine, HumanPrefab humanPrefab, WayPoint spawnPoint, Item parentItem, bool createNetworkEvents) {
            string itemIdentifier = itemElement.GetAttributeString("identifier", "");
            if (MapEntityPrefab.FindByIdentifier(itemIdentifier.ToIdentifier()) is not ItemPrefab itemPrefab) {
                DebugConsole.ThrowError($"Tried to spawn \"{humanPrefab?.Identifier}\" with the item \"{itemIdentifier}\". Matching item prefab not found.",
                    contentPackage: itemElement?.ContentPackage);
                return false;
            }
            Item item = new Item(itemPrefab, character.Position, null);
#if SERVER
            if (GameMain.Server != null && Entity.Spawner != null && createNetworkEvents) {
                if (GameMain.Server.EntityEventManager.UniqueEvents.Any(ev => ev.Entity == item)) {
                    GameMain.Server.EntityEventManager.UniqueEvents.RemoveAll(ev => ev.Entity == item);
                    GameMain.Server.EntityEventManager.Events.RemoveAll(ev => ev.Entity == item);
                }
                Entity.Spawner.CreateNetworkEvent(new EntitySpawner.SpawnEntity(item));
            }
#endif
            if (itemElement.GetAttributeBool("equip", false)) {
                List<InvSlotType> allowedSlots =
                    item.GetComponents<Pickable>().Count() > 1 ?
                    new List<InvSlotType>(item.GetComponent<Wearable>()?.AllowedSlots ?? item.GetComponent<Pickable>().AllowedSlots) :
                    new List<InvSlotType>(item.AllowedSlots);
                allowedSlots.Remove(InvSlotType.Any);
                item.UnequipAutomatically = false;
                character.Inventory.TryPutItem(item, null, allowedSlots);
            } else {
                character.Inventory.TryPutItem(item, null, item.AllowedSlots);
            }
            IdCard idCardComponent = item.GetComponent<IdCard>();
            if (idCardComponent != null) {
                idCardComponent.Initialize(spawnPoint, character);
                if (submarine != null && (submarine.Info.IsWreck || submarine.Info.IsOutpost)) {
                    idCardComponent.SubmarineSpecificID = submarine.SubmarineSpecificIDTag;
                }
                foreach (string tag in itemElement.GetAttributeStringArray("tags", System.Array.Empty<string>())) {
                    item.AddTag(tag);
                }
            }
            foreach (WifiComponent wifiComponent in item.GetComponents<WifiComponent>()) {
                wifiComponent.TeamID = character.TeamID;
            }
            parentItem?.Combine(item, user: null);
            int slotIndex = itemElement.GetAttributeInt("slotindex", -1);
            if (slotIndex >= 0 && (parentItem != null || !itemElement.GetAttributeBool("equip", false))) {
                Inventory target = parentItem != null ? parentItem.OwnInventory : character.Inventory;
                if (target != null && item.ParentInventory == target && target.GetItemAt(slotIndex) != item) {
                    target.TryPutItem(item, slotIndex, allowSwapping: false, allowCombine: true, user: null, createNetworkEvent: createNetworkEvents);
                }
            }
            if (itemElement.GetAttributeBool("infinite", false)) {
                (InfiniteItemsField?.GetValue(humanPrefab) as Dictionary<Identifier, ItemPrefab>)?.TryAdd(itemPrefab.Identifier, itemPrefab);
            }
            foreach (ContentXElement childItemElement in itemElement.Elements()) {
                int amount = childItemElement.GetAttributeInt("amount", 1);
                for (int i = 0; i < amount; i++) {
                    HumanPrefab.InitializeItem(character, childItemElement, submarine, humanPrefab, spawnPoint, item, createNetworkEvents);
                }
            }
            return false;
        }
    }
}
