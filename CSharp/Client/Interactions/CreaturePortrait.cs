using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;
using GUIComponent = PrimaryPackage.GUI.GUIComponent;

namespace PrimaryPackage.Interactions;

internal sealed class CreaturePortrait : GUIComponent {
    private readonly Sprite sprite;
    internal CreaturePortrait(CharacterPrefab prefab) { Height = 160; sprite = Load(prefab); }
    internal static Sprite Load(CharacterPrefab prefab) {
        RagdollParams ragdoll = prefab.ConfigElement.GetAttributeBool("humanoid", false)
            ? RagdollParams.GetDefaultRagdollParams<HumanRagdollParams>(prefab.Identifier, prefab.ConfigElement, prefab.ContentPackage)
            : RagdollParams.GetDefaultRagdollParams<FishRagdollParams>(prefab.Identifier, prefab.ConfigElement, prefab.ContentPackage);
        RagdollParams.SpriteParams selected = null;
        foreach (var limb in ragdoll.Limbs) {
            var candidate = limb.GetSprite();
            if (candidate == null) { continue; }
            if (selected == null || limb.Type == LimbType.Head) { selected = candidate; }
            if (limb.Type == LimbType.Head) { break; }
        }
        if (selected == null) { throw new ArgumentException("У существа нет изображения для предпросмотра"); }
        string path = ContentPath.FromRaw(selected.Element.ContentPackage ?? prefab.ContentPackage, selected.GetTexturePath()).Value;
        return new Sprite(selected.Element, file: path, sourceRectScale: ragdoll.SourceRectScale);
    }
    internal override void Paint(GUIRenderer renderer, float opacity) => renderer.Image(sprite, Bounds, opacity);
    public override void Dispose() { if (Disposed) { return; } base.Dispose(); sprite.Remove(); }
}
