#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace PrimaryPackage.RPWheel
{
    public enum EmoteCategory : byte { Vocal = 0, Hands = 1 }

    public sealed class EmoteDef
    {
        public byte          Id;
        public string        Key      = "";
        public string        NameKey  = "";
        public EmoteCategory Category;
        public string        SoundRel = "";

        public bool HasSound => SoundRel.Length > 0;
    }

    public static class EmoteCatalog
    {
        private static EmoteDef[] _defs = Array.Empty<EmoteDef>();

        public static EmoteDef[] Defs   => _defs;
        public static int        Count  => _defs.Length;
        public static int        MaxIndex => _defs.Length - 1;

        public static EmoteDef? Get(int id) => (uint)id < (uint)_defs.Length ? _defs[id] : null;

        public const string CatalogPath = "Content/Systems/RPWheel/Definitions/emotes.xml";

        public static void Load()
        {
            var list = new List<EmoteDef>();
            PrimaryPackage.Core.ModPrefs.Read(CatalogPath, root =>
            {
                foreach (var el in root?.Elements("Emote") ?? System.Linq.Enumerable.Empty<XElement>())
                {
                    string id = ((string?)el.Attribute("id") ?? "").Trim();
                    if (id.Length == 0) continue;

                    var def = new EmoteDef
                    {
                        Id       = (byte)list.Count,
                        Key      = id,
                        NameKey  = ((string?)el.Attribute("name")  ?? id).Trim(),
                        Category = ParseCategory((string?)el.Attribute("category")),
                        SoundRel = ((string?)el.Attribute("sound") ?? "").Trim(),
                    };
                    list.Add(def);
                    if (list.Count >= 256) break;
                }
            });
            _defs = list.ToArray();
        }

        private static EmoteCategory ParseCategory(string? s)
            => (s ?? "").Trim().ToLowerInvariant() == "hands" ? EmoteCategory.Hands : EmoteCategory.Vocal;
    }
}
