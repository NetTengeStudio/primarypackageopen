local MENU_PROXY = "actor-menu-proxy"

local menuItems = {
    ["actor-menu-proxy"] = true,
    ["cursor-anim"] = true,  ["cursor-anim1"] = true,
    ["push-anim"]   = true,  ["push-anim1"]   = true,
    ["point-anim"]  = true,  ["point-anim1"]  = true,
    ["hands-up-anim"]  = true,
    ["handcuffs-anim"] = true,
}

local idCards = { idcard = true, idcardsecure = true, idcardcommand = true }
local idCardSlot = InvSlotType.Card

local keyCooldown = 0.35
local lastClientToggle = 0
local lastServerToggles = {}
local toggleKeyConfig = nil

Hook.Add("AA.hands", "primary.AAHandsCompatibility", function() return end)

local function isMenuToggleHit()
    if toggleKeyConfig then
        local ok, hit = pcall(function() return toggleKeyConfig.IsHit() end)
        if ok then return hit end
    end
    return PlayerInput.KeyHit(Keys.N)
end

local function getIdentifier(item)
    if not item or not item.Prefab or not item.Prefab.Identifier then return nil end
    return item.Prefab.Identifier.Value
end

local function findIdCard(inventory)
    local card = inventory.GetItemInLimbSlot(idCardSlot)
    if card and idCards[getIdentifier(card)] and card.OwnInventory then return card end
    for i = 0, inventory.Capacity - 1 do
        local item = inventory.GetItemAt(i)
        if item and idCards[getIdentifier(item)] and item.OwnInventory then return item end
    end
end

local function collectMenuItems(inventory)
    local found = {}
    local function scan(inv)
        for i = 0, inv.Capacity - 1 do
            local item = inv.GetItemAt(i)
            if item and menuItems[getIdentifier(item)] then found[#found + 1] = item end
        end
    end
    scan(inventory)
    for i = 0, inventory.Capacity - 1 do
        local item = inventory.GetItemAt(i)
        if item and idCards[getIdentifier(item)] and item.OwnInventory then scan(item.OwnInventory) end
    end
    return found
end

local function spawnMenu(inventory)

    local prefab = ItemPrefab.GetItemPrefab(MENU_PROXY)
    if not prefab then return end
    local card = findIdCard(inventory)
    Entity.Spawner.AddItemToSpawnQueue(prefab, card and card.OwnInventory or inventory)
end

local function dropOrElse(character, elseFn)
    local items = collectMenuItems(character.Inventory)
    if #items > 0 then
        for _, item in ipairs(items) do item.Drop(character) end
    else
        elseFn()
    end
end

local function toggleMenu(character)
    if not character or character.Removed or character.IsDead or not character.Inventory then return end
    dropOrElse(character, function() spawnMenu(character.Inventory) end)
end

if CLIENT then
    pcall(function()
        local _, package = trygetpackage("Primary package")
        if not package then return end
        local found, config = ConfigService.TryGetConfig(package, "ToggleKey")
        if found and config then toggleKeyConfig = config end

        local function getKeyCode(cfg)
            if not cfg then return 0 end
            local ok, val = pcall(function() return cfg.Value end)
            if not ok or not val then return 0 end
            local okK, k = pcall(function() return val.Key end)
            if not okK or not k then return 0 end
            local okVal, raw = pcall(function() return k.value__ end)
            if okVal and type(raw) == "number" and raw > 0 then return raw end
            local okNum, num = pcall(function() return tonumber(k) end)
            if okNum and type(num) == "number" and num > 0 then return num end
            return 0
        end

        local _, zIn    = ConfigService.TryGetConfig(package, "ZoomInKey")
        local _, zOut   = ConfigService.TryGetConfig(package, "ZoomOutKey")
        local _, zReset = ConfigService.TryGetConfig(package, "ZoomResetKey")
        local _, zFocus = ConfigService.TryGetConfig(package, "FocusDistance")

        pcall(function()
            local zkc = LuaUserData.RegisterType("PrimaryPackage.ZoomKey.ZoomKeyConfig")
            if not zkc then return end
            zkc.ZoomInKeyCode    = getKeyCode(zIn)
            zkc.ZoomOutKeyCode   = getKeyCode(zOut)
            zkc.ZoomResetKeyCode = getKeyCode(zReset)
            if zFocus then
                local ok, v = pcall(function() return zFocus.Value end)
                if ok and type(v) == "number" and v > 0 then zkc.FocusDistance = v end
            end
        end)
    end)

    Hook.Add("keyUpdate", "primary.InteractionMenuToggle", function()
        local now = Timer.GetTime()
        if now - lastClientToggle < keyCooldown then return end
        if not isMenuToggleHit() then return end
        if GUI.KeyboardDispatcher.Subscriber then return end
        local character = Character.Controlled
        if not character or not character.Inventory then return end

        if Game.IsMultiplayer then
            dropOrElse(character, function()
                local msg = Networking.Start("PrimaryInteractionMenu")
                if msg then Networking.Send(msg) end
            end)
        else
            toggleMenu(character)
        end

        lastClientToggle = now
    end)
end

if SERVER then
    Hook.Add("roundStart", "primary.InteractionMenuToggle.reset", function() lastServerToggles = {} end)

    Networking.Receive("PrimaryInteractionMenu", function(_, client)
        if not client or not client.Character then return end
        local character = client.Character
        local now = Timer.GetTime()
        local key = tostring(client.SessionId or client.Name or client)
        if lastServerToggles[key] and now - lastServerToggles[key] < keyCooldown then return end
        lastServerToggles[key] = now
        toggleMenu(character)
    end)
end

