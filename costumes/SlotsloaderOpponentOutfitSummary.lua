-- The opponent outfit menu draws one summary label per
-- costume even when the menu supplies fewer label widgets than costumes.
local prefix = "[OpponentSummaryGuard] "
local ui = sdk.find_type_definition("app.UIFlowOtherMatchingFighterSetting.UIParam")
local redraw = ui and ui:get_method("UpdateCosColorTextList")
if not redraw then log.warn(prefix .. "Summary method unavailable"); return end
local last_counts = {}

sdk.hook(redraw, function(args)
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
    local ctx = thread.get_hook_storage()
    ctx.working, ctx.original = working, records
    -- Only the temporary opponent-menu copy, only during this synchronous draw.
    working.CostumeSettings = visible
end, function(result)
    local ctx = thread.get_hook_storage()
    if ctx.working then ctx.working.CostumeSettings = ctx.original end
    return result
end)
log.info(prefix .. "Ready")
