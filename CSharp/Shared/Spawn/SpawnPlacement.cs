using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Spawn {
    public static class SpawnPlacement {
        private struct Seating {
            public Character Character;
            public Item Furniture;
            public double Until;
        }

        private static readonly Dictionary<WayPoint, Character> claims = new Dictionary<WayPoint, Character>();
        private static readonly List<Seating> seatings = new List<Seating>();

        public static int Displaced { get; private set; }
        public static int Seated { get; private set; }

        public static void Reset() {
            claims.Clear();
            seatings.Clear();
            Displaced = 0;
            Seated = 0;
        }

        public static void OnCharacterCreated(Character character, Vector2 position) {
            if (character == null || character.Removed || !character.IsHuman) { return; }
            if (GameMain.GameSession == null || GameMain.NetworkMember is { IsClient: true }) { return; }

            WayPoint waypoint = Match(position);
            if (waypoint == null) { return; }

            if (claims.TryGetValue(waypoint, out Character holder) && holder != null && !holder.Removed && holder != character) {
                WayPoint free = FindFree(waypoint);
                if (free != null) {
                    waypoint = free;
                    character.TeleportTo(waypoint.WorldPosition);
                    Displaced++;
                }
            }
            claims[waypoint] = character;

            if (!SpawnConfig.HasSleepTag(waypoint)) { return; }
            Item furniture = FindSeat(waypoint.WorldPosition);
            if (furniture == null) { return; }
            Sit(character, furniture);
            seatings.Add(new Seating {
                Character = character,
                Furniture = furniture,
                Until = Timing.TotalTime + SpawnConfig.SeatRetrySeconds
            });
        }

        public static void Update() {
            for (int i = seatings.Count - 1; i >= 0; i--) {
                Seating seating = seatings[i];
                if (seating.Character == null || seating.Character.Removed || seating.Furniture == null ||
                    seating.Furniture.Removed || Timing.TotalTime >= seating.Until) {
                    seatings.RemoveAt(i);
                    continue;
                }
                if (seating.Character.IsAnySelectedItem(seating.Furniture)) { continue; }
                Sit(seating.Character, seating.Furniture);
            }
        }

        private static void Sit(Character character, Item furniture) {
            if (furniture.TryInteract(character, ignoreRequiredItems: true, forceSelectKey: true)) { Seated++; }
        }

        private static WayPoint Match(Vector2 worldPosition) {
            float best = SpawnConfig.WaypointMatchRadius * SpawnConfig.WaypointMatchRadius;
            WayPoint match = null;
            for (int i = 0; i < WayPoint.WayPointList.Count; i++) {
                WayPoint wp = WayPoint.WayPointList[i];
                if (wp == null || wp.Removed || wp.SpawnType != SpawnType.Human) { continue; }
                float distSq = Vector2.DistanceSquared(wp.WorldPosition, worldPosition);
                if (distSq >= best) { continue; }
                best = distSq;
                match = wp;
            }
            return match;
        }

        private static WayPoint FindFree(WayPoint taken) {
            WayPoint fallback = null;
            for (int i = 0; i < WayPoint.WayPointList.Count; i++) {
                WayPoint wp = WayPoint.WayPointList[i];
                if (wp == null || wp.Removed || wp == taken) { continue; }
                if (wp.SpawnType != SpawnType.Human || wp.Submarine != taken.Submarine) { continue; }
                if (claims.TryGetValue(wp, out Character holder) && holder != null && !holder.Removed) { continue; }
                if (wp.CurrentHull != null && wp.CurrentHull == taken.CurrentHull) { return wp; }
                fallback ??= wp;
            }
            return fallback;
        }

        public static Item FindSeat(Vector2 worldPosition) {
            float rangeSq = SpawnConfig.SleepSearchRadius * SpawnConfig.SleepSearchRadius;
            Item best = null;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < Item.ItemList.Count; i++) {
                Item item = Item.ItemList[i];
                if (item == null || item.Removed || item.NonInteractable) { continue; }
                if (item.GetComponent<Controller>() is not { ControlCharacterPose: true, CanBeSelectedByCharacters: true, IsSecondaryItem: true }) { continue; }
                float distSq = Vector2.DistanceSquared(item.WorldPosition, worldPosition);
                if (distSq > rangeSq || distSq >= bestDistSq) { continue; }
                bestDistSq = distSq;
                best = item;
            }
            return best;
        }
    }
}
