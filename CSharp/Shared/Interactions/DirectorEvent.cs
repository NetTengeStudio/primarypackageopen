using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

public sealed class DirectorEvent {
    public string Id { get; set; } = "";
    public DirectorKind Kind { get; set; }
    public string Target { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Text { get; set; } = "";
    public string Visual { get; set; } = "";
    public float Gain { get; set; } = .35f;
    public float Duration { get; set; } = 5;
    public float Interval { get; set; } = 2;
    public float Delay { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public ushort Submarine { get; set; }
    public float Intensity { get; set; } = .6f;
    public float Scale { get; set; } = 1;
    public float Speed { get; set; } = 80;
    public float NearDistance { get; set; } = 100;
    public float Radius { get; set; } = 500;
    public PhantomBehavior Behavior { get; set; }
    public DirectorEvent Copy() => (DirectorEvent)MemberwiseClone();
    public static void Validate(DirectorEvent value) {
        if (value == null || !Guid.TryParseExact(value.Id, "N", out _) || !Enum.IsDefined(value.Kind) || value.Target == null || value.Target.Length > 128 ||
            value.Resource == null || value.Resource.Length > 512 || value.Text == null || value.Text.Length > 512 || value.Visual == null || value.Visual.Length > 128 ||
            !float.IsFinite(value.Gain) || value.Gain < 0 || value.Gain > .7f || !float.IsFinite(value.Duration) || value.Duration < .25f || value.Duration > 60 ||
            !float.IsFinite(value.Interval) || value.Interval < .5f || value.Interval > 60 || !float.IsFinite(value.Delay) || value.Delay < 0 || value.Delay > 60) { throw new ArgumentException("Недопустимые параметры события"); }
        if (value.Kind == DirectorKind.Caption && !InteractionProtocol.ValidText(value.Text, 512)) { throw new ArgumentException("Введите сценарный текст"); }
        if (value.Text.Length > 0 && !InteractionProtocol.ValidText(value.Text, 512)) { throw new ArgumentException("Недопустимый сценарный текст"); }
        if (value.Kind is DirectorKind.Scare or DirectorKind.Hallucination && !CharacterPrefab.Prefabs.ContainsKey(value.Visual.ToIdentifier())) { throw new ArgumentException("Выберите изображение существа"); }
        if (!Enum.IsDefined(value.Behavior) || !float.IsFinite(value.X) || !float.IsFinite(value.Y) || Math.Abs(value.X) > 1e7f || Math.Abs(value.Y) > 1e7f ||
            !float.IsFinite(value.Intensity) || value.Intensity < 0 || value.Intensity > 1 || !float.IsFinite(value.Scale) || value.Scale < .1f || value.Scale > 4 ||
            !float.IsFinite(value.Speed) || value.Speed < 0 || value.Speed > 500 || !float.IsFinite(value.NearDistance) || value.NearDistance < 1 || value.NearDistance > 5000 || !float.IsFinite(value.Radius) || value.Radius < 10 || value.Radius > 5000) { throw new ArgumentException("Недопустимые параметры галлюцинации"); }
        if (value.Kind == DirectorKind.StopOne && !Guid.TryParseExact(value.Resource, "N", out _)) { throw new ArgumentException("Выберите активное событие"); }
        if (value.Kind is DirectorKind.Stop or DirectorKind.StopOne && value.Delay != 0) { throw new ArgumentException("Остановка выполняется немедленно"); }
        if ((value.Kind == DirectorKind.Sound || value.Kind is DirectorKind.Scare or DirectorKind.Hallucination or DirectorKind.SignalCollapse && value.Resource.Length > 0) && !DirectorCatalog.Sounds.ContainsKey(value.Resource)) { throw new ArgumentException("Звук отсутствует в каталоге ресурсов сервера"); }
    }
    internal static void Write(IWriteMessage output, DirectorEvent value) {
        output.WriteString(value.Id); output.WriteByte((byte)value.Kind); output.WriteString(value.Target);
        output.WriteString(value.Resource); output.WriteString(value.Text); output.WriteString(value.Visual); output.WriteSingle(value.Gain);
        output.WriteSingle(value.Duration); output.WriteSingle(value.Interval); output.WriteSingle(value.Delay);
        output.WriteSingle(value.X); output.WriteSingle(value.Y); output.WriteUInt16(value.Submarine); output.WriteSingle(value.Intensity); output.WriteSingle(value.Scale);
        output.WriteSingle(value.Speed); output.WriteSingle(value.NearDistance); output.WriteSingle(value.Radius); output.WriteByte((byte)value.Behavior);
    }
    internal static DirectorEvent Read(IReadMessage input) => new() {
        Id = input.ReadString(), Kind = (DirectorKind)input.ReadByte(), Target = input.ReadString(), Resource = input.ReadString(), Text = input.ReadString(), Visual = input.ReadString(),
        Gain = input.ReadSingle(), Duration = input.ReadSingle(), Interval = input.ReadSingle(), Delay = input.ReadSingle(),
        X = input.ReadSingle(), Y = input.ReadSingle(), Submarine = input.ReadUInt16(), Intensity = input.ReadSingle(), Scale = input.ReadSingle(),
        Speed = input.ReadSingle(), NearDistance = input.ReadSingle(), Radius = input.ReadSingle(), Behavior = (PhantomBehavior)input.ReadByte()
    };
}

internal static class DirectorCatalog {
    internal sealed class SoundEntry {
        public string Key, Path, Name;
    }
    public static readonly Dictionary<string, SoundEntry> Sounds = new(StringComparer.Ordinal);
    internal static string Describe(DirectorEvent value) {
        if (value.Kind == DirectorKind.SignalCollapse) { return "Signal Collapse"; }
        if (value.Kind == DirectorKind.Caption) { return value.Text; }
        if (value.Kind is DirectorKind.Scare or DirectorKind.Hallucination) {
            return (value.Kind == DirectorKind.Scare ? "Скример · " : "Галлюцинация · ") + (CharacterPrefab.Prefabs.TryGet(value.Visual.ToIdentifier(), out var creature) ? creature.Name : value.Visual);
        }
        return Sounds.TryGetValue(value.Resource, out var sound) ? sound.Name : "Звук";
    }
    public static void Load() {
        Sounds.Clear();
        foreach (var prefab in CharacterPrefab.Prefabs) { Add(prefab.ConfigElement, prefab.Name); }
        foreach (var package in ContentPackageManager.EnabledPackages.All) {
            foreach (var file in package.GetFiles<SoundsFile>()) {
                try {
                    var document = XDocument.Load(file.Path.Value);
                    if (document.Root != null) { Add(document.Root.FromPackage(package), package.Name); }
                } catch (Exception error) when (error is IOException or System.Xml.XmlException) { Core.ModLog.Error("Director catalog: " + error.Message); }
            }
        }
    }
    private static void Add(ContentXElement root, string group) {
        foreach (var element in root.Descendants()) {
            string raw = element.GetAttributeString("file", "");
            if (!raw.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) && !raw.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) { continue; }
            string path = ContentPath.FromRaw(root.ContentPackage, raw).Value;
            string key = raw.Replace('\\', '/');
            if (File.Exists(path)) { Sounds.TryAdd(key, new SoundEntry { Key = key, Path = path, Name = group + " / " + Path.GetFileNameWithoutExtension(path) }); }
        }
    }
}
