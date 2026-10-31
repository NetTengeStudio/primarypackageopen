if CLIENT and Game.IsMultiplayer then return end

NetConfig.MaxHealthUpdateInterval, NetConfig.HullUpdateDistance, NetConfig.MaxHealthUpdateIntervalDead = 2.0, 10000.0, 15.0

local delays = {smalldelay = 0.2, mediumdelay = 0.5, bigdelay = 1, hugedelay = 2}
local doorSounds = {enterable_door1 = "sfx_doorsound1", enterable_door2 = "sfx_doorsound2"}
local teleportDelays = {}

Hook.Add("roundStart", "ZDoors.ClearDelays", function() teleportDelays = {} end)

local function hasPerm(c) return c == nil or c.HasPermission(ClientPermissions.ConsoleCommands) end
local function countAndRemove(list, filter, removeFn)
    local n = 0
    for _, e in pairs(list) do if e and filter(e) then removeFn(e); n = n + 1 end end
    return n
end

Game.AddCommand("clearitems", "", function(_, c)
    if not hasPerm(c) then return end
    print("Удалено предметов: " .. countAndRemove(Item.ItemList, function(it)
        return it.body and it.body.Enabled and not it.ParentInventory and not it.HasTag("deletenone")
    end, function(it) Entity.Spawner.AddItemToRemoveQueue(it) end))
end)

Game.AddCommand("clearnpc", "", function(_, c)
    if not hasPerm(c) then return end
    print("Удалено персонажей: " .. countAndRemove(Character.CharacterList, function(ch)
        return not ch.IsPlayer and not ch.Removed
    end, function(ch) Entity.Spawner.AddEntityToRemoveQueue(ch) end))
end)

local function getDelay(item)
    for tag, delay in pairs(delays) do if item.HasTag(tag) then return delay end end
    return 0
end

local function handleDoorSignal(signal, connection)
    if not signal.sender or not connection.Item then return end
    local t, d = Timer.GetTime(), getDelay(connection.Item)
    if teleportDelays[signal.sender] and t - teleportDelays[signal.sender] < d then return end
    local pos = connection.Item.WorldPosition
    signal.sender.TeleportTo(pos)
    if signal.sender.SelectedCharacter then signal.sender.SelectedCharacter.TeleportTo(pos) end
    local s = doorSounds[connection.Item.Prefab.Identifier.Value]
    if s then Entity.Spawner.AddItemToSpawnQueue(ItemPrefab.GetItemPrefab(s), pos) end
    teleportDelays[signal.sender] = t
end

for _, door in ipairs{"enterable_door1", "enterable_door2", "enterable_door3"} do
    Hook.Add("signalReceived." .. door, "ZDoors", handleDoorSignal)
end

