using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace PrimaryPackage.Inspect {
    public static class InspectRegistry {
        private static readonly Dictionary<MapEntity, InspectData> map = new Dictionary<MapEntity, InspectData>();
        private static readonly List<MapEntity> entities = new List<MapEntity>();
        private static readonly List<InspectData> datas = new List<InspectData>();

        public static int Count => entities.Count;
        public static MapEntity EntityAt(int index) => entities[index];
        public static InspectData DataAt(int index) => datas[index];

        public static InspectData Get(MapEntity entity) =>
            entity != null && map.TryGetValue(entity, out InspectData data) ? data : null;

        public static InspectData Attach(MapEntity entity, InspectData data = null) {
            if (entity == null) { return null; }
            if (map.TryGetValue(entity, out InspectData existing)) { return existing; }
            data ??= new InspectData();
            map[entity] = data;
            entities.Add(entity);
            datas.Add(data);
            return data;
        }

        public static void Detach(MapEntity entity) {
            if (entity == null || !map.Remove(entity)) { return; }
            int index = entities.IndexOf(entity);
            if (index < 0) { return; }
            entities.RemoveAt(index);
            datas.RemoveAt(index);
        }

        public static void Prune() {
            for (int i = entities.Count - 1; i >= 0; i--) {
                if (!entities[i].Removed) { continue; }
                map.Remove(entities[i]);
                entities.RemoveAt(i);
                datas.RemoveAt(i);
            }
        }

        public static void Clear() {
            map.Clear();
            entities.Clear();
            datas.Clear();
        }
    }

    public sealed class InspectRegistryPlugin : PrimaryPackage.Core.IModule {
        private Harmony harmony;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            Patch(AccessTools.Method(typeof(Item), nameof(Item.Save)), nameof(Save_Postfix));
            Patch(AccessTools.Method(typeof(Structure), nameof(Structure.Save)), nameof(Save_Postfix));
            Patch(AccessTools.Method(typeof(Item), nameof(Item.Load),
                new[] { typeof(ContentXElement), typeof(Submarine), typeof(bool), typeof(IdRemap) }), nameof(ItemLoad_Postfix));
            Patch(AccessTools.Method(typeof(Structure), nameof(Structure.Load)), nameof(StructureLoad_Postfix));
            Patch(AccessTools.Method(typeof(Item), nameof(Item.Clone)), nameof(Clone_Postfix));
            Patch(AccessTools.Method(typeof(Structure), nameof(Structure.Clone)), nameof(Clone_Postfix));
        }


        public void Release() {
            InspectRegistry.Clear();
        }

        private void Patch(System.Reflection.MethodBase target, string postfix) {
            if (target == null) { return; }
            try {
                harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(InspectRegistryPlugin), postfix)));
            }
            catch (System.Exception e) {
                DebugConsole.ThrowError("pp_inspect: не удалось пропатчить " + target.DeclaringType?.Name + "." + target.Name, e);
            }
        }

        public static void Save_Postfix(MapEntity __instance, XElement __result) {
            if (__result == null) { return; }
            InspectRegistry.Get(__instance)?.Save(__result);
        }

        public static void ItemLoad_Postfix(ContentXElement element, Item __result) => Load(element, __result);

        public static void StructureLoad_Postfix(ContentXElement element, Structure __result) => Load(element, __result);

        private static void Load(ContentXElement element, MapEntity entity) {
            if (entity == null || element == null) { return; }
            ContentXElement stored = element.GetChildElement(InspectData.ElementName);
            if (stored == null) { return; }
            InspectRegistry.Attach(entity, new InspectData(stored.Element));
        }

        public static void Clone_Postfix(MapEntity __instance, MapEntity __result) {
            InspectData source = InspectRegistry.Get(__instance);
            if (source == null || __result == null) { return; }
            InspectRegistry.Attach(__result, new InspectData(source));
        }
    }
}
