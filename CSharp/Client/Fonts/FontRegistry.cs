using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Barotrauma.LuaCs;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;
using HarmonyLib;

namespace PrimaryPackage.Fonts {
    public static class FontRegistry {
        public const string FolderName = "Content/Fonts";

        private readonly struct Key : IEquatable<Key> {
            private readonly Identifier name;
            private readonly uint size;
            private readonly bool dynamic;

            public Key(Identifier name, uint size, bool dynamic) {
                this.name = name;
                this.size = size;
                this.dynamic = dynamic;
            }

            public bool Equals(Key other) => name == other.name && size == other.size && dynamic == other.dynamic;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(name, size, dynamic);
        }

        private static readonly Dictionary<Identifier, string> files = new Dictionary<Identifier, string>();
        private static readonly Dictionary<Key, ScalableFont> cache = new Dictionary<Key, ScalableFont>();
        private static readonly List<Identifier> names = new List<Identifier>();
        private static bool indexed;
        private static bool hooked;
        public static int Generation { get; private set; }

        public static IReadOnlyList<Identifier> Names {
            get {
                EnsureIndexed();
                return names;
            }
        }

        public static void Index() {
            Unload();
            files.Clear();
            names.Clear();
            indexed = true;
            string dir = ModAssets.Resolve(FolderName);
            if (!Directory.Exists(dir)) { return; }
            foreach (string path in Directory.EnumerateFiles(dir)) {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)) { continue; }
                Identifier key = Path.GetFileNameWithoutExtension(path).ToIdentifier();
                if (files.ContainsKey(key)) { continue; }
                files.Add(key, path);
                names.Add(key);
            }
            names.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
        }

        public static bool Contains(Identifier name) {
            EnsureIndexed();
            return files.ContainsKey(name);
        }

        public static ScalableFont Get(Identifier name, uint size, bool dynamicLoading) {
            EnsureIndexed();
            var key = new Key(name, size, dynamicLoading);
            if (cache.TryGetValue(key, out ScalableFont cached)) { return cached; }
            if (!files.TryGetValue(name, out string path)) { return null; }
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            if (device == null) { return null; }
            ScalableFont font;
            try {
                font = new ScalableFont(path, size, device, dynamicLoading);
            }
            catch (Exception) {
                cache[key] = null;
                return null;
            }
            Hook(device);
            cache[key] = font;
            return font;
        }

        private static void EnsureIndexed() {
            if (!indexed) { Index(); }
        }

        private static void Hook(GraphicsDevice device) {
            if (hooked) { return; }
            device.DeviceReset += OnDeviceReset;
            hooked = true;
        }

        private static void OnDeviceReset(object sender, EventArgs e) => Unload();

        private static void Unload() {
            Generation++;
            foreach (ScalableFont font in cache.Values) { font?.Dispose(); }
            cache.Clear();
        }

        public static void Dispose() {
            if (hooked) {
                GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
                if (device != null) { device.DeviceReset -= OnDeviceReset; }
                hooked = false;
            }
            Unload();
            files.Clear();
            names.Clear();
            indexed = false;
        }
    }

    public sealed class FontRegistryPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) => FontRegistry.Index();
        public void Release() => FontRegistry.Dispose();
    }
}
