using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private void RevivePlayer(IReadMessage input, Session session) {
        Require(session.Client, InteractionAction.RevivePlayers);
        string account = input.ReadString(); ushort expectedBody = input.ReadUInt16(); bool campaignOverride = input.ReadBoolean();
        Vector2 position = new(input.ReadSingle(), input.ReadSingle());
        Client player = FindAccount(account);
        if (GameMain.GameSession?.IsRunning != true || Entity.Spawner == null || player == null || !player.InGame || player.NeedsMidRoundSync ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) || Hull.FindHull(position, useWorldCoordinates: true) == null) {
            throw new ArgumentException("Игрок недоступен или точка возрождения вне помещения");
        }
        if ((player.Character is { Removed: false } body ? body.ID : 0) != expectedBody) { throw new InvalidOperationException("Тело игрока изменилось. Обновите список игроков"); }
        if (player.Character is { Removed: false, IsDead: false }) { throw new InvalidOperationException("Игрок уже жив"); }
        bool restricted = GameMain.GameSession.GameMode is CampaignMode || GameMain.Server.ServerSettings.RespawnMode == RespawnMode.Permadeath;
        if (restricted) {
            if (!campaignOverride) { throw new InvalidOperationException("Для кампании или permadeath требуется явное административное исключение"); }
            Require(session.Client, InteractionAction.OverrideRespawnRules);
        }
        var campaignData = (GameMain.GameSession.GameMode as MultiPlayerCampaign)?.GetClientCharacterData(player);
        CharacterInfo info = campaignData?.CharacterInfo ?? player.CharacterInfo ?? player.Character?.Info;
        if (info == null || info.SpeciesName != CharacterPrefab.HumanSpeciesName) { throw new InvalidOperationException("Сохранённая личность человека недоступна"); }
        if (player.Character is { Removed: false } linked && linked.Info != info) { throw new InvalidOperationException("Связь тела с сохранённой личностью игрока не совпадает"); }
        foreach (Character other in Character.CharacterList) {
            if (!other.Removed && other != player.Character && other.Info == info) { throw new InvalidOperationException("Для этой личности уже существует персонаж"); }
        }
        Character character;
        if (player.Character is { Removed: false } existing) {
            character = existing;
            character.Revive(removeAfflictions: true, createNetworkEvent: true);
            character.TeleportTo(position);
        } else {
            Character previous = info.Character;
            character = null;
            try {
                // No asynchronous gap in which RespawnManager or another GM can create a second body.
                character = Character.Create(info.SpeciesName, position, ToolBox.RandomSeed(8), info,
                    isRemotePlayer: true, hasAi: false, createNetworkEvent: false, spawnInitialItems: false);
                if (character == null) { throw new InvalidOperationException("Движок не создал персонажа"); }
                CharacterTeamType team = player.TeamID != CharacterTeamType.None ? player.TeamID : info.TeamID;
                if (team is not (CharacterTeamType.Team1 or CharacterTeamType.Team2)) { throw new InvalidOperationException("Команда игрока не определена"); }
                character.SetOriginalTeamAndChangeTeam(team, processImmediately: true);
                character.LoadTalents();
                campaignData?.ApplyWalletData(character);
                GameMain.GameSession.CrewManager.AddCharacter(character);
            } catch {
                if (character is { Removed: false }) { character.Remove(); }
                info.Character = previous; throw;
            }
            Entity.Spawner.CreateNetworkEvent(new EntitySpawner.SpawnEntity(character));
        }
        info.PermanentlyDead = false; info.CauseOfDeath = null;
        GameMain.Server.SetClientCharacter(player, character);
        GameMain.GameSession.ReviveCharacter(character);
        if (GameMain.GameSession.GameMode is MultiPlayerCampaign campaign) {
            // Preserve the campaign's player association; do not copy old inventory or wallet into a second body.
            campaign.SetClientCharacterData(player).HasSpawned = true;
        }
        rosterDirty = true;
        ModLog.Message("GM revive " + session.Account + " player=" + account + " body=" + character.ID + " override=" + restricted);
    }
}
