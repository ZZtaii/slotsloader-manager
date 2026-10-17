// Unit test of plugin/src/ownership.hpp: which slots the player sees from the game's answers.
// Build and run: build_ownership_test.bat (prints "all ok" and exits 0 when every check passes).
#include "../src/ownership.hpp"

#include <cstdio>

using slots::ownership::Book;
using slots::ownership::State;
using slots::ownership::always_shown;
using slots::ownership::outfit_label;

static int fails = 0;
static void check(bool ok, const char* what) {
    std::printf("  %-78s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++fails;
}

int main() {
    const int RYU = 1, KEN = 2, ARJUN = 34;
    const int O1 = 0, O2 = 1, O3 = 2, O4 = 3, DT = 4;

    // ---- no answer yet ----
    {
        Book b;
        check(b.check(), "the check is on by default");
        check(b.state(RYU, O2) == State::Unknown, "no answer yet: unknown");
        check(b.shown(RYU, O2), "no answer yet: shown (as before the check)");
        check(b.open(RYU, O2), "no answer yet: asked");
        check(b.shown(RYU, -1), "outfit not known (-1): shown");
        check(!b.open(RYU, -1), "outfit not known (-1): never asked");
    }
    // ---- Outfit 1 and DriveTech: always shown, never asked ----
    {
        Book b;
        check(always_shown(O1) && always_shown(DT), "Outfit 1 and DriveTech always shown");
        check(!always_shown(O2) && !always_shown(O3) && !always_shown(O4), "Outfit 2, 3, 4 can be hidden");
        check(!b.open(RYU, O1) && !b.open(RYU, DT), "Outfit 1 and DriveTech: never asked");
        b.answer(RYU, DT, {0, 1, 2}); // a list without DriveTech
        check(b.shown(RYU, DT) && b.state(RYU, DT) == State::Unknown, "DriveTech missing from the list: still shown, not recorded");
        b.answer(RYU, O1, {0});
        check(b.shown(RYU, O1), "Outfit 1: shown");
    }
    // ---- the game's list ----
    {
        Book b;
        b.answer(RYU, O2, {});
        check(b.state(RYU, O2) == State::Unknown && b.shown(RYU, O2), "empty list (inventory not loaded): no answer, shown");
        b.answer(RYU, O2, {4, 5});
        check(b.state(RYU, O2) == State::Unknown, "list without Outfit 1: no answer");
    }
    {
        Book b;
        b.answer(RYU, O2, {0, 4});
        check(!b.shown(RYU, O2) && b.state(RYU, O2) == State::NotOwned, "Outfit 2 not listed: hidden");
        check(b.open(RYU, O2), "hidden outfit: asked again later");
        check(b.state(KEN, O2) == State::Unknown && b.shown(KEN, O2), "another fighter is not affected");
        b.answer(RYU, O2, {});
        check(!b.shown(RYU, O2), "no answer afterwards: stays hidden");
        b.answer(RYU, O2, {0, 1, 4});
        check(b.shown(RYU, O2) && b.state(RYU, O2) == State::Owned, "listed later (bought, DLC, unlock-all): shown");
        check(!b.open(RYU, O2), "owned: not asked again");
        b.answer(RYU, O2, {0, 4});
        check(b.shown(RYU, O2), "not listed after being owned (online, Lua stopped): stays shown");
    }
    {
        Book b;
        b.answer(RYU, O4, {0, 1, 2, 3, 4});
        check(b.shown(RYU, O4) && b.state(RYU, O4) == State::Owned, "Outfit 4 listed: owned, shown");
        b.answer(KEN, O4, {0, 1, 2, 4});
        check(!b.shown(KEN, O4), "Outfit 4 not listed: hidden");
    }
    // ---- before the login ----
    {
        Book b;
        b.answer(RYU, O3, {0, 4}, false);
        check(b.state(RYU, O3) == State::Unknown && b.shown(RYU, O3), "before the login a missing outfit is not denied");
        b.answer(RYU, O3, {0, 2, 4}, false);
        check(b.state(RYU, O3) == State::Owned, "before the login a listed outfit is owned");
        b.answer(RYU, O3, {0, 4}, true);
        check(b.shown(RYU, O3), "owned before the login stays shown");
    }
    // ---- a fighter without DriveTech (Arjun: costumes 0, 1 and 100, no costume id 4 (DriveTech, C5)) ----
    {
        Book b;
        const std::vector<uint32_t> arjun_owns_o1 = {0, 100};
        b.answer(ARJUN, O1, arjun_owns_o1);
        b.answer(ARJUN, O2, arjun_owns_o1);
        check(b.shown(ARJUN, O1), "Arjun, slot of Outfit 1, no DriveTech in the list: shown");
        check(!b.shown(ARJUN, O2), "Arjun, slot of Outfit 2 not listed: hidden");
        b.answer(ARJUN, O2, {0, 1, 100});
        check(b.shown(ARJUN, O2), "Arjun, Outfit 2 listed later: shown");
        Book t;
        t.answer(ARJUN, O2, {});   // trial character: nothing listed
        check(t.shown(ARJUN, O2), "Arjun on trial (empty list): no answer, shown");
    }
    {
        Book b;
        b.answer(RYU, 9, {0, 1, 4});
        check(!b.shown(RYU, 9), "an outfit the game does not have: hidden");
    }
    // ---- check off (the private opt-out, or the game's method missing) ----
    {
        Book b(false);
        check(!b.check(), "check off: reported off");
        check(!b.open(RYU, O2) && !b.open(RYU, O4), "check off: nothing asked");
        b.answer(RYU, O2, {0, 4});
        check(b.shown(RYU, O2) && b.state(RYU, O2) == State::Unknown, "check off: an answer is ignored, shown");
        check(b.shown(RYU, O3) && b.shown(RYU, O1) && b.shown(RYU, DT), "check off: every slot shown");
    }
    {
        Book b;
        b.answer(RYU, O2, {0, 4});
        check(!b.shown(RYU, O2), "check on: Outfit 2 not listed hidden ...");
        b.set_check(false);
        check(b.shown(RYU, O2) && !b.open(RYU, O2), "... then check off (method missing): shown, not asked");
    }

    check(outfit_label(0) == "Outfit 1", "label of costume 0");
    check(outfit_label(1) == "Outfit 2", "label of costume 1");
    check(outfit_label(3) == "Outfit 4", "label of costume 3");
    check(outfit_label(4) == "DriveTech", "label of costume id 4 (DriveTech, C5)");
    check(outfit_label(100) == "costume 100", "label of another number");

    std::printf(fails ? "%d FAILED\n" : "all ok\n", fails);
    return fails ? 1 : 0;
}
