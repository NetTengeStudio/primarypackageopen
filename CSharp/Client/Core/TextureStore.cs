using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Core;

public static class TextureStore {
    private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
    private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
    private static readonly HashSet<string> rejected = new HashSet<string>();
    private static readonly List<Sprite> borrowed = new List<Sprite>();

    public static Texture2D File(string relativePath) {
        if (textures.TryGetValue(relativePath, out Texture2D cached)) {
            Texture2D alive = Live(cached);
            if (alive != null) { return alive; }
            textures.Remove(relativePath);
        }
        if (rejected.Contains(relativePath)) { return null; }
        if (TextureLoader.PlaceHolderTexture == null) { return null; }
        string absolute = ModAssets.Resolve(relativePath);
        if (absolute.Length == 0) { return null; }
        Texture2D loaded;
        try {
            loaded = TextureLoader.FromFile(absolute, compress: false);
        } catch (Exception) {
            Reject(relativePath);
            return null;
        }
        if (loaded == null) {
            Reject(relativePath);
            return null;
        }
        textures[relativePath] = loaded;
        return loaded;
    }

    public static Texture2D Generated<TSource>(string key, TSource source, Func<TSource, Texture2D> build) {
        if (textures.TryGetValue(key, out Texture2D cached)) {
            Texture2D alive = Live(cached);
            if (alive != null) { return alive; }
            textures.Remove(key);
        }
        Texture2D created = build(source);
        if (created == null) { return null; }
        textures[key] = created;
        return created;
    }

    public static void Adopt(string key, Texture2D texture) {
        if (string.IsNullOrEmpty(key) || texture == null) { return; }
        if (textures.TryGetValue(key, out Texture2D previous) && !ReferenceEquals(previous, texture)) {
            previous?.Dispose();
        }
        textures[key] = texture;
    }

    public static Sprite Art(string relativePath) => Art(relativePath, null);

    public static Sprite Art(string relativePath, Vector2 relativeOrigin) {
        Sprite sprite = Art(relativePath, null);
        if (sprite != null) { sprite.RelativeOrigin = relativeOrigin; }
        return sprite;
    }

    public static Sprite Art(string relativePath, Rectangle? source) {
        string key = source.HasValue ? relativePath + source.Value : relativePath;
        if (sprites.TryGetValue(key, out Sprite cached) && cached.Texture != null && !cached.Texture.IsDisposed) {
            return cached;
        }
        if (rejected.Contains(relativePath)) { return null; }
        if (TextureLoader.PlaceHolderTexture == null) { return null; }
        string absolute = ModAssets.Resolve(relativePath);
        if (absolute.Length == 0) { return null; }
        Sprite built = new Sprite(absolute, source, null);
        if (built.Texture == null) {
            built.Remove();
            Reject(relativePath);
            return null;
        }
        sprites[key] = built;
        return built;
    }

    public static Sprite Borrow(Texture2D texture, Rectangle? source, Vector2? origin) {
        if (texture == null || texture.IsDisposed) { return null; }
        Sprite sprite = new Sprite(texture, source, null);
        if (origin.HasValue) { sprite.Origin = origin.Value; }
        borrowed.Add(sprite);
        return sprite;
    }

    public static void Detach(Sprite sprite) {
        if (sprite == null) { return; }
        borrowed.Remove(sprite);
        sprite.texture = null;
        sprite.Remove();
    }

    public static void Release() {
        for (int i = borrowed.Count - 1; i >= 0; i--) {
            borrowed[i].texture = null;
            borrowed[i].Remove();
        }
        borrowed.Clear();

        foreach (KeyValuePair<string, Sprite> entry in sprites) { entry.Value?.Remove(); }
        sprites.Clear();
        foreach (KeyValuePair<string, Texture2D> entry in textures) {
            Texture2D texture = entry.Value;
            if (texture != null && !texture.IsDisposed) { texture.Dispose(); }
        }
        textures.Clear();
        rejected.Clear();
    }

    private static void Reject(string relativePath) {
        rejected.Add(relativePath);
        ModLog.Error($"{relativePath} cannot be loaded");
    }

    private static Texture2D Live(Texture2D texture) =>
        texture != null && !texture.IsDisposed ? texture : null;
}
