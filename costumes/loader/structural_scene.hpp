#pragma once
// Plain-bytes editing of the game's esf scenes (RE Engine .scn.20, RSZ): lists the costume
// folders (via.Folder named esfNNNvCC) and appends new ones. No REFramework / Windows dependency.
#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <vector>

// Costume folders of an esf scene: fighter dir ("esf034") -> costume numbers found as esfNNNvCC.
// Empty map if the scene layout is not recognised.
std::map<std::string, std::set<int>> scene_costume_folders(const std::vector<uint8_t>& scn);
// The same, telling a scene without costume folders (ver_02_0110 has none) from one whose layout is not
// recognised (false, err says why).
bool read_scene_costume_folders(const std::vector<uint8_t>& scn, std::map<std::string, std::set<int>>& out,
                                std::string* err);

// Which folder of a fighter the new slot folders copy (the rules of costumes/tools/static_slots.py):
//   V00         the root fighter scene: esfNNNv00;
//   V04_OR_LAST a version scene: esfNNNv04 (DriveTech) when the scene has it, else the fighter's
//               highest esfNNNvCC (Arjun's v01 in ver_02_0500); its esfNNNvCC folders must then all
//               have the same Tag, flags and UniversalOffset, otherwise the scene is refused.
enum class SlotTemplate { V00, V04_OR_LAST };

// Appends folders esfNNNv<first..last-1> for each fighter dir, copied from its template folder under
// the same parent: the fighters with a v04 first, then the others, each group in name order (the order
// static_slots.py writes them). A fighter with no template folder in this scene is refused. False and
// data untouched on any layout it does not recognise; err says why. Byte-identical to static_slots.py
// (and to REasy-parser, which made the first static copies).
bool add_scene_folders(std::vector<uint8_t>& scn, const std::vector<std::string>& fighter_dirs,
                       SlotTemplate rule, int first, int last, std::string* err);
