using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

internal sealed class PhantomRig : IDisposable {
    private sealed class Bone {
        internal Sprite Sprite;
        internal RagdollParams.LimbParams Parameters;
        internal int Parent = -1;
        internal Vector2 ParentAnchor, Anchor, Position;
        internal float Rotation, Scale;
    }
    private readonly Bone[] bones;
    private readonly int[] order;
    internal float BodyExtent { get; private set; }
    internal PhantomRig(CharacterPrefab prefab) {
        RagdollParams ragdoll = prefab.ConfigElement.GetAttributeBool("humanoid", false)
            ? RagdollParams.GetDefaultRagdollParams<HumanRagdollParams>(prefab.Identifier, prefab.ConfigElement, prefab.ContentPackage)
            : RagdollParams.GetDefaultRagdollParams<FishRagdollParams>(prefab.Identifier, prefab.ConfigElement, prefab.ContentPackage);
        if (ragdoll.Limbs.Count is < 1 or > 128) { throw new ArgumentException("Существо не поддерживается: от 1 до 128 частей модели"); }
        bones = new Bone[ragdoll.Limbs.Count]; order = new int[bones.Length];
        var indices = new Dictionary<int, int>(bones.Length);
        try {
            int root = 0;
            for (int i = 0; i < bones.Length; i++) {
                var limb = ragdoll.Limbs[i];
                var bone = bones[i] = new Bone { Parameters = limb, Scale = limb.Scale * ragdoll.LimbScale * ragdoll.TextureScale };
                indices.Add(limb.ID, i);
                if (limb.Type == LimbType.Torso) { root = i; }
                var sprite = limb.GetSprite();
                if (sprite == null || limb.Hide) { continue; }
                string path = ContentPath.FromRaw(sprite.Element.ContentPackage ?? prefab.ContentPackage, sprite.GetTexturePath()).Value;
                bone.Sprite = new Sprite(sprite.Element, file: path, sourceRectScale: ragdoll.SourceRectScale);
            }
            var placed = new bool[bones.Length];
            order[0] = root; placed[root] = true;
            int count = 1;
            for (int cursor = 0; cursor < count; cursor++) {
                int parent = order[cursor];
                foreach (var joint in ragdoll.Joints) {
                    if (!indices.TryGetValue(joint.Limb1, out int a) || !indices.TryGetValue(joint.Limb2, out int b)) { throw new ArgumentException("Некорректные соединения модели"); }
                    int child = a == parent ? b : b == parent ? a : -1;
                    if (child < 0 || placed[child]) { continue; }
                    Bone bone = bones[child]; bone.Parent = parent;
                    float scale = ragdoll.JointScale * joint.Scale;
                    bone.ParentAnchor = (a == parent ? joint.Limb1Anchor : joint.Limb2Anchor) * scale;
                    bone.Anchor = (a == parent ? joint.Limb2Anchor : joint.Limb1Anchor) * scale;
                    placed[child] = true; order[count++] = child;
                }
            }
            Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            for (int cursor = 0; cursor < order.Length; cursor++) {
                Bone bone = bones[order[cursor]];
                bone.Position = bone.Parent < 0 ? Vector2.Zero : bones[bone.Parent].Position + bone.ParentAnchor - bone.Anchor;
                if (bone.Sprite == null) { continue; }
                Vector2 size = bone.Sprite.size * bone.Scale;
                Vector2 origin = bone.Sprite.Origin * bone.Scale;
                min = Vector2.Min(min, bone.Position - origin); max = Vector2.Max(max, bone.Position - origin + size);
            }
            BodyExtent = Math.Max(max.X - min.X, max.Y - min.Y);
            if (!float.IsFinite(BodyExtent) || BodyExtent <= 0) { throw new ArgumentException("Модель не содержит видимых частей"); }
            if (count != bones.Length) { throw new ArgumentException("Модель содержит несвязанные части; выберите другое существо"); }
        } catch { Dispose(); throw; }
    }
    internal void Draw(SpriteBatch batch, Vector2 at, float scale, float facing, float phase, Color color, bool distorted, float intensity) {
        for (int cursor = 0; cursor < order.Length; cursor++) {
            Bone bone = bones[order[cursor]];
            float direction = bone.Parameters.Type switch { LimbType.LeftArm or LimbType.RightLeg or LimbType.LeftFoot or LimbType.Tail => 1, LimbType.RightArm or LimbType.LeftLeg or LimbType.RightFoot => -1, _ => 0 };
            bone.Rotation = direction * MathF.Sin(phase) * .25f;
            if (bone.Parent >= 0) {
                Bone parent = bones[bone.Parent];
                bone.Position = parent.Position + Rotate(bone.ParentAnchor, parent.Rotation) - Rotate(bone.Anchor, bone.Rotation);
            } else { bone.Position = Vector2.Zero; }
            Sprite sprite = bone.Sprite;
            if (sprite == null) { continue; }
            Vector2 position = at + new Vector2(bone.Position.X * facing, bone.Position.Y) * scale;
            if (distorted) { position.X += MathF.Sin(phase * 2 + cursor) * intensity * 4; }
            Vector2 origin = sprite.Origin;
            if (facing < 0) { origin.X = sprite.SourceRect.Width - origin.X; }
            sprite.Draw(batch, new Vector2(position.X, -position.Y), color, origin, rotate: -bone.Rotation * facing,
                scale: bone.Scale * scale, spriteEffect: facing < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                depth: .5f + (1 - bone.Parameters.GetSprite().Depth) * .0001f);
        }
    }
    private static Vector2 Rotate(Vector2 vector, float angle) {
        float c = MathF.Cos(angle), s = MathF.Sin(angle); return new Vector2(vector.X * c - vector.Y * s, vector.X * s + vector.Y * c);
    }
    public void Dispose() { if (bones == null) { return; } for (int i = 0; i < bones.Length; i++) { bones[i]?.Sprite?.Remove(); } }
}

internal static class PhantomRenderer {
    private static readonly List<Instance> active = new(16);
    internal sealed class Instance : IDisposable {
        internal readonly DirectorEvent Event;
        internal readonly PhantomRig Rig;
        internal readonly PhantomMotion Motion;
        internal readonly Submarine Submarine;
        internal readonly double Start;
        internal Instance(DirectorEvent value, double start) {
            Event = value; Start = start;
            Submarine = Entity.FindEntityByID(value.Submarine) as Submarine;
            if (value.Submarine != 0 && Submarine is not { Removed: false }) { throw new ArgumentException("Подлодка галлюцинации недоступна"); }
            Rig = new PhantomRig(CharacterPrefab.Prefabs[value.Visual.ToIdentifier()]);
            try { Motion = new PhantomMotion(new Vector2(value.X, value.Y), value.Radius, value.Speed, value.Behavior, start, value.NearDistance, Rig.BodyExtent * value.Scale); }
            catch { Rig.Dispose(); throw; }
            active.Add(this);
        }
        internal bool Update(double now) {
            if (Submarine is { Removed: true }) { return false; }
            Vector2 observer = Character.Controlled is { Removed: false } player ? player.WorldPosition : GameMain.GameScreen.Cam.Position;
            Vector2 target = observer - (Submarine?.WorldPosition ?? Vector2.Zero);
            Motion.Advance(now, new Vector2(target.X, target.Y), Event.Intensity);
            return !Motion.Vanished;
        }
        internal void Draw(SpriteBatch batch, double now) {
            if (now < Start || Motion.Vanished) { return; }
            float alpha = (float)Math.Clamp(Math.Min((now - Start) / .35, (Start + Event.Duration - now) / .5), 0, 1);
            Vector2 frame = Submarine?.WorldPosition ?? Vector2.Zero;
            bool lagged = Event.Behavior == PhantomBehavior.Lagged;
            if (lagged) {
                Vector2 ghost = new Vector2(Motion.Ghost.X, Motion.Ghost.Y) + frame;
                Rig.Draw(batch, ghost, Event.Scale, Motion.GhostFacing, Motion.GhostPhase, new Color(35, 30, 40) * (alpha * Event.Intensity * .25f), true, Event.Intensity);
            }
            Vector2 position = new Vector2(Motion.Displayed.X, Motion.Displayed.Y) + frame;
            Rig.Draw(batch, position, Event.Scale, Motion.DisplayedFacing, Motion.DisplayedPhase, (lagged ? new Color(3, 3, 5) : Color.White) * alpha, lagged, Event.Intensity);
        }
        public void Dispose() { active.Remove(this); Rig.Dispose(); }
    }
    internal static void Attach(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawFront)), prefix: ModuleHost.Bind(typeof(PhantomRenderer), nameof(Draw)));
    // This batch targets the world before foreground structures, lightmap multiplication and LOS composition.
    private static void Draw(SpriteBatch spriteBatch, bool editing) {
        if (editing || Screen.Selected != GameMain.GameScreen) { return; }
        double now = GUIRuntime.Now;
        for (int i = 0; i < active.Count; i++) { active[i].Draw(spriteBatch, now); }
    }
}
