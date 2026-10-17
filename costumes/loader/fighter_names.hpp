#pragma once
#include <map>
#include <string>

class PakReader;

// The fighters' names as the game spells them, read from the game's own message file
// (natives/stm/product/message/cmn/001/wordlist/fighternamemessage.msg.21, entries
// "FighterNameMessage<id>"). A fighter added by a game update is therefore named with no code change.

// {fighter id: English name} from the game's paks (the base pak with the DLC paks read over it, as
// the loader opens them; mod paks are never read). Empty on any failure; never throws. Needs
// structural_msg.cpp, pak.cpp and the zstd / miniz objects.
std::map<int, std::string> game_fighter_names(PakReader& pak);

// A Windows-safe folder name in the Slots Loader's style: spaces and inner dots are kept
// ("C. Viper", "E. Honda"), a trailing dot is dropped ("A.K.I." -> "A.K.I"), characters that are
// invalid in file names and anything outside printable ASCII are removed, and the ends are trimmed.
// Empty if nothing is left.
std::string fighter_folder_name(const std::string& english);
