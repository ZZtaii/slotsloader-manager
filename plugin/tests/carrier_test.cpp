// Unit test of plugin/src/carrier.hpp: the carrier of the online alias, a stale carrier.
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

    std::printf(fails ? "%d FAILED\n" : "all ok\n", fails);
    return fails ? 1 : 0;
}
