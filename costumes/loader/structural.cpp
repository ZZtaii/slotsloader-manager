#include "structural.hpp"
#include "pak.hpp"
#include "patch.hpp"
#include "structural_scene.hpp"
#include "structural_msg.hpp"

#include <cstdio>
#include <cstdlib>
#include <map>
#include <set>
#include <string_view>

namespace {

constexpr const char* ESF_ROOT_SCENE = "natives/stm/product/charparam/esf/esf.scn.20";
constexpr const char* COSTUME_TABLE  = "natives/stm/product/cfncontents/onlineshop/fightercostumeuserdata.user.2";
constexpr const char* COSTUME_NAMES  = "natives/stm/product/message/fgm/fighter/fightercostumemessage.msg.21";
constexpr uint32_t SLOT_RECORD_ID_BASE = 5000;   // static_meta.json record_id_base
constexpr uint32_t SLOT_SORT_NO_BASE = 101;      // static_meta.json sort_no_base
constexpr int SLOTS_PER_FIGHTER = 100;           // costume_slot_index() blocks

bool load(PakReader& pak, const std::string& path, std::vector<uint8_t>& out) {
    const PakEntry* e = pak.find(pak_path_hash(std::string_view(path)));
    if (!e) return false;
    out = pak.read(*e);
    return !out.empty();
}

std::string roman(int n) {
    static const int vals[] = {1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1};
    static const char* syms[] = {"M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"};
    std::string s;
    for (int i = 0; i < 13; i++)
        while (n >= vals[i]) { s += syms[i]; n -= vals[i]; }
    return s;
}

std::string dir_of(int fighter) {
    char b[16];
    snprintf(b, sizeof b, "esf%03d", fighter);
    return b;
}

}  // namespace

bool game_costume_fighters(PakReader& pak, std::vector<int>& out) {
    out.clear();
    std::vector<uint8_t> table;
    std::vector<std::pair<int, int>> records;
    if (!load(pak, COSTUME_TABLE, table) || !costume_table_records(table, records)) return false;
    std::set<int> fighters;
    for (auto& r : records) fighters.insert(r.first);
    out.assign(fighters.begin(), fighters.end());
    return true;
}

static const char VER_PREFIX[] = "natives/stm/product/flow/version_update/ver_";
static const char VER_SUFFIX[] = "/esf.scn.20";

std::vector<std::string> slot_version_scene_paths() {
    std::vector<std::string> out;
    for (int major = 2; major <= 9; major++)
        for (int minor = (major == 2 ? 300 : 0); minor <= 9999; minor++) {
            char p[96];
            snprintf(p, sizeof p, "%s%02d_%04d%s", VER_PREFIX, major, minor, VER_SUFFIX);
            out.push_back(p);
        }
    return out;
}

bool is_slot_version_scene(const std::string& p) {
    const size_t n = sizeof(VER_PREFIX) - 1, m = sizeof(VER_SUFFIX) - 1;
    if (p.size() != n + 7 + m || p.compare(0, n, VER_PREFIX) != 0 || p.compare(n + 7, m, VER_SUFFIX) != 0
        || p[n + 2] != '_') return false;
    for (size_t i : {0, 1, 3, 4, 5, 6}) if (p[n + i] < '0' || p[n + i] > '9') return false;
    const int major = atoi(p.substr(n, 2).c_str()), minor = atoi(p.substr(n + 3, 4).c_str());
    return major >= 2 && major <= 9 && (major > 2 || minor >= 300);
}

GameStructural build_game_structural(PakReader& pak, int slot_min, int slot_max, uint32_t msg_id_base) {
    GameStructural st;
    auto fail = [&](const std::string& why) { st = GameStructural{}; st.why = why; return st; };
    if (slot_max - slot_min + 1 != SLOTS_PER_FIGHTER) return fail("slot range is not 100 slots");

    // Fighters: those of the game's costume table
    std::vector<uint8_t> table;
    std::vector<std::pair<int, int>> game_records;
    if (!load(pak, COSTUME_TABLE, table)) return fail("costume table not found in the game's paks");
    if (!costume_table_records(table, game_records)) return fail("costume table layout not recognised");
    std::set<int> fighters;
    for (auto& r : game_records) fighters.insert(r.first);
    auto is_fighter = [&](const std::string& dir) {
        return dir.size() == 6 && fighters.count(atoi(dir.c_str() + 3)) > 0;
    };

    // Root scene: the fighters that have their Outfit 1 folder there, each slot a copy of it
    std::vector<uint8_t> root;
    std::map<std::string, std::set<int>> folders;
    std::string err;
    if (!load(pak, ESF_ROOT_SCENE, root)) return fail("root fighter scene not found in the game's paks");
    if (!read_scene_costume_folders(root, folders, &err)) return fail(std::string("root fighter scene: ") + err);
    std::set<std::string> seen, in_scene;
    std::vector<std::string> dirs;
    for (auto& [dir, costumes] : folders) {
        if (!costumes.count(0)) continue;
        seen.insert(dir);
        if (is_fighter(dir)) dirs.push_back(dir);
    }
    if (!add_scene_folders(root, dirs, SlotTemplate::V00, slot_min, slot_max + 1, &err))
        return fail(std::string("root fighter scene: ") + err);
    in_scene.insert(dirs.begin(), dirs.end());
    st.files.push_back({ ESF_ROOT_SCENE, std::move(root) });

    // Version scenes: a fighter goes in the first one that has its folders (a later one can add an outfit
    // to an older fighter: ver_02_0500 has C. Viper's Outfit 4, her slots stay in ver_02_0300)
    for (const std::string& p : slot_version_scene_paths()) {
        if (!pak.find(pak_path_hash(std::string_view(p)))) continue;
        std::vector<uint8_t> scn;
        if (!load(pak, p, scn)) return fail(p + ": cannot be read");
        if (!read_scene_costume_folders(scn, folders, &err)) return fail(p + ": " + err);
        dirs.clear();
        for (auto& [dir, costumes] : folders) {
            if (seen.count(dir)) continue;
            seen.insert(dir);
            if (is_fighter(dir)) dirs.push_back(dir);   // others (esf103) have no costume record
        }
        if (dirs.empty()) continue;
        if (!add_scene_folders(scn, dirs, SlotTemplate::V04_OR_LAST, slot_min, slot_max + 1, &err))
            return fail(p + ": " + err);
        in_scene.insert(dirs.begin(), dirs.end());
        st.files.push_back({ p, std::move(scn) });
    }

    // Costume table: 100 records per fighter, ids and sort numbers by fighter block (costume_slot_index),
    // the 100 shared name messages before them
    std::vector<std::vector<uint8_t>> guids(SLOTS_PER_FIGHTER);
    std::vector<MsgEntry> names;
    for (int so = 0; so < SLOTS_PER_FIGHTER; so++) {
        char g[64];
        snprintf(g, sizeof g, "a1b2c3d4-%04x-4000-8000-%012x", so + 1, (unsigned)(msg_id_base + so));
        if (!guid_bytes(g, guids[so])) return fail("bad message GUID");
        names.push_back({ g, "FighterCostumeMessage" + std::to_string(6 + so), "Outfit " + roman(so + 1) });
    }
    std::vector<CostumeSlotRecord> add;
    for (int f : fighters) {
        if (!in_scene.count(dir_of(f))) {
            st.notes.push_back("fighter " + std::to_string(f) + " is in the costume table but in no fighter scene: no slots");
            continue;
        }
        const int idx = costume_slot_index(f);
        for (int so = 0; so < SLOTS_PER_FIGHTER; so++) {
            const uint32_t id = SLOT_RECORD_ID_BASE + (uint32_t)(idx * SLOTS_PER_FIGHTER + so);
            add.push_back({ id, (uint32_t)f, (uint32_t)(slot_min + so),
                            SLOT_SORT_NO_BASE + (uint32_t)(idx * SLOTS_PER_FIGHTER + so), (uint32_t)so });
            st.records.push_back({ f, slot_min + so, (int)id });
        }
    }
    if (!add_costume_records(table, add, msg_id_base, guids, &err)) return fail("costume table: " + err);
    st.files.push_back({ COSTUME_TABLE, std::move(table) });

    // Names
    std::vector<uint8_t> msg;
    if (!load(pak, COSTUME_NAMES, msg)) return fail("costume name file not found in the game's paks");
    if (!add_msg_entries(msg, names, &err)) return fail("costume name file: " + err);
    st.files.push_back({ COSTUME_NAMES, std::move(msg) });

    st.fighters.assign(fighters.begin(), fighters.end());
    st.ok = true;
    return st;
}
