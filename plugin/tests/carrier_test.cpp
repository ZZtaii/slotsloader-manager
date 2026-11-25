// Unit test of plugin/src/carrier.hpp: the carrier of the online alias, a stale carrier, the network decision (24/11/2026).
// Build and run: build_carrier_test.bat (prints "all ok" and exits 0 when every check passes).
#include "../src/carrier.hpp"

#include <cstdio>

using namespace slots::carrier;

static int fails = 0;
static void check(bool ok, const char* what) {
    std::printf("  %-72s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++fails;
}

int main() {
    const int O1 = 0, O2 = 1, DT = 4;

    // choose: from the costume numbers of the game's costume table
    check(choose({0, 1, 2, 3, 4}) == O1, "Outfit 1 for every fighter, DriveTech or not");
    check(choose({0, 1}) == O1, "Outfit 1 without DriveTech (Arjun)");
    check(choose({0}) == O1, "Outfit 1 alone");
    check(choose({1, 2}) == -1, "neither Outfit 1 nor DriveTech: no carrier");
    check(choose({}) == -1, "no record: no carrier");
    check(choose({0, 1, 4}, true) == DT, "DriveTech set: DriveTech where the fighter has it");
    check(choose({0, 1}, true) == O1, "DriveTech set, no DriveTech (Arjun): Outfit 1");
    check(choose({1, 4}) == DT, "no Outfit 1: DriveTech");

    // stale: the save still holds a former carrier (DriveTech until 1.14)
    check(stale(DT, O1, DT, true, true), "DriveTech carrier then, slot intent: to Outfit 1");
    check(!stale(DT, O1, DT, false, true), "DriveTech chosen by the player, no intent: kept");
    check(stale(DT, O1, DT, false, false), "DriveTech written for Arjun, no intent: to Outfit 1");
    check(!stale(DT, O1, O1, true, true), "save on Outfit 1 already: nothing moves");
    check(!stale(DT, O1, O2, true, true), "another outfit in the save: not a carrier, left");
    check(!stale(O1, O1, O1, true, true), "Outfit 1 then and now: nothing moves");
    check(!stale(DT, -1, DT, true, false), "no carrier now: nothing moves");
    check(stale(O1, DT, O1, true, true), "setting changed to DriveTech, slot intent: to DriveTech");
    check(!stale(O1, DT, O1, false, true), "setting changed to DriveTech, Outfit 1 chosen: the player's");

    // safe_outfit / safe_colour / net_colour: what the network may hold (Wael 21/11)
    check(safe_outfit(O1) && safe_outfit(DT), "Outfit 1 and DriveTech are safe");
    check(!safe_outfit(O2) && !safe_outfit(2) && !safe_outfit(3) && !safe_outfit(5), "Outfit 2, 3, 4 and later are not");
    check(safe_colour(0) && safe_colour(1) && !safe_colour(2) && !safe_colour(9) && !safe_colour(-1), "Color 1 and 2 only");
    check(net_colour(1, {0, 1, 2, 3}) == 1, "Color 2 chosen: Color 2 sent");
    check(net_colour(0, {0, 1, 2}) == 0, "Color 1 chosen: Color 1 sent");
    check(net_colour(6, {0, 1, 2, 3, 4, 5, 6}) == 0, "Color 7 chosen: Color 1 sent");
    check(net_colour(1, {0, 2}) == 0, "Color 2 not in the carrier: Color 1 sent");
    check(net_colour(5, {1, 3}) == 1, "no Color 1 in the carrier: Color 2 sent");
    check(net_colour(5, {}) == 0, "carrier colours not known: Color 1 sent");
    check(net_colour(1, {}) == 1, "carrier colours not known, Color 2 chosen: Color 2 sent");
    check(net_colour(7, {3, 4, 5}) == 0, "neither Color 1 nor 2 listed: Color 1 all the same, never Color 4+");

    // ---- Wael 24/11/2026: what the account really owns is sent as it is (owned_carrier, decide, intent_of, real_choice) ----
    const Own Y = Own::Yes, N = Own::No, U = Own::Unknown;
    const std::set<int> all = {0, 1, 2, 3, 4}, arjun = {0, 1, 2};
    check(owned_carrier(all, true, Y) == DT, "DriveTech carrier: asked for, present and really owned");
    check(owned_carrier(all, true, N) == O1, "DriveTech asked for but not owned (unlock-all only): Outfit 1");
    check(owned_carrier(all, true, U) == O1, "DriveTech asked for, ownership not known: Outfit 1");
    check(owned_carrier(arjun, true, Y) == O1, "DriveTech asked for, the fighter has none (Arjun): Outfit 1");
    check(owned_carrier(all, false, Y) == O1, "Outfit 1 asked for, DriveTech owned: Outfit 1");
    check(owned_carrier({}, true, Y) == -1 && owned_carrier({1, 2}, false, U) == -1, "neither Outfit 1 nor DriveTech: no carrier");

    const std::set<int> o1_cols = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9}, no_c2 = {0, 2, 3};
    auto ask = [&](Kind k, int cos, int col, Own outfit = Own::Unknown, Own colour = Own::Unknown, int car = 0, const std::set<int>* cc = nullptr) {
        Ask a;
        a.kind = k; a.costume = cos; a.colour = col; a.outfit = outfit; a.colour_own = colour; a.carrier = car;
        a.carrier_colours = cc ? cc : &o1_cols;
        return a;
    };
    auto is = [](const Pick& p, int nc, int ncol, int wc, int wcol) {
        return p.net_cos == nc && p.net_col == ncol && p.worn_cos == wc && p.worn_col == wcol;
    };
    {
        const Pick p = decide(ask(Kind::Slot, 6, 3));
        check(is(p, O1, 0, 6, 3), "slot v06 colour 3: the network gets Outfit 1 Color 1, this PC wears the slot");
        const Kept k = intent_of(p);
        check(k.slot == 6 && k.color == 3, "slot: kept as the intent with its colour");
        check(is(decide(ask(Kind::Slot, 6, 1)), O1, 1, 6, 1), "slot in Color 2: Color 2 of the carrier sent");
        check(is(decide(ask(Kind::Slot, 6, 1, U, U, O1, &no_c2)), O1, 0, 6, 1), "slot in Color 2, the carrier has no Color 2: Color 1");
        check(is(decide(ask(Kind::Slot, 7, 5, U, U, DT)), DT, 0, 7, 5), "slot over a DriveTech carrier: DriveTech sent");
    }
    {
        const Pick p = decide(ask(Kind::Game, O2, 1, Y));
        check(is(p, O2, 1, O2, 1), "Outfit 2 really owned, Color 2: sent as it is");
        const Kept k = intent_of(p);
        check(k.slot == 0 && k.color == -1, "owned outfit sent as it is: no intent");
    }
    {
        const Pick p = decide(ask(Kind::Game, 2, 1, N));
        check(is(p, O1, 1, 2, 1), "Outfit 3 only an unlock-all script gives: the carrier on the network, Outfit 3 worn here");
        check(intent_of(p).slot == 2 && intent_of(p).color == 1, "unlock-all-only outfit: kept as the intent");
        check(is(decide(ask(Kind::Game, 2, 6, N)), O1, 0, 2, 6), "unlock-all-only outfit in Color 7: the carrier in Color 1");
        check(is(decide(ask(Kind::Game, 2, 1, N, U, DT)), DT, 1, 2, 1), "unlock-all-only outfit, DriveTech carrier: DriveTech");
    }
    check(is(decide(ask(Kind::Game, O2, 5, Y, Y)), O2, 5, O2, 5), "owned Outfit 2 in an owned Color 6: sent as they are");
    {
        const Pick p = decide(ask(Kind::Game, O2, 5, Y, N));
        check(is(p, O2, 0, O2, 5), "owned Outfit 2, Color 6 not owned: Outfit 2 Color 1 sent, Color 6 worn here");
        check(intent_of(p).slot == 0 && intent_of(p).color == 5, "colour not owned: only the colour kept as the intent");
    }
    check(is(decide(ask(Kind::Game, O1, 7, U, N)), O1, 0, O1, 7), "Outfit 1 (always owned), Color 8 not owned: Color 1 sent");
    check(is(decide(ask(Kind::Game, O1, 7, U, Y)), O1, 7, O1, 7), "Outfit 1 in an owned Color 8: sent as it is");
    check(is(decide(ask(Kind::Game, O2, 5, Y, U)), O2, 0, O2, 5), "owned outfit, colour not known: Color 1 of it sent");
    check(is(decide(ask(Kind::Game, DT, 3, Y, Y, DT)), DT, 3, DT, 3), "DriveTech owned and chosen: sent as it is");
    check(is(decide(ask(Kind::Game, DT, 3, N, U, O1)), O1, 0, DT, 3), "DriveTech chosen, not owned: Outfit 1 sent, DriveTech worn");
    // not known: Outfit 1 Color 1, never the save's value
    check(is(decide(ask(Kind::Game, 2, 4, U, U, -1)), O1, 0, 2, 4), "ownership not known: Outfit 1 Color 1 sent, the choice worn here");
    check(is(decide(ask(Kind::Game, 2, 1, U, U, O1)), O1, 0, 2, 1), "ownership not known, Color 2 chosen: Color 1 all the same");
    check(is(decide(ask(Kind::NotKnown, 17, 5)), O1, 0, O1, 0), "a number nobody knows: Outfit 1 Color 1, nothing worn over it");
    check(is(decide(ask(Kind::Game, -1, -1, Y, Y)), O1, 0, O1, 0), "no choice at all: Outfit 1 Color 1");
    check(is(decide(Ask{}), O1, 0, O1, 0), "nothing known at all: Outfit 1 Color 1");
    {
        const Pick p = decide(ask(Kind::NotKnown, 17, 5));
        check(intent_of(p).slot == 0 && intent_of(p).color == -1, "a number nobody knows: no intent");
    }

    // real_choice: the save read again outside the menu
    {
        // a raw slot number found in the save (a tester's v1.28.0 log: F13 c6 in a Battle Hub CPU battle): intent + carrier
        const Choice c = real_choice(6, 3, 0, -1, false, false);
        check(c.costume == 6 && c.colour == 3, "raw slot number in the save: it is the choice");
        const Pick p = decide(ask(Kind::Slot, c.costume, c.colour));
        const Kept k = intent_of(p);
        check(k.slot == 6 && k.color == 3 && p.net_cos == O1 && p.net_col == 0, "raw slot number: intent v06 colour 3, the save gets Outfit 1 Color 1");
    }
    {
        const Choice c = real_choice(O1, 1, 6, 4, true, true);
        check(c.costume == 6 && c.colour == 4, "the save holds the carrier for an intent: the intent, its colour");
        const Choice d = real_choice(O1, 5, 6, 4, true, true);
        check(d.costume == 6 && d.colour == 5, "intent kept, Color 6 found in the save: the new colour");
        const Choice e = real_choice(O1, 5, 6, 4, true, false);
        check(e.costume == 6 && e.colour == 5, "intent kept, a colour the network must not hold found in the save: the new colour");
    }
    {
        const Choice c = real_choice(2, 1, 0, -1, false, false);
        check(c.costume == 2 && c.colour == 1, "unlock-all-only outfit found in the save: it is the choice");
        const Choice d = real_choice(2, 1, 6, 4, false, false);
        check(d.costume == 2 && d.colour == 1, "unlock-all-only outfit found in the save over an older intent: the save wins");
    }
    {
        const Choice c = real_choice(O2, 3, 0, -1, true, true);
        check(c.costume == O2 && c.colour == 3, "owned outfit and colour in the save: as they are");
        const Choice d = real_choice(O2, 6, 0, -1, true, false);
        check(d.costume == O2 && d.colour == 6, "owned outfit, a colour not owned in the save: it is the choice");
        const Choice e = real_choice(O1, 0, 0, 5, true, true);
        check(e.costume == O1 && e.colour == 5, "Color 1 in the save, an intent colour: the intent's colour (worn here)");
        const Choice f = real_choice(O1, 7, 0, 5, true, true);
        check(f.costume == O1 && f.colour == 7, "an owned Color 8 in the save over an intent colour: the save's");
    }
    {
        // an old intent (21/11 - 24/11: Outfit 2 aliased) whose outfit the account really owns: sent as it is now
        const Choice c = real_choice(O1, 1, O2, 1, true, true);
        const Pick p = decide(ask(Kind::Game, c.costume, c.colour, Y));
        check(is(p, O2, 1, O2, 1) && intent_of(p).slot == 0, "an older alias of an owned Outfit 2: the save gets Outfit 2, no intent");
    }

    std::printf(fails ? "%d FAILED\n" : "all ok\n", fails);
    return fails ? 1 : 0;
}
