using System.Collections.Generic;
using Barotrauma.Sounds;

namespace PrimaryPackage.Core;

public static class SoundStore {
    private static readonly Dictionary<string, Sound> sounds = new Dictionary<string, Sound>();
    private static readonly HashSet<string> rejected = new HashSet<string>();

    public static Sound Get(string relativePath) => Absolute(ModAssets.Resolve(relativePath));

    public static Sound Absolute(string absolutePath) {
        if (string.IsNullOrEmpty(absolutePath)) { return null; }
        if (sounds.TryGetValue(absolutePath, out Sound cached)) {
            if (!cached.Disposed) { return cached.Loading ? null : cached; }
            sounds.Remove(absolutePath);
        }
        if (rejected.Contains(absolutePath)) { return null; }
        SoundManager manager = GameMain.SoundManager;
        if (manager == null || manager.Disabled) { return null; }
        Sound loaded = manager.LoadSound(absolutePath, stream: false);
        if (loaded == null) {
            rejected.Add(absolutePath);
            ModLog.Error($"{absolutePath} cannot be loaded");
            return null;
        }
        sounds[absolutePath] = loaded;
        return loaded.Loading ? null : loaded;
    }

    public static void Warm(IReadOnlyList<string> relativePaths) {
        if (relativePaths == null) { return; }
        for (int i = 0; i < relativePaths.Count; i++) { Get(relativePaths[i]); }
    }

    public static void Release() {
        foreach (KeyValuePair<string, Sound> entry in sounds) {
            Sound sound = entry.Value;
            if (sound != null && !sound.Disposed) { sound.Dispose(); }
        }
        sounds.Clear();
        rejected.Clear();
    }
}
