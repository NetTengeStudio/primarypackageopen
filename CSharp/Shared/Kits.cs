using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using PrimaryPackage.Economy;

namespace PrimaryPackage.Kits {
    public static class KitNet {
        public const string NetId = "pp_kits";
        public const byte RequestList = 1;
        public const byte Create = 2;
        public const byte Delete = 3;
        public const byte Spawn = 4;
        public const byte ExportJob = 5;
        public const byte ListSync = 10;
        public const byte FlagControl = 1;
        public const byte FlagHostile = 2;

        public const byte FlagFirst = 1;
        public const byte FlagLast = 2;
        public const int MaxPerMessage = 32;
        public const int MaxMessageBytes = 1024;
    }

    public static class KitStore {
        private const string RelativePath = "Data/pp_kits.xml";
        public static readonly Identifier SetId = "pp_kits".ToIdentifier();

        public sealed class Kit {
            public XElement Element;
            private HumanPrefab prefab;
            public HumanPrefab Prefab => prefab ??= KitFactory.BuildPrefab(Element);
        }

        private static readonly Dictionary<Identifier, Kit> kits = new();

        public static IEnumerable<KeyValuePair<Identifier, Kit>> All => kits.OrderBy(k => k.Key.Value);
        public static int Count => kits.Count;
        public static bool TryGet(Identifier id, out Kit kit) => kits.TryGetValue(id, out kit);

        public static void Load() {
            kits.Clear();
            PrimaryPackage.Core.ModPrefs.Read(RelativePath, root => {
                foreach (XElement el in root.Elements("npc")) {
                    Identifier id = (el.Attribute("identifier")?.Value ?? "").ToIdentifier();
                    if (!id.IsEmpty) { kits[id] = new Kit { Element = el }; }
                }
            });
        }

        private static void Save() {
            var root = new XElement("Kits");
            foreach (var pair in All) { root.Add(pair.Value.Element); }
            PrimaryPackage.Core.ModPrefs.Write(RelativePath, root);
        }

        public static bool CreateFromCharacter(Character character, Identifier id) {
            if (id.IsEmpty || character?.Info == null || character.Inventory == null) { return false; }
            var npc = new XElement("npc",
                new XAttribute("identifier", id.Value),
                new XAttribute("job", character.Info.Job?.Prefab.Identifier.Value ?? "any"));
            XElement charEl = character.Info.Save(npc);
            charEl.Attribute("hull")?.Remove();
            charEl.Attribute("npcsetid")?.Remove();
            charEl.Attribute("npcid")?.Remove();
            npc.Add(KitFactory.BuildItemSet(character));
            kits[id] = new Kit { Element = npc };
            Save();
            return true;
        }

        public static bool Delete(Identifier id) {
            if (!kits.Remove(id)) { return false; }
            Save();
            return true;
        }

        public static void ReplaceAll(List<(Identifier Id, XElement Element)> items) {
            kits.Clear();
            foreach (var (id, el) in items) { kits[id] = new Kit { Element = el }; }
        }
    }

    public static class KitFactory {
        public static HumanPrefab BuildPrefab(XElement element) {
            ContentPackage? pkg = PrimaryPackage.Core.ModAssets.Package;
            ContentFile? file = pkg?.Files.FirstOrDefault();
            if (file == null || element == null) { return null; }
            try {
                return new HumanPrefab(element.FromPackage(pkg), file, KitStore.SetId);
            } catch (Exception e) {
                DebugConsole.ThrowError("pp_kits - не удалось собрать HumanPrefab из кита", e);
                return null;
            }
        }

        public static XElement BuildItemSet(Character character) {
            var set = new XElement("ItemSet", new XAttribute("commonness", 1));
            var inv = character.Inventory;
            var seen = new HashSet<Item>();
            for (int i = 0; i < inv.Capacity; i++) {
                bool equip = inv.SlotTypes[i] != InvSlotType.Any;
                foreach (Item item in inv.GetItemsAt(i)) {
                    if (seen.Add(item)) { set.Add(BuildItem(item, equip, equip ? -1 : i)); }
                }
            }
            return set;
        }

        private static XElement BuildItem(Item item, bool equip, int slotIndex) {
            var el = new XElement("Item", new XAttribute("identifier", item.Prefab.Identifier.Value));
            if (equip) { el.Add(new XAttribute("equip", "true")); }
            if (slotIndex >= 0) { el.Add(new XAttribute("slotindex", slotIndex)); }
            var inv = item.OwnInventory;
            if (inv == null) { return el; }
            var seen = new HashSet<Item>();
            for (int i = 0; i < inv.Capacity; i++) {
                foreach (Item contained in inv.GetItemsAt(i)) {
                    if (seen.Add(contained)) { el.Add(BuildItem(contained, false, i)); }
                }
            }
            return el;
        }
    }

    public static class KitSpawn {
        public static HumanPrefab Find(Identifier id, out bool isKit) {
            isKit = false;
            if (KitStore.TryGet(id, out var kit)) {
                isKit = true;
                return kit.Prefab;
            }
            return NPCSet.Sets.SelectMany(s => s.Humans).FirstOrDefault(h => h.Identifier == id);
        }

        public const string AssaultOrder = "assaultenemy";

        public static bool Spawn(HumanPrefab prefab, Vector2 worldPos, CharacterTeamType team, bool hostile,
            Action<Character> onSpawned) {
            if (prefab == null || Entity.Spawner == null) { return false; }
            CharacterInfo info = prefab.CreateCharacterInfo();
            info._humanPrefab = prefab;
            Entity.Spawner.AddCharacterToSpawnQueue(CharacterPrefab.HumanSpeciesName, worldPos, info, onSpawn: c => {
                c.HumanPrefab = prefab;
                c.SetOriginalTeamAndChangeTeam(hostile ? CharacterTeamType.None : team, processImmediately: true);
                prefab.GiveItems(c, c.Submarine, null);
                prefab.InitializeCharacter(c);
                if (hostile) {
                    Disown(c);
                    Assault(c);
                } else {
                    GameMain.GameSession?.CrewManager?.AddCharacter(c);
                    c.OnDeath += RemoveFromCrew;
                }
                onSpawned?.Invoke(c);
            });
            return true;
        }

        private static void Disown(Character c) {
            Inventory inventory = c.Inventory;
            if (inventory == null) { return; }
            foreach (Item item in inventory.FindAllItems(recursive: true)) {
                if (item.GetComponent<IdCard>() is IdCard card) { card.TeamID = CharacterTeamType.None; }
                if (item.GetComponent<WifiComponent>() is WifiComponent wifi) { wifi.TeamID = CharacterTeamType.None; }
                item.AllowStealing = true;
            }
        }

        private static void Assault(Character c) {
            if (c.AIController is not HumanAIController) { return; }
            OrderPrefab prefab = OrderPrefab.Prefabs[AssaultOrder.ToIdentifier()];
            if (prefab == null) {
                DebugConsole.AddWarning($"{AssaultOrder} does not resolve");
                return;
            }
            try {
                Order order = prefab.CreateInstance(OrderPrefab.OrderTargetType.Entity, orderGiver: c)
                    .WithManualPriority(CharacterInfo.HighestManualOrderPriority);
                c.SetOrder(order, isNewOrder: true, speak: false);
            } catch (Exception e) {
                DebugConsole.AddWarning($"{c.Name} does not take the assault order {e.Message}");
            }
        }

        private static void RemoveFromCrew(Character c, CauseOfDeath _) {
            GameMain.GameSession?.CrewManager?.RemoveCharacterInfo(c.Info);
        }
    }

    public static class KitJobExport {
        public static bool Export(Identifier id, out string error) {
            error = null;
            if (!KitStore.TryGet(id, out var kit)) {
                error = "kit not found";
                return false;
            }
            try {
                WriteJob(id, kit.Element);
                WriteTexts(id);
                return true;
            } catch (Exception e) {
                error = e.Message;
                return false;
            }
        }

        private static void WriteJob(Identifier id, XElement kitElement) {
            const string path = "Data/pp_jobs.xml";
            XElement root = PrimaryPackage.Core.ModPrefs.Open(path, () => new XElement("Jobs"));
            root.Elements("Job").Where(j => (j.Attribute("identifier")?.Value ?? "") == id.Value).Remove();

            var job = new XElement("Job",
                new XAttribute("identifier", id.Value),
                new XAttribute("minnumber", 0),
                new XAttribute("maxnumber", 10),
                new XAttribute("initialcount", 0),
                new XAttribute("uicolor", "0.5,0.7,0.6"),
                new XAttribute("vitalitymodifier", 0));
            job.Add(new XElement("Jobicon",
                new XElement("sprite",
                    new XAttribute("texture", "Content/UI/MainIconsAtlas.png"),
                    new XAttribute("sourcerect", "384,256,128,128"),
                    new XAttribute("origin", "0.5,0.5"))));

            var skills = new XElement("Skills");
            XElement jobEl = kitElement.Element("Character")?.Element("job");
            bool primary = true;
            foreach (XElement skill in jobEl?.Elements("skill") ?? Enumerable.Empty<XElement>()) {
                string skillId = skill.Attribute("identifier")?.Value;
                if (string.IsNullOrEmpty(skillId)) { continue; }
                int level = (int)Math.Round(float.TryParse(skill.Attribute("level")?.Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lv) ? lv : 10f);
                var skillOut = new XElement("Skill",
                    new XAttribute("identifier", skillId),
                    new XAttribute("level", $"{level},{level}"));
                if (primary) {
                    skillOut.Add(new XAttribute("primary", "true"));
                    primary = false;
                }
                skills.Add(skillOut);
            }
            if (!skills.HasElements) {
                skills.Add(new XElement("Skill", new XAttribute("identifier", "weapons"), new XAttribute("level", "10,20"), new XAttribute("primary", "true")));
            }
            job.Add(skills);

            XElement itemSet = kitElement.Element("ItemSet");
            if (itemSet != null) {
                var copy = new XElement(itemSet);
                foreach (XElement el in copy.DescendantsAndSelf()) { el.Attribute("slotindex")?.Remove(); }
                job.Add(copy);
            } else {
                job.Add(new XElement("ItemSet"));
            }

            root.Add(job);
            PrimaryPackage.Core.ModPrefs.Write(path, root);
        }

        private static void WriteTexts(Identifier id) {
            WriteTextFile("Content/Texts/KitJobs_Russian.xml", "Russian", id);
            WriteTextFile("Content/Texts/KitJobs_English.xml", "English", id);
        }

        private static void WriteTextFile(string path, string language, Identifier id) {
            XElement root = PrimaryPackage.Core.ModPrefs.Open(path, () => new XElement("infotexts",
                new XAttribute("language", language),
                new XAttribute("nowhitespace", "false"),
                new XAttribute("translatedname", language)));
            string nameKey = "jobname." + id.Value;
            string descKey = "jobdescription." + id.Value;
            if (root.Element(nameKey) == null) { root.Add(new XElement(nameKey, id.Value)); }
            if (root.Element(descKey) == null) { root.Add(new XElement(descKey, id.Value)); }
            PrimaryPackage.Core.ModPrefs.Write(path, root);
        }
    }
}
