using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Fonts {
    public readonly struct FontHandle {
        private readonly GUIFont style;
        private readonly ScalableFont file;

        public FontHandle(GUIFont font) {
            style = font;
            file = null;
        }

        public FontHandle(ScalableFont font) {
            style = null;
            file = font;
        }

        public bool HasValue => file != null || (style != null && style.HasValue);

        public uint Size => file?.Size ?? style?.Size ?? 0u;

        public Vector2 MeasureString(string text) =>
            file != null ? file.MeasureString(text) : style?.MeasureString(text) ?? Vector2.Zero;

        public string WrapText(string text, float width) =>
            file != null ? file.WrapText(text, width) : style?.WrapText(text, width) ?? text;

        public void DrawString(SpriteBatch spriteBatch, string text, Vector2 position, Color color,
                               float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float depth) {
            if (file != null) {
                file.DrawString(spriteBatch, text, position, color, rotation, origin, scale, effects, depth);
            } else {
                style?.DrawString(spriteBatch, text, position, color, rotation, origin, scale, effects, depth);
            }
        }
    }

    public readonly struct FontRef {
        public const string StylePrefix = "style:";
        public const string FilePrefix = "file:";
        public const string Default = StylePrefix + "Font";

        public readonly bool IsFile;
        public readonly Identifier Name;

        private FontRef(bool isFile, Identifier name) {
            IsFile = isFile;
            Name = name;
        }

        public static FontRef Parse(string value) {
            if (string.IsNullOrWhiteSpace(value)) { return new FontRef(false, "Font".ToIdentifier()); }
            value = value.Trim();
            if (value.StartsWith(FilePrefix, System.StringComparison.OrdinalIgnoreCase)) {
                return new FontRef(true, value.Substring(FilePrefix.Length).ToIdentifier());
            }
            if (value.StartsWith(StylePrefix, System.StringComparison.OrdinalIgnoreCase)) {
                return new FontRef(false, value.Substring(StylePrefix.Length).ToIdentifier());
            }
            return new FontRef(false, value.ToIdentifier());
        }

        public static string Compose(bool isFile, Identifier name) =>
            (isFile ? FilePrefix : StylePrefix) + name.Value;
    }

    public static class FontResolver {
        private static readonly uint[] buckets = { 16, 24, 32, 48, 64 };

        public static uint Bucket(float desired) {
            foreach (uint bucket in buckets) {
                if (desired <= bucket) { return bucket; }
            }
            return buckets[buckets.Length - 1];
        }

        public static GUIFont Style(Identifier name) =>
            GUIStyle.Fonts.TryGetValue(name, out GUIFont font) ? font : GUIStyle.Font;

        public static bool TryFile(Identifier name, float desiredHeight, out FontHandle handle, out float drawScale) {
            handle = default;
            drawScale = 1.0f;
            uint size = Bucket(desiredHeight);
            ScalableFont font = FontRegistry.Get(name, size, dynamicLoading: true);
            if (font == null || font.Size == 0) { return false; }
            handle = new FontHandle(font);
            drawScale = desiredHeight / font.Size;
            return true;
        }
    }
}
