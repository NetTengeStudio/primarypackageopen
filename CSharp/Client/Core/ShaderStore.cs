using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Core;

public static class ShaderStore {
    private const int Signature = 0x5846474D;
    private const byte Container = 8;

    private static readonly Dictionary<string, Effect> effects = new Dictionary<string, Effect>();
    private static readonly HashSet<string> rejected = new HashSet<string>();

    public static Effect File(GraphicsDevice graphics, string relativePath) {
        if (graphics == null || string.IsNullOrEmpty(relativePath)) { return null; }
        if (effects.TryGetValue(relativePath, out Effect cached)) {
            if (cached != null && !cached.IsDisposed) { return cached; }
            effects.Remove(relativePath);
        }
        if (rejected.Contains(relativePath)) { return null; }
        string absolute = ModAssets.Resolve(relativePath);
        if (absolute.Length == 0) { return Reject(relativePath); }
        byte[] blob;
        try {
            blob = System.IO.File.ReadAllBytes(absolute);
        } catch (Exception) {
            return Reject(relativePath);
        }
        if (!Accepts(blob)) { return Reject(relativePath); }
        Effect built;
        try {
            built = new Effect(graphics, blob);
        } catch (Exception) {
            return Reject(relativePath);
        }
        effects[relativePath] = built;
        return built;
    }

    public static void Release() {
        foreach (KeyValuePair<string, Effect> entry in effects) {
            try { entry.Value?.Dispose(); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("ShaderStore.1", noted); }
        }
        effects.Clear();
        rejected.Clear();
    }

    private static bool Accepts(byte[] blob) =>
        blob != null && blob.Length > 6 && BitConverter.ToInt32(blob, 0) == Signature && blob[4] == Container;

    private static Effect Reject(string relativePath) {
        rejected.Add(relativePath);
        ModLog.Once($"{relativePath} shader does not load");
        return null;
    }
}
