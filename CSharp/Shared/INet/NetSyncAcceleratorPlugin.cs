using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using Barotrauma;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;
using Voronoi2;

namespace INet {
    public sealed class NetSyncAcceleratorPlugin : IModule {
        private static Harmony harmony;
        private static FieldInfo entitiesField;
        private static FieldInfo allEntitiesField;
        private static GCLatencyMode previousLatencyMode;

        private static readonly ConditionalWeakTable<VoronoiCell, StrongBox<Vector4>> CellAabbCache = new();
        private static readonly Dictionary<MapEntityPrefab, int> LastPrefabIndex = new();

        public void Attach(Harmony h) {
            harmony = h;

            MethodInfo levelGen = AccessTools.Method(typeof(Level), nameof(Level.Generate), new[] {
                typeof(LevelData), typeof(bool), typeof(Location), typeof(Location), typeof(SubmarineInfo), typeof(SubmarineInfo)
            });
            if (levelGen != null) {
                harmony.Patch(levelGen,
                    prefix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PrefixLevelGenerate)),
                    postfix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PostfixLevelGenerate)));
            }

            MethodInfo isPointInside = AccessTools.Method(typeof(VoronoiCell), nameof(VoronoiCell.IsPointInside), new[] { typeof(Vector2) });
            if (isPointInside != null) {
                harmony.Patch(isPointInside, prefix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PrefixIsPointInside)));
            }

            MethodInfo getTooClose = AccessTools.Method(typeof(Level), "GetTooCloseCells", new[] { typeof(List<VoronoiCell>), typeof(float) });
            if (getTooClose != null) {
                harmony.Patch(getTooClose, prefix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PrefixGetTooCloseCells)));
            }

            MethodInfo insertToList = AccessTools.Method(typeof(MapEntity), "InsertToList");
            if (insertToList != null) {
                harmony.Patch(insertToList, prefix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PrefixInsertToList)));
            }

            MethodInfo createDroppedStacks = AccessTools.Method(typeof(MapEntity), "CreateDroppedStacks", new[] { typeof(List<MapEntity>) });
            if (createDroppedStacks != null) {
                harmony.Patch(createDroppedStacks, prefix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PrefixCreateDroppedStacks)));
            }

            entitiesField = AccessTools.Field(typeof(EntityGrid), "entities");
            allEntitiesField = AccessTools.Field(typeof(EntityGrid), "allEntities");
            MethodInfo removeEntity = AccessTools.Method(typeof(EntityGrid), nameof(EntityGrid.RemoveEntity), new[] { typeof(MapEntity) });
            if (removeEntity != null && entitiesField != null && allEntitiesField != null) {
                harmony.Patch(removeEntity, prefix: new HarmonyMethod(typeof(NetSyncAcceleratorPlugin), nameof(PrefixRemoveEntity)));
            }
        }

        public void RoundStart() {
            CellAabbCache.Clear();
            LastPrefabIndex.Clear();
        }

        public void Release() {
            harmony?.UnpatchSelf();
            harmony = null;
            entitiesField = null;
            allEntitiesField = null;
            CellAabbCache.Clear();
            LastPrefabIndex.Clear();
        }

        private static void PrefixLevelGenerate() {
            previousLatencyMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            CellAabbCache.Clear();
            LastPrefabIndex.Clear();
        }

        private static void PostfixLevelGenerate() {
            GCSettings.LatencyMode = previousLatencyMode;
            GC.Collect(1, GCCollectionMode.Optimized, false, false);
        }

        public static bool FastAabbReject(VoronoiCell cell, Vector2 point) {
            if (cell == null) { return true; }
            if (!CellAabbCache.TryGetValue(cell, out var box)) {
                var edges = cell.Edges;
                if (edges == null || edges.Count == 0) { return true; }
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < edges.Count; i++) {
                    var e = edges[i];
                    var p1 = e.Point1;
                    var p2 = e.Point2;
                    if (p1.X < minX) minX = p1.X;
                    if (p1.X > maxX) maxX = p1.X;
                    if (p1.Y < minY) minY = p1.Y;
                    if (p1.Y > maxY) maxY = p1.Y;
                    if (p2.X < minX) minX = p2.X;
                    if (p2.X > maxX) maxX = p2.X;
                    if (p2.Y < minY) minY = p2.Y;
                    if (p2.Y > maxY) maxY = p2.Y;
                }
                box = new StrongBox<Vector4>(new Vector4(minX, minY, maxX, maxY));
                CellAabbCache.Add(cell, box);
            }
            ref var val = ref box.Value;
            return point.X < val.X || point.X > val.Z || point.Y < val.Y || point.Y > val.W;
        }

        private static bool PrefixIsPointInside(VoronoiCell __instance, Vector2 point, ref bool __result) {
            if (FastAabbReject(__instance, point)) {
                __result = false;
                return false;
            }
            return true;
        }

        private static bool PrefixGetTooCloseCells(Level __instance, List<VoronoiCell> emptyCells, float minDistance, ref List<VoronoiCell> __result) {
            if (minDistance <= 0.0f || emptyCells == null || emptyCells.Count == 0) {
                __result = new List<VoronoiCell>();
                return false;
            }
            var set = new HashSet<VoronoiCell>();
            var distinct = emptyCells.Distinct();
            foreach (var cell in distinct) {
                var close = __instance.GetTooCloseCells(cell.Center, minDistance);
                for (int i = 0; i < close.Count; i++) {
                    set.Add(close[i]);
                }
            }
            __result = set.ToList();
            return false;
        }

        private static bool PrefixInsertToList(MapEntity __instance) {
            if (__instance == null) { return false; }
            var list = MapEntity.MapEntityList;
            if (__instance.Sprite == null) {
                list.Add(__instance);
                return false;
            }

            if (__instance is Structure { DrawDamageEffect: true } structure) {
                float drawDepth = structure.SpriteDepth;
                int count = list.Count;
                int low = 0, high = count - 1, insertIdx = count;
                while (low <= high) {
                    int mid = (low + high) >> 1;
                    float midDepth = (list[mid] as Structure)?.SpriteDepth ?? 1.0f;
                    if (midDepth < drawDepth) {
                        insertIdx = mid;
                        high = mid - 1;
                    } else {
                        low = mid + 1;
                    }
                }
                list.Insert(insertIdx, __instance);
                return false;
            }

            var prefab = __instance.Prefab;
            if (prefab != null && LastPrefabIndex.TryGetValue(prefab, out int cachedIdx)) {
                if (cachedIdx < list.Count && list[cachedIdx]?.Prefab == prefab) {
                    list.Insert(cachedIdx + 1, __instance);
                    LastPrefabIndex[prefab] = cachedIdx + 1;
                    return false;
                }
            }

            int i = 0;
            while (i < list.Count) {
                i++;
                if (list[i - 1]?.Prefab == prefab) {
                    list.Insert(i, __instance);
                    if (prefab != null) { LastPrefabIndex[prefab] = i; }
                    return false;
                }
            }

#if CLIENT
            i = 0;
            while (i < list.Count) {
                i++;
                Sprite existingSprite = list[i - 1].Sprite;
                if (existingSprite == null) { continue; }
                if (existingSprite.Texture == __instance.Sprite.Texture) { break; }
            }
#endif
            list.Insert(i, __instance);
            if (prefab != null) { LastPrefabIndex[prefab] = i; }
            return false;
        }

        private static bool PrefixRemoveEntity(EntityGrid __instance, MapEntity entity) {
            if (entity == null) { return false; }
            if (entitiesField?.GetValue(__instance) is not List<MapEntity>[,] grid ||
                allEntitiesField?.GetValue(__instance) is not List<MapEntity> allEntities) {
                return true;
            }

            Rectangle rect = entity.Rect;
            Rectangle indices = __instance.GetIndices(rect);

            int maxX = grid.GetLength(0) - 1;
            int maxY = grid.GetLength(1) - 1;

            int startX = Math.Max(indices.X, 0);
            int endX = Math.Min(indices.Width, maxX);
            int startY = Math.Max(indices.Y, 0);
            int endY = Math.Min(indices.Height, maxY);

            bool removed = false;
            if (startX <= endX && startY <= endY) {
                for (int x = startX; x <= endX; x++) {
                    for (int y = startY; y <= endY; y++) {
                        var cell = grid[x, y];
                        if (cell != null && cell.Remove(entity)) {
                            removed = true;
                        }
                    }
                }
            }

            if (!removed) {
                for (int x = 0; x <= maxX; x++) {
                    for (int y = 0; y <= maxY; y++) {
                        var cell = grid[x, y];
                        if (cell != null && cell.Contains(entity)) {
                            cell.Remove(entity);
                        }
                    }
                }
            }

            allEntities.Remove(entity);
            return false;
        }

        private static bool PrefixCreateDroppedStacks(List<MapEntity> entities) {
            if (entities == null || entities.Count < 2) { return false; }
            const float MaxDist = 10.0f;
            const float CellSize = 32.0f;
            var grid = new Dictionary<long, List<Item>>();
            for (int i = 0; i < entities.Count; i++) {
                if (entities[i] is not Item item || item.Prefab.MaxStackSize <= 1 || item.body is not { Enabled: true }) { continue; }
                int cx = (int)MathF.Floor(item.Position.X / CellSize);
                int cy = (int)MathF.Floor(item.Position.Y / CellSize);
                long key = ((long)cx << 32) | (uint)cy;
                if (!grid.TryGetValue(key, out var list)) {
                    list = new List<Item>();
                    grid.Add(key, list);
                }
                list.Add(item);
            }

            var stack = new List<Item>();
            foreach (var kvp in grid) {
                var list = kvp.Value;
                for (int i = 0; i < list.Count; i++) {
                    Item item1 = list[i];
                    if (item1.DroppedStack.Any()) { continue; }
                    stack.Clear();
                    stack.Add(item1);

                    int cx = (int)MathF.Floor(item1.Position.X / CellSize);
                    int cy = (int)MathF.Floor(item1.Position.Y / CellSize);

                    for (int dx = -1; dx <= 1; dx++) {
                        for (int dy = -1; dy <= 1; dy++) {
                            long neighborKey = ((long)(cx + dx) << 32) | (uint)(cy + dy);
                            if (!grid.TryGetValue(neighborKey, out var neighborList)) { continue; }
                            for (int j = 0; j < neighborList.Count; j++) {
                                Item item2 = neighborList[j];
                                if (ReferenceEquals(item1, item2) || item2.DroppedStack.Any()) { continue; }
                                if (item1.Prefab != item2.Prefab) { continue; }
                                if (Math.Abs(item1.Position.X - item2.Position.X) > MaxDist) { continue; }
                                if (Math.Abs(item1.Position.Y - item2.Position.Y) > MaxDist) { continue; }
                                if (!stack.Contains(item2)) { stack.Add(item2); }
                            }
                        }
                    }

                    if (stack.Count > 1) {
                        item1.CreateDroppedStack(stack, allowClientExecute: true);
                    }
                }
            }
            return false;
        }
    }
}
