using System;
using System.IO;
using PrimaryPackage.Core;
using PrimaryPackage.Emotions;

namespace PrimaryPackage.RPWheel;

public enum EmoteCategory : byte { Vocal, Hands }
public sealed class EmoteDef {
    public byte Id;
    public string Key, Title, Bubble;
    public EmoteCategory Category;
}

public static class EmoteCatalog {
    private static EmoteDef[] definitions = Array.Empty<EmoteDef>();
    internal static Emotions.EmoteCatalog Source { get; private set; }
    internal static string Fingerprint { get; private set; } = "";
    public static EmoteDef[] Defs => definitions;
    public static int Count => definitions.Length;
    public static int MaxIndex => Count - 1;
    public const string CatalogPath = "Content/Systems/RPWheel/Definitions/emotes.xml";
    public static EmoteDef Get(int id) => (uint)id < (uint)definitions.Length ? definitions[id] : null;
    public static void Load() {
        var catalog = Emotions.EmoteCatalog.Parse(File.ReadAllText(ModAssets.Resolve(CatalogPath)), File.ReadAllText(ModAssets.Resolve("Content/Systems/RPWheel/Emotions/Sounds/manifest.json")));
        if (catalog.Count == 0 || catalog.Problems.Count > 0) { throw new InvalidDataException("Invalid emotion catalog: " + string.Join("; ", catalog.Problems)); }
        var next = new EmoteDef[catalog.Count];
        for (int i = 0; i < next.Length; i++) {
            var definition = catalog[i];
            next[i] = new EmoteDef { Id = definition.Index, Key = definition.Id, Title = definition.Title, Bubble = definition.Bubble, Category = definition.Kind == EmoteKind.Hands ? EmoteCategory.Hands : EmoteCategory.Vocal };
        }
        definitions = next; Source = catalog; Fingerprint = Convert.ToHexString(catalog.Fingerprint);
    }
}
