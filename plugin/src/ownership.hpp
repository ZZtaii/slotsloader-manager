// SF6 Slots - which costume slots the player sees. Pure logic, no game access (plugin/tests).
//
// A slot made from fighter F's Outfit 2, 3 or 4 is shown only when the game counts that outfit as
// the player's. The game's answer is the list of costume numbers it lists for F
// (app.InventoryManager.CostumeInventory.GetCostumeNoList): the list of the character select
// screen (app.UIFlowUI10505.Param.GetCostumeId) and of Battle Settings
// (app.helper.hGUI.GetEnableFighterCostumeIdList). An outfit is listed when it is a default one,
// bought (in-game currency, shop), or given by a DLC the game counts as valid
// (app.DlcManager.IsValidDlcItem): whatever makes the game say yes counts, an unlock-all script
// that hooks the DLC checks included. Slots made from Outfit 1 or DriveTech are always shown and
// never asked about.
//
// The check can be turned off (a private maintainer opt-out, marker.hpp, read once at start):
// every slot is then shown and nothing is asked, as before this check existed.
#pragma once

#include <algorithm>
#include <cstdint>
#include <map>
#include <string>
#include <utility>
#include <vector>

namespace slots::ownership {

enum class State : uint8_t { Unknown, NotOwned, Owned };

constexpr int kOutfit1   = 0;   // costume 0, every fighter's default outfit
constexpr int kDriveTech = 4;   // costume id 4 (DriveTech, C5)

// Outfit 1 and DriveTech: a slot made from them is always shown, the game is never asked
inline bool always_shown(int outfit) { return outfit == kOutfit1 || outfit == kDriveTech; }

class Book {
public:
    explicit Book(bool check = true) : check_(check) {}

    // false: every slot shown, nothing asked (the private opt-out, or the game's method missing)
    void set_check(bool on) { check_ = on; }
    bool check() const { return check_; }

    // The game listed `listed` for `fighter`; returns the state of `outfit` after that answer.
    // A list without Outfit 1 (costume 0, every fighter's default outfit) is no answer: the
    // inventory is not loaded yet, and the state stays as it was. With may_deny false (before the
    // login: shop purchases and DLC may not be known yet) only "owned" is taken.
    // Owned is kept for the session. Online, REFramework stops Lua scripts, an unlock-all script's
    // hooks with them, and the game would answer with what the player really owns: a slot shown
    // never disappears before the game is closed.
    State answer(int fighter, int outfit, const std::vector<uint32_t>& listed, bool may_deny = true) {
        if (!check_ || outfit < 0 || always_shown(outfit)) return state(fighter, outfit);
        State& s = m_[{fighter, outfit}];
        if (!has(listed, static_cast<uint32_t>(kOutfit1))) return s;
        if (has(listed, static_cast<uint32_t>(outfit))) s = State::Owned;
        else if (may_deny && s != State::Owned) s = State::NotOwned;
        return s;
    }

    State state(int fighter, int outfit) const {
        auto it = m_.find({fighter, outfit});
        return it == m_.end() ? State::Unknown : it->second;
    }

    // Hidden only when the check is on and the game said the outfit is not the player's. No answer
    // yet, an outfit not known (-1), Outfit 1 and DriveTech leave the slot shown.
    bool shown(int fighter, int outfit) const {
        return !check_ || outfit < 0 || always_shown(outfit) || state(fighter, outfit) != State::NotOwned;
    }

    // Still worth asking the game about (check on, an outfit that can be hidden, not owned yet)
    bool open(int fighter, int outfit) const {
        return check_ && outfit >= 0 && !always_shown(outfit) && state(fighter, outfit) != State::Owned;
    }

private:
    static bool has(const std::vector<uint32_t>& v, uint32_t x) {
        return std::find(v.begin(), v.end(), x) != v.end();
    }
    bool check_ = true;
    std::map<std::pair<int, int>, State> m_;
};

// The outfit's name in log lines: costume 0 -> "Outfit 1", 3 -> "Outfit 4", 4 -> "DriveTech"
inline std::string outfit_label(int costume) {
    if (costume == kDriveTech) return "DriveTech";
    if (costume >= 0 && costume < kDriveTech) return "Outfit " + std::to_string(costume + 1);
    return "costume " + std::to_string(costume);
}

} // namespace slots::ownership
