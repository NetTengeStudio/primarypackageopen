using System;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

internal static class EntityActions {
    public static void Execute(ushort id, EntityOperation operation, string value, Vector2 position) {
        if (GameMain.NetworkMember is { IsClient: true }) { throw new InvalidOperationException("Действие принадлежит серверу"); }
        if (GameMain.GameSession?.IsRunning != true || Entity.Spawner == null) { throw new InvalidOperationException("Раунд не запущен"); }
        if (operation is EntityOperation.SpawnCreature or EntityOperation.SpawnDormantCreature) {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || Math.Abs(position.X) > 1e7 || Math.Abs(position.Y) > 1e7 ||
                !CharacterPrefab.Prefabs.TryGet(value.ToIdentifier(), out var prefab) || prefab.Identifier == CharacterPrefab.HumanSpeciesName) { throw new ArgumentException("Существо или точка появления недопустимы"); }
            Entity.Spawner.AddCharacterToSpawnQueue(prefab.Identifier, position, onSpawn: character => {
                if (character.AIController != null) { character.AIController.Enabled = operation == EntityOperation.SpawnCreature; }
            });
            return;
        }
        Entity entity = Entity.FindEntityByID(id);
        if (entity == null || entity.Removed) { throw new ArgumentException("Объект больше не существует"); }
        switch (operation) {
            case EntityOperation.AIEnabled when entity is Character { AIController: not null } actor:
                if (value != "on" && value != "off") { throw new ArgumentException("Недопустимый режим ИИ"); }
                actor.AIController.Enabled = value == "on";
                break;
            case EntityOperation.Condition when entity is Item item:
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float condition) || !float.IsFinite(condition) || condition < 0 || condition > item.MaxCondition) { throw new ArgumentException("Состояние должно находиться в пределах прочности предмета"); }
                if (item.Indestructible || condition < item.Condition && (item.InvulnerableToDamage || item.GetComponent<Door>() is { IsStuck: true })) { throw new ArgumentException("Изменение прочности блокируется защитой или сваркой"); }
                item.Condition = condition;
                if (item.Condition != condition) { throw new InvalidOperationException("Игра отклонила изменение прочности: проверьте защиту и сварку"); }
                break;
            case EntityOperation.RemoveItem when entity is Item removed:
                Entity.Spawner.AddItemToRemoveQueue(removed);
                break;
            case EntityOperation.Gib when entity is Character character:
                if (character.GodMode || character.CharacterHealth.Unkillable) { throw new ArgumentException("Персонаж защищён от смерти"); }
                bool severable = false;
                foreach (var joint in character.AnimController.LimbJoints) { severable |= joint.CanBeSevered && !joint.IsSevered; }
                if (!severable) { throw new ArgumentException("У персонажа нет разрываемых соединений"); }
                if (!character.IsDead) { character.Kill(CauseOfDeathType.Unknown, null); }
                if (!character.IsDead) { throw new InvalidOperationException("Игра отклонила смерть персонажа"); }
                foreach (var joint in character.AnimController.LimbJoints) { character.AnimController.SeverLimbJoint(joint); }
                break;
            case EntityOperation.BreakDoor when entity is Item doorItem && doorItem.GetComponent<Door>() is { } door:
                if (doorItem.Indestructible || doorItem.InvulnerableToDamage) { throw new ArgumentException("Дверь защищена от разрушения"); }
                door.Stuck = 0;
                doorItem.Condition = 0;
                if (doorItem.Condition != 0) { throw new InvalidOperationException("Игра отклонила разрушение двери"); }
                door.UpdateBroken(0, null);
                break;
            default: throw new ArgumentException("Действие неприменимо к выбранному объекту");
        }
    }
}
