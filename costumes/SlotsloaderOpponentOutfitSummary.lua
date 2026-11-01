-- The opponent outfit menu draws one summary label per
-- costume even when the menu supplies fewer label widgets than costumes.
local prefix = "[OpponentSummaryGuard] "
local ui = sdk.find_type_definition("app.UIFlowOtherMatchingFighterSetting.UIParam")
local redraw = ui and ui:get_method("UpdateCosColorTextList")
if not redraw then log.warn(prefix .. "Summary method unavailable"); return end
local last_counts = {}
-- The working copy shortened for the current draw. Kept here rather than in hook storage, so a
-- draw that never reaches its post hook (the game method threw) is still put back: at the next
-- draw, or at the start of the next frame on the game thread at the latest.
local pending = nil

local function restore()
    if pending == nil then return end
    local shortened = pending
    pending = nil
    local ok, err = pcall(function() shortened.working.CostumeSettings = shortened.original end)
    if not ok then log.warn(prefix .. "Could not restore the full costume list: " .. tostring(err)) end
end

sdk.hook(redraw, function(args)
    restore()
    local row = sdk.to_managed_object(args[2])
    local working = row:get_field("<SettingDataDuplicated>k__BackingField")
    local summary = row:get_field("mCosColorTextList")
    if not working or not summary then return end
    local records = working:get_field("CostumeSettings")
    local labels = summary:call("get_Items")
    if not records or not labels then return end
    local count = records:call("get_Count")
    local capacity = labels:get_size()
    local fighter = row:get_field("SettingFighterId")
    local counts = tostring(count) .. " outfits / " .. tostring(capacity) .. " summary rows"
    if last_counts[fighter] ~= counts then
        log.info(prefix .. tostring(fighter) .. " " .. counts)
        last_counts[fighter] = counts
    end
    if count <= capacity then return end
    local visible = records:call("GetRange", 0, capacity)
    if not visible then return end
    -- Only the temporary opponent-menu copy, only during this synchronous draw.
    pending = { working = working, original = records }
    working.CostumeSettings = visible
end, function(result)
    restore()
    return result
end)

re.on_pre_application_entry("UpdateBehavior", restore)
log.info(prefix .. "Ready")
