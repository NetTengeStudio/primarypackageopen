using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Zones {
    public abstract class ZoneComponent : ItemComponent {
        protected const float DetectInterval = 0.25f;

        protected readonly List<Hull> hulls = new List<Hull>();
        protected readonly List<Character> occupants = new List<Character>();
        private Rectangle cachedRect;
        private Submarine cachedSub;
        private bool hullsCached;
        private float detectTimer;

        protected ZoneComponent(Item item, ContentXElement element) : base(item, element) {
            IsActive = true;
        }

        public override void Update(float deltaTime, Camera cam) {
            detectTimer -= deltaTime;
            if (detectTimer <= 0.0f) {
                detectTimer = DetectInterval;
                RefreshOccupants();
                OnOccupantsRefreshed();
            }
            UpdateZone(deltaTime);
        }

        protected virtual void OnOccupantsRefreshed() {}
        protected virtual void UpdateZone(float deltaTime) {}

        private void RefreshHulls() {
            if (hullsCached && item.Rect == cachedRect && item.Submarine == cachedSub) { return; }
            hullsCached = true;
            cachedRect = item.Rect;
            cachedSub = item.Submarine;
            hulls.Clear();
            Rectangle worldRect = item.WorldRect;
            foreach (Hull h in Hull.HullList) {
                Rectangle hullRect = h.WorldRect;
                if (worldRect.X < hullRect.X + hullRect.Width && hullRect.X < worldRect.X + worldRect.Width &&
                    worldRect.Y - worldRect.Height < hullRect.Y && hullRect.Y - hullRect.Height < worldRect.Y) {
                    hulls.Add(h);
                }
            }
        }

        private void RefreshOccupants() {
            RefreshHulls();
            occupants.Clear();
            Rectangle worldRect = item.WorldRect;
            foreach (Character c in Character.CharacterList) {
                if (c.Removed || c.IsDead || !c.Enabled) { continue; }
                if (hulls.Count > 0 ? !hulls.Contains(c.CurrentHull) : c.CurrentHull != null) { continue; }
                if (!Submarine.RectContains(worldRect, c.WorldPosition)) { continue; }
                occupants.Add(c);
            }
        }

        protected Hull FindZoneHull(Vector2 worldPos) {
            foreach (Hull h in hulls) {
                if (Submarine.RectContains(h.WorldRect, worldPos)) { return h; }
            }
            return null;
        }
    }
}
