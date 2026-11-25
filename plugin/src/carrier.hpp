// SF6 Slots — the carrier of the online alias. Pure logic, no game access (plugin/tests).
//
// While a fighter wears a slot chosen in Battle Settings, the save data and the network see one of
// the game's own outfits, the carrier, and the plugin dresses the player's own fighter in the slot on
// the player's screen only. The carrier is the player's setting (Options > Interface > Other Settings,
// "Outfit Others See", settings.json "online_outfit"): Outfit 1 (costume 0, every fighter's default,
// the default since Slots Loader 1.15) or DriveTech (costume 4; Outfit 1 for a fighter without it, Arjun,
// and since 24/11/2026 for a fighter whose DriveTech the account does not really own: owned_carrier).
// A fighter with neither in the game's costume table is left out of the alias.
#pragma once

#include <cstdint>
#include <set>

namespace slots::carrier {

constexpr int kOutfit1 = 0, kDriveTech = 4;

// The fighter's carrier from the costume numbers the game's costume table has for it and the setting
// (`drivetech`: DriveTech wanted); -1 when it has neither (the fighter is left out of the alias)
inline int choose(const std::set<int>& costumes, bool drivetech = false) {
    if (drivetech && costumes.count(kDriveTech)) return kDriveTech;
    if (costumes.count(kOutfit1)) return kOutfit1;
    return costumes.count(kDriveTech) ? kDriveTech : -1;
}

// The save data holds a former carrier: it goes to the carrier. `saved` is the carrier the save was
// given before (state.json; DriveTech when it names none: Slots Loader 1.14 and before used
// DriveTech for every fighter that has it), or before the setting changed. Moved only when a slot is
// the intent (the plugin put that costume there), or when the fighter does not have that costume at
// all; otherwise the costume is the player's own choice.
inline bool stale(int saved, int carrier, int save_cos, bool intent, bool fighter_has_it) {
    return carrier >= 0 && saved != carrier && save_cos == saved && (intent || !fighter_has_it);
}

// ---- Wael 21/11: "tout spoof le costume 1 ou drive tech, couleur 1 ou 2" ----
// From 21/11 to 24/11/2026 the save and the network held only Outfit 1 or DriveTech, in colour 1 or 2 (0 or 1): anything else
// (a slot, the game's Outfit 2 / 3 / 4..., a colour from 3 on) was worn on this PC only, over the carrier. A game outfit or
// colour sent as it is raised the server's communication error 50301-30003 for players whose game counts as theirs what their
// account does not have (an unlock-all script). Since 24/11/2026 what the account really owns is sent as it is (decide below).

// The carrier outfits: Outfit 1 or DriveTech (whether the account owns DriveTech: owned_carrier)
inline bool safe_outfit(int costume) { return costume == kOutfit1 || costume == kDriveTech; }
// A colour every outfit has for every player who has the outfit: Color 1 or Color 2 (no colour record: never bought)
inline bool safe_colour(int colour) { return colour == 0 || colour == 1; }

// The colour the save holds for a choice worn in `colour` over a carrier whose colours are `carrier_colours` (empty: not
// known): the colour itself when it is Color 1 or 2 and the carrier has it, else Color 1 (Color 2 when only that one is
// listed). Never Color 3 or later, whatever the list says.
inline int net_colour(int colour, const std::set<int>& carrier_colours) {
    auto has = [&](int c) { return carrier_colours.empty() || carrier_colours.count(c) != 0; };
    if (safe_colour(colour) && has(colour)) return colour;
    if (!has(0) && has(1)) return 1;
    return 0;
}

// ---- Wael 24/11/2026: "si on a vraiment le costume 2, il faut que l'adversaire le voie" ----
// What the ACCOUNT really owns goes on the network as it is; everything else is worn on this PC only, over the carrier
// (true_owner.hpp reads the ownership: an unlock-all script makes the game's own lists count outfits and colours the
// account does not have, and the server answers 50301-30003 when one is sent). In every mode the save feeds (ranked,
// casual, Battle Hub and its CPU battles, rooms, online training, team battle), whatever the save held before:
//   - a mod slot never goes on the network: the carrier, in Color 1 or 2 (net_colour);
//   - a game outfit the account really owns is sent as it is; one only an unlock-all script gives: the carrier, worn here;
//   - a colour from Color 3 on (number >= 2) of an outfit sent as it is: sent when the account really owns it, else Color 1
//     goes on the network and the colour is worn here; on the carrier the colour is always Color 1 or 2;
//   - not known (the ownership not read yet, no Steam to ask about a DLC, a choice nobody can name): Color 1 of the carrier,
//     which is Outfit 1 unless DriveTech is asked for and known to be owned; never the save's value. An outfit known to be
//     owned whose colour is not known goes in Color 1 (every player has Color 1 of an outfit they own).
// What the player sees on their own screen never changes: always the real choice.

enum class Own : uint8_t { Unknown, No, Yes };   // the account really owns it; Unknown: the plugin cannot tell

// The carrier: DriveTech when the setting asks for it, the fighter has it AND the account really owns it ("costume 1 ou
// drive tech selon le choix et dispo du costume"); else Outfit 1. -1: the fighter has neither (left out of the alias).
inline int owned_carrier(const std::set<int>& costumes, bool drivetech, Own dt) {
    if (dt != Own::Yes) return costumes.count(kOutfit1) ? kOutfit1 : -1;
    return choose(costumes, drivetech);
}

// What the player chose: an outfit (a slot's or the game's number) and its colour (-1: not known, Color 1)
struct Choice { int costume = -1, colour = -1; };
// What the plugin keeps besides the save (state.json's intent): the outfit worn when the network holds another one, else 0;
// the colour worn when only the colour differs from the network's (or with that outfit), else -1
struct Kept { int slot = 0, color = -1; };

enum class Kind : uint8_t { NotKnown, Slot, Game };
struct Ask {
    Kind kind = Kind::NotKnown;          // Slot: one of the loader's outfits (the Random outfit included); Game: the game's own
    int costume = -1, colour = -1;       // the real choice
    Own outfit = Own::Unknown;           // Game: the account really owns that outfit (Outfit 1: always, every fighter's default)
    Own colour_own = Own::Unknown;       // Game, colour >= 2: the account really owns that colour of that outfit
    int carrier = -1;                    // owned_carrier(); -1: Outfit 1
    const std::set<int>* carrier_colours = nullptr;   // the carrier's colours (none: not known)
};
struct Pick {
    int net_cos = kOutfit1, net_col = 0;      // what the save and the network hold
    int worn_cos = kOutfit1, worn_col = 0;    // what the player's fighter wears on this PC
    const char* why = "";
};

// THE decision: what the network gets and what this PC wears for the player's real choice
inline Pick decide(const Ask& a) {
    static const std::set<int> none;
    const std::set<int>& cc = a.carrier_colours ? *a.carrier_colours : none;
    const int car = a.carrier >= 0 ? a.carrier : kOutfit1;
    const int col = a.colour >= 0 ? a.colour : 0;
    Pick p;
    p.net_cos = p.worn_cos = car;
    p.net_col = p.worn_col = 0;
    if (a.kind == Kind::NotKnown || a.costume < 0) {
        p.why = "not known what the player chose: the carrier in Color 1";
        return p;
    }
    p.worn_cos = a.costume;
    p.worn_col = col;
    if (a.kind == Kind::Slot) {
        p.net_col = net_colour(col, cc);
        p.why = "a mod slot never goes on the network: the carrier";
        return p;
    }
    const Own o = a.costume == kOutfit1 ? Own::Yes : a.outfit;
    if (o == Own::No) {
        p.net_col = net_colour(col, cc);
        p.why = "an outfit the account does not really own (only an unlock-all script gives it): the carrier";
        return p;
    }
    if (o == Own::Unknown) {
        p.why = "not known whether the account owns that outfit: the carrier in Color 1";
        return p;
    }
    p.net_cos = a.costume;
    if (safe_colour(col) || a.colour_own == Own::Yes) {
        p.net_col = col;
        p.why = safe_colour(col) ? "an outfit the account owns: sent as it is" : "an outfit and a colour the account owns: sent as they are";
        return p;
    }
    p.net_col = 0;
    p.why = a.colour_own == Own::No ? "a colour the account does not really own: Color 1 sent, the colour worn on this PC"
                                    : "not known whether the account owns that colour: Color 1 sent, the colour worn on this PC";
    return p;
}

// The intent the plugin keeps for a pick (what this PC wears that the network does not hold)
inline Kept intent_of(const Pick& p) {
    if (p.worn_cos != p.net_cos) return { p.worn_cos, p.worn_col };
    if (p.worn_col != p.net_col) return { 0, p.worn_col };
    return {};
}

// The player's real choice outside Battle Settings, from what the save holds and the intent kept (`intent_slot` 0: none,
// `intent_col` -1: none). `outfit_ok` / `colour_ok`: the network may hold the save's outfit / its colour as they are (decide).
//   - the save's outfit the network may not hold (a slot's number, an outfit only an unlock-all script gives): put there
//     outside the menu (a choice the plugin did not see, a save of an older version), it is the choice, in the save's colour;
//   - else an intent outfit (the save holds the carrier the plugin wrote for it): that outfit, in a colour from Color 3 on
//     found in the save (the plugin writes Color 1 / 2 only there), else in the intent's colour;
//   - else the save's outfit: a colour the network may not hold found in the save is the choice; Color 1 / 2 in the save
//     with an intent colour (worn on this PC over what the plugin wrote): the intent's; else the save's.
inline Choice real_choice(int save_cos, int save_col, int intent_slot, int intent_col, bool outfit_ok, bool colour_ok) {
    if (!outfit_ok) return { save_cos, save_col };
    if (intent_slot > 0) return { intent_slot, !safe_colour(save_col) && save_col >= 0 ? save_col : intent_col >= 0 ? intent_col : save_col };
    if (!colour_ok) return { save_cos, save_col };
    if (intent_col >= 0 && safe_colour(save_col)) return { save_cos, intent_col };
    return { save_cos, save_col };
}

} // namespace slots::carrier
