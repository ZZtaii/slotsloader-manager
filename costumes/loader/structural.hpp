#pragma once
// Structural files built from the game's own: the files the costume pak replaces to declare the extra
// slots (the root fighter scene and the version scenes: a via.Folder per slot; the costume table: a
// record per slot; the costume name file: "Outfit I" .. "Outfit C"). Built from the game's current files
// at each pak build, so that a game update that adds a fighter or an outfit is taken in without new
// static files. The rules are those of costumes/tools/static_slots.py, which made the shipped copies in
// loader/static/: for today's game the output is the same bytes (tests/game_derived_real_input.py).
#include <cstdint>
#include <string>
#include <vector>

class PakReader;

struct GameStructural {
    bool ok = false;
    std::string why;                         // when !ok
    std::vector<int> fighters;               // fighters of the game's costume table, ascending
    struct Record { int fighter, costume_no, record_id; };   // ManageId == record id
    std::vector<Record> records;             // 100 slot records per fighter that a scene declares
    struct File { std::string pak; std::vector<uint8_t> data; };
    std::vector<File> files;                 // root scene, version scenes, costume table (every slot
                                             // record: trimmed later), costume name file
    std::vector<std::string> notes;          // worth a log line: fighters left without slots
};

// Fighters of the game's costume table, ascending. False when the table is missing or not recognised.
bool game_costume_fighters(PakReader& pak, std::vector<int>& out);

// The version scenes that can host slots, in game version order: ver_02_0300 (the first one of the
// shipped data; the DLC fighters' earlier debut scenes are re-declared there with their DriveTech
// folder) up to ver_09_9999. Each path is probed in the paks, so the list costs a lookup per name.
std::vector<std::string> slot_version_scene_paths();
// True for exactly the names slot_version_scene_paths() lists
bool is_slot_version_scene(const std::string& pak_path);

// Builds the structural files from the game's (pak: the base pak with the DLC paks read over it).
//   root scene     100 folders esfNNNv<slot_min..slot_max> per fighter of the costume table that has
//                  its Outfit 1 (v00) there, copies of that v00 folder;
//   version scenes in each scene of slot_version_scene_paths(), the fighters it brings first (neither
//                  in the root scene nor in an earlier version scene), copies of their v04 (DriveTech),
//                  else of their highest vNN folder; a scene that brings none is not replaced;
//   costume table  100 records per fighter given folders above: ids 5000 + costume_slot_index * 100 +
//                  offset (ManageId the same), sort numbers 101 + the same, costume numbers slot_min.. ;
//                  the 100 name messages (ids msg_id_base..) before them;
//   name file      the game's entries plus FighterCostumeMessage6..105 = "Outfit I" .. "Outfit C".
// Any file missing or laid out in a way the code does not know fails the whole build (ok = false,
// why says what): the caller then uses the shipped copies.
GameStructural build_game_structural(PakReader& pak, int slot_min, int slot_max, uint32_t msg_id_base);
