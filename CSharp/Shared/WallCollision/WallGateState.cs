using System.Collections.Generic;
using System.Runtime.InteropServices;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    
    
    
    
    internal sealed class WallGateState {
        private struct Crossing {
            internal object Owner;
            internal Body Body;
            internal Character Character;
            internal long CheckedStep;
        }

        private readonly object sync = new object();
        private readonly Dictionary<object, int> indices = new Dictionary<object, int>(4, ReferenceEqualityComparer.Instance);
        private readonly List<Crossing> crossings = new List<Crossing>(4);
        private Body reference;
        private Vector2 localNormal;
        private WallBounds wallBounds;

        internal bool HasCrossings { get { lock (sync) { return crossings.Count != 0; } } }

        internal void Configure(Body reference, WallFace face, in WallBounds bounds) {
            lock (sync) {
                this.reference = reference;
                localNormal = new WallPlane(face).LocalNormal;
                wallBounds = bounds;
                var entries = CollectionsMarshal.AsSpan(crossings);
                for (int i = 0; i < entries.Length; i++) { entries[i].CheckedStep = -1; }
            }
        }

        internal bool AllowCrossing(Body other) {
            if (!Live(other)) { return false; }
            Character character = Owner(other);
            if (character != null && (character.Removed || character.AnimController == null)) { return false; }
            object owner = (object)character ?? other;
            lock (sync) {
                if (reference == null || reference.World != other.World) { return false; }
                Vector2 origin = reference.Position;
                Vector2 normal = reference.GetWorldVector(localNormal);
                long step = WallContacts.PhysicsStep;
                if (indices.TryGetValue(owner, out int index)) {
                    ref Crossing entry = ref CollectionsMarshal.AsSpan(crossings)[index];
                    if (entry.CheckedStep == step) { return true; }
                    if (Overlaps(entry.Body, entry.Character, origin, normal)) {
                        entry.CheckedStep = step;
                        return true;
                    }
                    RemoveAt(index);
                }
                Body anchor = Anchor(character) ?? other;
                float side = Vector2.Dot(anchor.WorldCenter - origin, normal);
                if (side <= (wallBounds.Min.X + wallBounds.Max.X) * 0.5f) { return false; }
                
                
                if (Overlaps(other, character, origin, normal)) {
                    indices.Add(owner, crossings.Count);
                    crossings.Add(new Crossing { Owner = owner, Body = other, Character = character, CheckedStep = step });
                }
                return true;
            }
        }

        internal void Sweep(World world) {
            lock (sync) {
                if (reference == null) { return; }
                Vector2 origin = reference.Position;
                Vector2 normal = reference.GetWorldVector(localNormal);
                for (int i = crossings.Count - 1; i >= 0; i--) {
                    Crossing entry = crossings[i];
                    if (reference.World != world || !Overlaps(entry.Body, entry.Character, origin, normal)) { RemoveAt(i); }
                }
            }
        }

        internal void Clear() {
            lock (sync) { indices.Clear(); crossings.Clear(); reference = null; }
        }

        private bool Overlaps(Body body, Character character, Vector2 origin, Vector2 normal) {
            WallBounds subject = default;
            if (character != null) {
                if (character.Removed || character.AnimController == null) { return false; }
                Body anchor = Anchor(character);
                Include(ref subject, anchor, origin, normal);
                Limb[] limbs = character.AnimController.Limbs;
                if (limbs != null) {
                    for (int i = 0; i < limbs.Length; i++) {
                        Limb limb = limbs[i];
                        if (limb == null || limb.IsSevered) { continue; }
                        Body limbBody = limb.body?.FarseerBody;
                        if (!ReferenceEquals(limbBody, anchor)) { Include(ref subject, limbBody, origin, normal); }
                    }
                }
            }
            else { Include(ref subject, body, origin, normal); }
            return wallBounds.Overlaps(in subject);
        }

        private void Include(ref WallBounds bounds, Body body, Vector2 origin, Vector2 normal) {
            if (Live(body) && body.World == reference.World) { bounds.Include(body, origin, normal, wallOnly: false); }
        }

        private void RemoveAt(int index) {
            int last = crossings.Count - 1;
            indices.Remove(crossings[index].Owner);
            if (index != last) {
                Crossing moved = crossings[last];
                crossings[index] = moved;
                indices[moved.Owner] = index;
            }
            crossings.RemoveAt(last);
        }

        private static bool Live(Body body) => body != null && body.Enabled && body.BodyType == FarseerPhysics.BodyType.Dynamic;

        private static Character Owner(Body body) => body.UserData switch {
            Character character => character,
            Limb limb when !limb.IsSevered => limb.character,
            _ => null
        };

        private static Body Anchor(Character character) {
            var ragdoll = character?.AnimController;
            Body collider = ragdoll?.Collider?.FarseerBody;
            return Live(collider) ? collider : ragdoll?.MainLimb?.body?.FarseerBody;
        }
    }
}
