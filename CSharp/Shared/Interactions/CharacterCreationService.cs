using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

public sealed class CharacterTemplate {
    public string Name { get; set; } = "";
    public string Job { get; set; } = "assistant";
    public int Head { get; set; }
    public int Hair { get; set; }
    public int Beard { get; set; }
    public int Moustache { get; set; }
    public int Attachment { get; set; }
    public int SkinColor { get; set; }
    public int HairColor { get; set; }
    public int FacialHairColor { get; set; }
    public bool Control { get; set; }
}

public sealed class CharacterCreationService {
    private readonly HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);
    public static CharacterCreationService Instance { get; } = new();
    public CharacterInfo CreateInfo(CharacterTemplate template) => BuildInfo(template, true);
    public CharacterInfo CreatePreview(CharacterTemplate template) => BuildInfo(template, false);
    private CharacterInfo BuildInfo(CharacterTemplate template, bool validateIdentity) {
        if (template == null || validateIdentity && (!InteractionProtocol.ValidText(template.Name, 40) || template.Name != template.Name.Trim())) { throw new ArgumentException("Имя должно содержать от 1 до 40 символов без управляющих знаков"); }
        if (!JobPrefab.Prefabs.TryGet(template.Job.ToIdentifier(), out var job)) { throw new ArgumentException("Профессия не найдена"); }
        var info = new CharacterInfo(CharacterPrefab.HumanSpeciesName, validateIdentity ? template.Name : "Предпросмотр", jobOrJobPrefab: job);
        try {
            if ((uint)template.Head >= (uint)info.Prefab.Heads.Length) { throw new ArgumentException("Вариант внешности не найден"); }
            info.RecreateHead(info.Prefab.Heads[template.Head].TagSet, 0, 0, 0, 0);
            if (template.Hair < 0 || template.Beard < 0 || template.Moustache < 0 || template.Attachment < 0 ||
                template.Hair >= Math.Max(1, info.Hairs.Count) || template.Beard >= Math.Max(1, info.Beards.Count) ||
                template.Moustache >= Math.Max(1, info.Moustaches.Count) || template.Attachment >= Math.Max(1, info.FaceAttachments.Count)) { throw new ArgumentException("Недопустимая настройка внешности"); }
            info.RecreateHead(info.Prefab.Heads[template.Head].TagSet, template.Hair, template.Beard, template.Moustache, template.Attachment);
            if ((uint)template.SkinColor >= (uint)info.SkinColors.Length || (uint)template.HairColor >= (uint)info.HairColors.Length || (uint)template.FacialHairColor >= (uint)info.FacialHairColors.Length) { throw new ArgumentException("Недопустимый цвет внешности"); }
            info.Head.SkinColor = info.SkinColors[template.SkinColor].Color;
            info.Head.HairColor = info.HairColors[template.HairColor].Color;
            info.Head.FacialHairColor = info.FacialHairColors[template.FacialHairColor].Color;
            return info;
        } catch { info.Remove(); throw; }
    }
    public void Spawn(CharacterTemplate template, Vector2 position, Action<Character> onSpawned) {
        if (GameMain.NetworkMember is { IsClient: true }) { throw new InvalidOperationException("Character creation is server owned"); }
        if (GameMain.GameSession == null || !GameMain.GameSession.IsRunning || Entity.Spawner == null ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) || Math.Abs(position.X) > 1e7 || Math.Abs(position.Y) > 1e7) { throw new InvalidOperationException("Мир недоступен для создания персонажа"); }
        string name = template?.Name ?? "";
        if (reserved.Count >= 32) { throw new InvalidOperationException("Очередь создания заполнена"); }
        if (reserved.Contains(name)) { throw new ArgumentException("Имя уже находится в очереди создания"); }
        foreach (Character character in Character.CharacterList) {
            if (!character.Removed && string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Имя уже занято"); }
        }
        CharacterInfo info = CreateInfo(template);
        reserved.Add(name);
        try {
            bool queued = Kits.KitSpawn.Spawn(info, position, CharacterTeamType.Team1, character => {
                reserved.Remove(name);
                onSpawned?.Invoke(character);
            });
            if (!queued) { throw new InvalidOperationException("Не удалось поставить персонажа в очередь"); }
        } catch { reserved.Remove(name); info.Remove(); throw; }
    }
    public void Clear() => reserved.Clear();
}
