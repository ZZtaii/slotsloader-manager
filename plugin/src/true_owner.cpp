// SF6 Slots -- what the ACCOUNT really owns, game outfits and colours (true_owner.hpp)
#include "true_owner.hpp"
#include "slots.hpp"

#include <windows.h>

#include <cstdlib>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

namespace slots::trueown {
namespace {

constexpr uint32_t kLoaderRecords = 5000;   // the loader's costume records start here (costumes.cpp kStaticMin): never read

// ---- written by refresh only (game thread), read by outfit() / colour() (any thread) under g_mx. The tables are written before
// g_state / g_colours_ok say they are there (set under g_mx), the readers touch none of them before: the game calls of the reads
// are made without the lock (a hook of ours the game would run inside one of them could not wait on it). The snapshot of the save
// and Steam is swapped under g_mx. ----
std::mutex g_mx;
State g_state = State::Waiting;
// the game's tables, read once
std::map<int, uint32_t> g_dlc_app;                                   // DLC id -> its Steam app id (0: none, not on Steam)
std::map<uint32_t, std::vector<int>> g_cos_dlcs;                     // costume record -> the DLCs that give it
std::map<uint32_t, std::vector<int>> g_col_dlcs;                     // colour record -> the DLCs that give it
std::map<std::pair<int, int>, std::vector<uint32_t>> g_cos_rids;     // (fighter, costume) -> its records
std::map<std::tuple<int, int, int>, std::vector<uint32_t>> g_col_rids;   // (fighter, costume, colour) -> its records
std::set<uint32_t> g_cos_default, g_col_default;
bool g_colours_ok = false;                                           // the colour records and DlcItemColor read
// what the save and Steam say now (refresh)
std::set<uint32_t> g_cos_saved, g_col_saved;
bool g_steam = false;
std::map<uint32_t, bool> g_app_owned;                                // the DLCs' Steam apps -> the account owns it

// ---- game access (game thread only) ----
Obj* inventory(const char* field) {
    auto* im = api().get_managed_singleton("app.InventoryManager");
    return im ? get_obj(im, field) : nullptr;
}
Obj* array_item(Obj* arr, int i) {
    Obj* x = call_obj(arr, "Get", { arg_i32(i) });
    return x ? x : call_obj(arr, "get_Item", { arg_i32(i) });
}
int array_length(Obj* arr) {
    if (!arr) return 0;
    Ret r = call(arr, "get_Length");
    return r.exception_thrown ? 0 : static_cast<int32_t>(r.dword);
}
uint32_t u32_of(Obj* o, const char* name) {
    uint32_t v = 0;
    get_u32(o, name, v);
    return v;
}
// A DlcManager table's DataArray: (record field, Dlc) of each record
void read_items(Obj* dm, const char* table, const char* field, std::map<uint32_t, std::vector<int>>& out) {
    Obj* t = get_obj(dm, table);
    Obj* arr = t ? get_obj(t, "DataArray") : nullptr;
    for (int i = 0, n = array_length(arr); i < n; ++i)
        if (Obj* r = array_item(arr, i)) out[u32_of(r, field)].push_back(static_cast<int32_t>(u32_of(r, "Dlc")));
}

// Every colour record of the costume records read (TableDataManager.GetFighterCostumeColors(record, false): owned or not, as
// costumes.cpp reads Random's colours). False when none was found (the table not ready yet: asked again at the next refresh).
bool read_colours(const std::map<uint32_t, std::pair<int, int>>& fc_of, std::map<std::tuple<int, int, int>, std::vector<uint32_t>>& rids,
                  std::set<uint32_t>& defaults) {
    auto* tm = api().get_managed_singleton("app.TableDataManager");
    Method* m_cols = tm ? method_of(tm->get_type_definition(), "GetFighterCostumeColors", 2) : nullptr;
    if (!m_cols) return false;
    for (auto& [rid, fc] : fc_of) {
        Obj* list = call_obj(m_cols, tm, { arg_i32(static_cast<int32_t>(rid)), arg_bool(false) });
        for (int j = 0, k = list_count(list); j < k; ++j) {
            Obj* cr = list_item(list, j);
            int32_t no = -1;
            if (!cr || !get_i32(cr, "colorNo", no) || no < 0) continue;
            uint32_t id = u32_of(cr, "id");
            if (!id) id = u32_of(cr, "ManageId");
            if (!id) continue;
            rids[{ fc.first, fc.second, no }].push_back(id);
            bool def = false;
            if (get_bool(cr, "isDefault", def) && def) defaults.insert(id);
        }
    }
    return !rids.empty();
}

// The game's tables, once: the state they leave (Waiting: DlcManager not initialized yet, nothing read)
bool g_told_colours = false;
std::map<uint32_t, std::pair<int, int>> g_fc_of;   // costume record -> (fighter, costume), for the colours' read
State read_tables() {
    auto* dm = api().get_managed_singleton("app.DlcManager");
    Obj* ci = inventory("_Costume");
    bool init = false;
    if (!dm || !ci || !get_bool(dm, "IsInitialized", init) || !init) return State::Waiting;   // asked again later
    Obj* main = get_obj(dm, "DlcDataMain");
    Obj* main_arr = main ? get_obj(main, "DataArray") : nullptr;
    for (int i = 0, n = array_length(main_arr); i < n; ++i) {
        Obj* r = array_item(main_arr, i);
        if (!r) continue;
        g_dlc_app[static_cast<int32_t>(u32_of(r, "id"))] = static_cast<uint32_t>(strtoul(str(get_obj(r, "SteamAppId")).c_str(), nullptr, 10));
    }
    read_items(dm, "DlcItemCostume", "Costume", g_cos_dlcs);
    read_items(dm, "DlcItemColor", "Color", g_col_dlcs);
    Method* m_rec = method_of(ci->get_type_definition(), "GetMasterDataRecord", 1);
    for (uint32_t rid = 1, last = 0; m_rec && rid - last <= 300 && rid < kLoaderRecords; ++rid) {   // read_carriers' way
        Obj* rec = call_obj(m_rec, ci, { arg_i32(static_cast<int32_t>(rid)) });
        int32_t fid = 0, cn = -1;
        if (!rec || !get_i32(rec, "fighterId", fid) || !get_i32(rec, "costumeNo", cn) || cn < 0) continue;
        last = rid;
        g_cos_rids[{ fid, cn }].push_back(rid);
        g_fc_of[rid] = { fid, cn };
        bool def = false;
        if (get_bool(rec, "isDefault", def) && def) g_cos_default.insert(rid);
    }
    if (g_dlc_app.empty() || g_cos_dlcs.empty() || g_cos_rids.empty()) {
        logf("true owner: the game's DLC / costume tables not read (DLCs %zu, DLC costumes %zu, costume records %zu): which outfits and "
             "colours the account really owns is not known, the network gets Outfit 1 in Color 1 for anything else",
             g_dlc_app.size(), g_cos_dlcs.size(), g_cos_rids.size());
        return State::Failed;
    }
    logf("true owner: %zu DLCs, %zu costume records given by DLCs, %zu colour records given by DLCs, %zu costume records (%zu default)",
         g_dlc_app.size(), g_cos_dlcs.size(), g_col_dlcs.size(), g_fc_of.size(), g_cos_default.size());
    return State::Ready;
}

// ---- Steam (the game's own steam_api64.dll, its flat API) ----
using AppsFn = void*(__cdecl*)();
using AskFn = bool(__cdecl*)(void*, uint32_t);
AppsFn g_apps = nullptr;
AskFn g_subscribed = nullptr, g_dlc_installed = nullptr;
bool g_steam_tried = false;
void* steam_apps() {
    if (!g_steam_tried) {
        g_steam_tried = true;
        if (HMODULE h = GetModuleHandleA("steam_api64.dll")) {
            g_apps = reinterpret_cast<AppsFn>(GetProcAddress(h, "SteamAPI_SteamApps_v008"));
            g_subscribed = reinterpret_cast<AskFn>(GetProcAddress(h, "SteamAPI_ISteamApps_BIsSubscribedApp"));
            g_dlc_installed = reinterpret_cast<AskFn>(GetProcAddress(h, "SteamAPI_ISteamApps_BIsDlcInstalled"));
        }
        if (!g_apps || !g_subscribed)
            logf("true owner: no Steam (steam_api64.dll's SteamApps): an outfit or a colour only a DLC gives is not known to be owned, "
                 "the network gets Outfit 1 in Color 1 for it");
    }
    return g_apps && g_subscribed ? g_apps() : nullptr;
}

std::string g_last_summary;

}  // namespace

bool refresh(const char* why) {
    bool fresh = false;                                  // the tables (or the colours) read by this call
    if (g_state == State::Failed) return false;          // (g_state, g_colours_ok: written on this thread only)
    if (g_state == State::Waiting) {
        const State s = read_tables();
        static int waited = 0;
        if (s == State::Waiting && ++waited == 20)       // (boot passes and screens: told once, the answers stay "not known")
            logf("true owner: DlcManager not initialized after %d tries (%s): which outfits and colours the account owns is not "
                 "known yet, the network gets Outfit 1 in Color 1 for anything else", waited, why);
        if (s == State::Failed) { std::lock_guard lk(g_mx); g_state = s; return true; }   // every answer stays Unknown (told)
        if (s != State::Ready) return false;
        fresh = true;
    }
    bool colours_now = false;
    if (!g_colours_ok) {
        std::map<std::tuple<int, int, int>, std::vector<uint32_t>> rids;
        std::set<uint32_t> defaults;
        if (!g_col_dlcs.empty() && read_colours(g_fc_of, rids, defaults)) {
            g_col_rids = std::move(rids);                // (not read by anyone before g_colours_ok)
            g_col_default = std::move(defaults);
            colours_now = fresh = true;
            logf("true owner: %zu colours of the game's outfits read (%zu default)", g_col_rids.size(), g_col_default.size());
        } else if (!g_told_colours) {
            g_told_colours = true;
            logf("true owner: the colour records %s (%s): a colour from Color 3 on goes on the network as Color 1 until they are",
                 g_col_dlcs.empty() ? "not read (DlcManager.DlcItemColor empty)" : "not read yet (TableDataManager.GetFighterCostumeColors)", why);
        }
    }
    // What the save and Steam say now (game calls, outside the lock)
    std::set<uint32_t> cos_saved, col_saved;
    if (Obj* ci = inventory("_Costume")) {
        static Method* m_save = nullptr;
        if (!m_save) m_save = method_of(ci->get_type_definition(), "ExistsSaveData", 1);
        if (m_save)
            for (auto& [rid, fc] : g_fc_of) {
                if (g_cos_default.count(rid)) continue;
                Ret r = call(m_save, ci, { arg_i32(static_cast<int32_t>(rid)) });
                if (!r.exception_thrown && (r.byte & 1)) cos_saved.insert(rid);
            }
    }
    if (Obj* co = g_colours_ok || colours_now ? inventory("_Color") : nullptr) {
        static Method* m_list = nullptr;
        static Method* m_has = nullptr;
        if (!m_list) m_list = method_of(co->get_type_definition(), "get_SaveDataList", 0);
        if (!m_has) m_has = method_of(co->get_type_definition(), "ExistsSaveData", 1);
        Obj* list = m_list ? call_obj(m_list, co) : nullptr;
        if (list) {
            for (int i = 0, n = list_count(list); i < n; ++i)
                if (auto el = list_elem(list, i); el.ok && el.vt) col_saved.insert(el.ret.dword);
        } else if (m_has) {
            for (auto& [key, ids] : g_col_rids)
                for (uint32_t id : ids) {
                    if (g_col_default.count(id)) continue;
                    Ret r = call(m_has, co, { arg_i32(static_cast<int32_t>(id)) });
                    if (!r.exception_thrown && (r.byte & 1)) col_saved.insert(id);
                }
        }
    }
    std::map<uint32_t, bool> apps;
    void* sa = steam_apps();
    if (sa) {
        std::set<uint32_t> asked;
        for (auto* items : { &g_cos_dlcs, &g_col_dlcs })
            for (auto& [rid, dlcs] : *items)
                for (int id : dlcs)
                    if (auto a = g_dlc_app.find(id); a != g_dlc_app.end() && a->second) asked.insert(a->second);
        for (uint32_t app : asked) apps[app] = g_subscribed(sa, app) || (g_dlc_installed && g_dlc_installed(sa, app));
    }
    size_t owned_apps = 0;
    std::string owned_list;
    for (auto& [app, own] : apps)
        if (own) {
            ++owned_apps;
            if (owned_apps <= 40) owned_list += (owned_list.empty() ? "" : " ") + std::to_string(app);
        }
    char b[256];
    snprintf(b, sizeof b, "%zu outfit records and %zu colour records bought (in the save), Steam %s: %zu of %zu DLC apps owned",
             cos_saved.size(), col_saved.size(), sa ? "available" : "NOT available", owned_apps, apps.size());
    std::lock_guard lk(g_mx);
    g_state = State::Ready;
    if (colours_now) g_colours_ok = true;
    const bool changed = fresh || cos_saved != g_cos_saved || col_saved != g_col_saved || apps != g_app_owned || g_steam != (sa != nullptr);
    g_cos_saved = std::move(cos_saved);
    g_col_saved = std::move(col_saved);
    g_app_owned = std::move(apps);
    g_steam = sa != nullptr;
    if (b != g_last_summary) {
        g_last_summary = b;
        logf("true owner (%s): %s%s%s", why, b, owned_list.empty() ? "" : "; owned: ", owned_list.c_str());
    }
    return changed;
}

State state() {
    std::lock_guard lk(g_mx);
    return g_state;
}

namespace {
// The rule of the top, for the records of one outfit or one colour (under g_mx)
Own own_of(const std::vector<uint32_t>& rids, const std::set<uint32_t>& defaults, const std::set<uint32_t>& saved,
           const std::map<uint32_t, std::vector<int>>& dlcs) {
    bool dlc = false, ask_steam = false;
    for (uint32_t rid : rids) {
        if (defaults.count(rid) || saved.count(rid)) return Own::Yes;
        auto d = dlcs.find(rid);
        if (d == dlcs.end()) continue;
        dlc = true;
        for (int id : d->second) {
            auto a = g_dlc_app.find(id);
            if (a == g_dlc_app.end() || !a->second) continue;          // not on Steam (a PlayStation pack): never owned here
            if (!g_steam) { ask_steam = true; continue; }
            if (auto o = g_app_owned.find(a->second); o != g_app_owned.end() && o->second) return Own::Yes;
        }
    }
    if (!dlc) return Own::Yes;                                         // no DLC gives it: the game's list counts it on other grounds
    return ask_steam ? Own::Unknown : Own::No;
}
}  // namespace

Own outfit(int fid, int cos) {
    if (cos == carrier::kOutfit1) return Own::Yes;                     // every fighter's default
    if (cos < 0) return Own::Unknown;
    std::lock_guard lk(g_mx);
    if (g_state != State::Ready) return Own::Unknown;
    auto it = g_cos_rids.find({ fid, cos });
    if (it == g_cos_rids.end()) return Own::Unknown;
    return own_of(it->second, g_cos_default, g_cos_saved, g_cos_dlcs);
}

Own colour(int fid, int cos, int colour) {
    if (carrier::safe_colour(colour)) return Own::Yes;                 // Colors 1 and 2: no record, every player's
    if (colour < 0 || cos < 0) return Own::Unknown;
    std::lock_guard lk(g_mx);
    if (g_state != State::Ready || !g_colours_ok) return Own::Unknown;
    auto it = g_col_rids.find({ fid, cos, colour });
    if (it == g_col_rids.end()) return Own::Unknown;
    return own_of(it->second, g_col_default, g_col_saved, g_col_dlcs);
}

}  // namespace slots::trueown
