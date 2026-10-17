#include "fighter_names.hpp"

#include <algorithm>
#include <cstdlib>
#include <string_view>
#include <vector>

#include "pak.hpp"
#include "structural_msg.hpp"

static const char* const kNameMsg = "natives/stm/product/message/cmn/001/wordlist/fighternamemessage.msg.21";
static const char kPrefix[] = "FighterNameMessage";

std::map<int, std::string> game_fighter_names(PakReader& pak) {
    std::map<int, std::string> result;
    try {
        const PakEntry* e = pak.find(pak_path_hash(std::string_view(kNameMsg)));
        if (!e) return result;
        std::vector<uint8_t> data = pak.read(*e);
        std::map<std::string, std::string> texts;
        if (data.empty() || !read_msg_texts(data, 1, texts)) return result;   // English = via.Language 1
        for (const auto& [name, text] : texts) {
            if (text.empty() || name.compare(0, sizeof(kPrefix) - 1, kPrefix) != 0) continue;
            const std::string digits = name.substr(sizeof(kPrefix) - 1);
            if (digits.empty() || digits.size() > 5 ||
                !std::all_of(digits.begin(), digits.end(), [](char c) { return c >= '0' && c <= '9'; }))
                continue;
            result[std::atoi(digits.c_str())] = text;
        }
    } catch (...) {
        result.clear();
    }
    return result;
}

std::string fighter_folder_name(const std::string& english) {
    std::string s;
    for (unsigned char c : english) {
        // the costume_mods folders are created from narrow strings: printable ASCII only
        if (c < 0x20 || c >= 0x7F || std::string_view("<>:\"/\\|?*").find(static_cast<char>(c)) != std::string_view::npos) continue;
        s.push_back(static_cast<char>(c));
    }
    while (!s.empty() && (s.back() == ' ' || s.back() == '.')) s.pop_back();
    size_t a = 0;
    while (a < s.size() && s[a] == ' ') ++a;
    s.erase(0, a);
    return s;
}
