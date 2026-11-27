// Unit test of plugin/src/random_logic.hpp (the Random outfit: rounds without repeats, a new round once every outfit was given,
// the NSFW filter at the draw, the colour rounds per outfit with the Color Mod's entries, a Random colour of one outfit, the
// online set and the save, state.json's "random").
// Build and run: build_random_test.bat (prints "all ok" and exits 0 when every check passes).
#define _CRT_SECURE_NO_WARNINGS
#include "../src/random_logic.hpp"

#include <cstdio>
#include <random>
#include <set>

using namespace slots;
using namespace slots::rnd;

static int fails = 0;
static void check(bool ok, const char* what) {
    std::printf("  %-80s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++fails;
}

static Rng seeded(uint32_t seed) {
    auto mt = std::make_shared<std::mt19937>(seed);
    return [mt]() { return static_cast<uint32_t>((*mt)()); };
}

int main() {
    std::printf("random_logic\n");
    const std::vector<int> outfits = { 0, 1, 2, 5, 7 };

    {   // one round gives every outfit once, then a new round starts, never with the last one drawn
        bool rounds_ok = true, no_back_to_back = true;
        for (uint32_t seed = 1; seed <= 200; ++seed) {
            Pool p;
            Rng rng = seeded(seed);
            int prev = -1;
            for (int round = 0; round < 6; ++round) {
                std::set<int> got;
                for (size_t i = 0; i < outfits.size(); ++i) {
                    const int v = draw(p, outfits, nullptr, rng);
                    if (v == prev) no_back_to_back = false;
                    prev = v;
                    got.insert(v);
                }
                if (got.size() != outfits.size()) rounds_ok = false;
            }
        }
        check(rounds_ok, "each round of 5 draws gives the 5 outfits once (200 seeds, 6 rounds)");
        check(no_back_to_back, "never the same outfit twice in a row, across rounds too");
    }
    {   // a single outfit: always that one; nothing allowed: -1 and the pool untouched
        Pool p;
        Rng rng = seeded(7);
        bool same = true;
        for (int i = 0; i < 5; ++i) same &= draw(p, { 3 }, nullptr, rng) == 3;
        check(same, "a pool of one outfit gives it at every draw");
        Pool q;
        q.drawn = { 1 };
        q.last = 1;
        check(draw(q, { 1, 2 }, [](int) { return false; }, rng) == -1 && q.drawn.size() == 1 && q.last == 1,
              "nothing allowed: -1, the pool left as it was");
        check(draw(q, {}, nullptr, rng) == -1, "no candidate: -1");
    }
    {   // NSFW outfits hidden: checked at each draw, whatever the round holds
        const std::set<int> nsfw = { 5, 7 };
        bool hidden = false;
        Pool p;
        Rng rng = seeded(11);
        for (int i = 0; i < 300; ++i) {
            const bool show = i >= 150;                       // "Display NSFW Outfits" turned on half way
            const int v = draw(p, outfits, [&](int c) { return show || !nsfw.count(c); }, rng);
            if (!show && nsfw.count(v)) hidden = true;
        }
        check(!hidden, "an NSFW outfit is never drawn while NSFW outfits are hidden");
        Pool q;
        q.drawn = { 0, 1, 2 };                               // the hidden ones left: a new round of what is allowed
        const int v = draw(q, outfits, [&](int c) { return !nsfw.count(c); }, rng);
        check(v >= 0 && !nsfw.count(v) && q.drawn.size() == 1, "every allowed outfit given: a new round of allowed ones only");
    }
    {   // an outfit gone from the list (its mod removed) is forgotten by the round; a new one joins it
        Pool p;
        p.drawn = { 0, 9 };
        p.last = 9;
        Rng rng = seeded(3);
        const int v = draw(p, { 0, 1 }, nullptr, rng);
        check(v == 1 && p.drawn == std::vector<int>({ 0, 1 }), "an outfit no longer listed leaves the round, the one left is drawn");
        const int w = draw(p, { 0, 1, 4 }, nullptr, rng);
        check(w == 4, "a new outfit listed is drawn before the round ends");
    }
    {   // the colour: a round per outfit, colour 0 when none is known
        Fighter f;
        Rng rng = seeded(5);
        auto colours = [](int cos) { return cos == 1 ? std::vector<int>{ 0, 1, 2 } : std::vector<int>{}; };
        std::set<int> c1;
        bool zero = true;
        for (int i = 0; i < 6; ++i) {
            const Draw d = draw_outfit(f, { 1, 8 }, nullptr, colours, rng);
            if (d.costume == 1) c1.insert(d.color);
            else zero &= d.color == 0;
        }
        check(c1.size() == 3, "outfit 1 drawn 3 times: its 3 colours, one round");
        check(zero, "an outfit without known colours: colour 0");
        check(!draw_outfit(f, { 1 }, [](int) { return false; }, colours, rng).valid(), "no outfit allowed: no draw");
    }
    {   // the Color Mod's entries: a number of their own per file, riding on colour 02
        const int rw = entry_value("..\\color_mods\\Ryu\\pack\\esf001_001_cmd_003.user.2");
        const int mc = entry_value("..\\color_mods\\Ryu\\MANUALCOLORS_01\\esf001_001_cmd_001.user.2");
        check(is_entry(rw) && is_entry(mc) && rw != mc, "two entry files: two entry numbers, both entries");
        check(rw == entry_value("..\\color_mods\\Ryu\\pack\\esf001_001_cmd_003.user.2"), "the same file: the same number (a new pack moves no round)");
        check(!is_entry(0) && !is_entry(9) && !is_entry(207), "the game's colours (01-10, EX, DX) are no entries");
        check(worn_colour(rw) == 1 && worn_colour(4) == 4 && worn_colour(201) == 201, "an entry is worn on colour 02, a game colour as it is");
        // a round among the game's colours and the entries: each once, never twice in a row
        Fighter f;
        Rng rng = seeded(21);
        const std::vector<int> cs = { 0, 1, 2, rw, mc };
        std::set<int> got;
        int prev = -1;
        bool back_to_back = false;
        for (int i = 0; i < 5; ++i) {
            const int v = draw_colour(f, 3, cs, nullptr, rng);
            got.insert(v);
            back_to_back |= v == prev;
            prev = v;
        }
        check(got.size() == 5 && !back_to_back, "a round of 5 colours (3 of the game's, 2 entries): each once");
        check(f.colours.count(3) && f.colours[3].drawn.size() == 5, "the round is that outfit's (shared with the Random outfit's draws of it)");
        const int v = draw_colour(f, 3, cs, [&](int c) { return worn_colour(c) != 1; }, rng);
        check(v == 0 || v == 2, "a mirror on colour 02: neither 02 nor an entry (they ride on it)");
        check(draw_colour(f, 4, {}, nullptr, rng) == 0, "nothing known: colour 0");
        // the Random outfit's colour: the entries too, the mirror filter by outfit
        Fighter g;
        const Draw d = draw_outfit(g, { 5 }, nullptr, [&](int) { return std::vector<int>{ 1, rw }; }, rng,
                                   [](int cos, int c) { return cos != 5 || worn_colour(c) != 1; });
        check(d.costume == 5 && d.color == 0, "every colour of the outfit drawn ridden on 02 by a mirror: colour 0");
    }
    {   // refused only for now (a mirror's colour, an NSFW outfit hidden): still given in this round, never back before it ends
        bool ok = true;
        for (uint32_t seed = 1; seed <= 200; ++seed) {
            Rng rng = seeded(seed);
            Pool p;
            p.drawn = { 1 };
            p.last = 1;
            const int v = draw(p, { 1, 2, 3 }, [](int c) { return c != 1; }, rng);   // 1 refused now
            ok &= (v == 2 || v == 3) && has(p.drawn, 1);
            const int w = draw(p, { 1, 2, 3 }, nullptr, rng);                         // everything allowed again
            ok &= w != 1 && w != v && (w == 2 || w == 3);
            const int x = draw(p, { 1, 2, 3 }, nullptr, rng);                         // the round is over: a new one
            ok &= x != w && p.drawn.size() == 1;
        }
        check(ok, "a value refused only for now stays given: the round's other values come first (200 seeds)");
        Pool q;
        q.drawn = { 0, 7 };
        q.last = 7;
        Rng rng = seeded(9);
        const int v = draw(q, { 0, 2, 7 }, [](int c) { return c == 2; }, rng);
        check(v == 2 && has(q.drawn, 0) && has(q.drawn, 7), "the values refused now keep their place in the round");
        check(draw(q, { 0, 2, 7 }, [](int c) { return c != 2; }, rng) != 2 && q.drawn.size() == 1,
              "every value allowed now was given: a new round, the refused one not in it");
    }
    {   // a draw on a copy of the rounds (a select screen's landing), given in the rounds later: as if drawn there
        bool same = true, chain = true;
        auto colours = [](int) { return std::vector<int>{ 0, 1, 2 }; };
        for (uint32_t seed = 1; seed <= 300; ++seed) {
            Rng rng = seeded(seed);
            Fighter a;
            for (uint32_t k = 0; k < seed % 7; ++k) draw_outfit(a, outfits, nullptr, colours, rng);   // some rounds under way
            Fighter copy = a;
            const Draw d = draw_outfit(copy, outfits, nullptr, colours, rng);
            Fighter given = a;
            give_draw(given, d);
            same &= given.outfits.drawn == copy.outfits.drawn && given.outfits.last == copy.outfits.last
                    && given.colours[d.costume].drawn == copy.colours[d.costume].drawn;
            Fighter second = a;                                   // a second player on the same fighter: drawn on the first one's
            give_draw(second, d);
            const Draw e = draw_outfit(second, outfits, nullptr, colours, rng);
            chain &= e.costume != d.costume;
        }
        check(same, "a draw on a copy, given in the rounds afterwards: the rounds as if drawn there (300 seeds)");
        check(chain, "a second player's draw on the first one's copy: never the same outfit (300 seeds)");
        Pool p;
        p.drawn = { 0 };
        p.last = 0;
        give(p, 2);
        check(p.drawn == std::vector<int>({ 0, 2 }) && p.last == 2, "give: the value joins the round");
        give(p, 0);
        check(p.drawn == std::vector<int>({ 0 }) && p.last == 0, "give: a value the round gave already starts a new round");
        Fighter none;
        give_draw(none, Draw{});
        check(none.outfits.drawn.empty() && none.colours.empty(), "give_draw of no draw: nothing");
    }
    {   // the pool: the game's outfits listed that the costume table has, then the loader's the player sees
        const std::vector<int> listed = { 0, 1, 2, 4, 5, 16, 17, 104, 5, 2 };
        auto slot = [](int c) { return c >= 5 && c <= 12; };
        auto table = [](int c) { return c < 5; };
        Listed l = split_listed(listed, 104, slot, table);
        check(l.game == std::vector<int>({ 0, 1, 2, 4 }) && l.unknown == std::vector<int>({ 16, 17 }),
              "a listed number neither the loader's nor the table's: never in the pool");
        l = split_listed(listed, 104, slot, nullptr);
        check(l.game == std::vector<int>({ 0, 1, 2, 4, 16, 17 }) && l.unknown.empty(), "the table not known yet: every listed number is the game's");
        const std::vector<int> game = { 0, 4 };
        check(pool_of(&game, { 5, 7, 4 }) == std::vector<int>({ 0, 4, 5, 7 }), "the pool: the game's listed, then the loader's shown");
        check(pool_of(nullptr, { 5 }) == std::vector<int>({ 0, 5 }), "the row never read: Outfit 1 and the loader's");
        check(pool_of(nullptr, {}) == std::vector<int>({ 0 }), "nothing known: Outfit 1 alone");
    }
    {   // an outfit's colours: the game's table (owned or not) and its Color row, each once, no entry
        const int mc = entry_value("mc");
        check(outfit_colours({ 0, 1, 2, 9 }, { 0, 1, 200, mc }) == std::vector<int>({ 0, 1, 2, 9, 200 }),
              "the table's colours, then the row's others (an EX colour), never an entry");
        check(outfit_colours({}, { 3, 0 }) == std::vector<int>({ 3, 0 }), "no table: the row's");
        check(outfit_colours({ 5, -1 }, {}) == std::vector<int>({ 5 }), "no row: the table's");
        check(outfit_colours({}, {}).empty(), "nothing known: none (the caller's Color 1)");
    }
    {   // state.json's numbers nobody knows any more: out of the rounds and the next draws
        Fighter f;
        f.outfits = { { 0, 16, 5, 17 }, 17 };
        f.colours[0] = { { 1 }, 1 };
        f.colours[17] = { { 0 }, 0 };
        f.next = { 17, 0 };
        f.next_colour = { 16, 1 };
        const int n = forget(f, [](int c) { return c < 16; });
        check(n == 6 && f.outfits.drawn == std::vector<int>({ 0, 5 }) && f.outfits.last == -1 && f.colours.size() == 1
              && f.colours.count(0) && !f.next.valid() && !f.next_colour.valid(), "forget: every value of an unknown outfit goes (6)");
        Fighter g;
        g.outfits = { { 0, 5 }, 5 };
        g.next = { 5, 3 };
        check(forget(g, [](int c) { return c < 16; }) == 0 && g.next == Draw({ 5, 3 }) && g.outfits.last == 5, "forget: known ones stay");
    }
    {   // online: the set and the save
        const Draw game{ 2, 4 }, ours{ 12, 1 };
        check(!save_holds(game, true, false).valid(), "Outfit 3 drawn: the alias wears it, the save holds the carrier (1.49)");
        check(save_holds(Draw{ 4, 6 }, false, true) == Draw({ 4, 6 }), "DriveTech drawn: the save holds it (the profile sends it)");
        check(!save_holds(Draw{ 17, 0 }, false, false).valid(), "a number nobody knows drawn: the save holds the carrier, never it");
        const int rw = entry_value("rw");
        check(save_holds(Draw{ 0, rw }, false) == Draw({ 0, 1 }), "Outfit 1 and an entry drawn: the save holds Outfit 1 on 02");
        Bind e = bind_set(Draw{ 2, rw }, false, 2, 1);
        check(e.consumed && !e.local && e.set == Draw({ 2, rw }), "the match holds that outfit on 02: the entry is worn (on this PC)");
        e = bind_set(Draw{ 2, rw }, false, 2, 5);
        check(e.consumed && e.set == Draw({ 2, 5 }), "the match holds another colour: that colour (what the opponent sees)");
        e = bind_set(Draw{ 12, rw }, true, 0, 3);
        check(e.consumed && e.local && e.set == Draw({ 12, rw }), "a Slots Loader outfit and an entry: the alias wears both");
        // Battle Settings' Random colour of a game outfit
        Bind c = bind_colour(Draw{ 3, 6 }, 3, 6);
        check(c.consumed && c.set == Draw({ 3, 6 }), "Random colour: the match holds the colour drawn, the set wears it");
        c = bind_colour(Draw{ 3, rw }, 3, 1);
        check(c.consumed && c.set == Draw({ 3, rw }), "Random colour, an entry: the match holds 02, the entry is worn");
        c = bind_colour(Draw{ 3, 6 }, 3, 2);
        check(!c.consumed && c.set == Draw({ 3, 2 }), "no profile since the draw (another colour): the set wears it, the draw waits");
        c = bind_colour(Draw{ 3, 6 }, 4, 6);
        check(!c.consumed && c.set == Draw({ 4, 6 }), "another outfit in the match: what it holds, the draw waits");
        check(!bind_colour(Draw{}, 3, 6).consumed, "no Random colour: nothing taken");
        check(!save_holds(ours, true).valid(), "a Slots Loader outfit drawn: the save holds the carrier");
        Bind b = bind_set(ours, true, 0, 3);
        check(b.consumed && b.local && b.set == ours, "Slots Loader outfit: worn on this PC over what the match holds, the draw taken");
        b = bind_set(game, false, 2, 4);
        check(b.consumed && !b.local && b.set == Draw({ 2, 4 }), "game outfit the match holds: the game wears it, the draw taken");
        b = bind_set(game, false, 2, 6);
        check(b.consumed && !b.local && b.set == Draw({ 2, 4 }), "game outfit, Color 5 drawn: kept, the match holds Color 1 / 2 only (21/11)");
        b = bind_set(game, false, 0, 1);
        check(b.consumed && b.local && b.set == game, "game outfit the match does not hold (no profile since): worn on this PC, the draw taken");
        b = bind_set(Draw{ 0, 2 }, false, 4, 1);
        check(!b.consumed && !b.local && b.set == Draw({ 4, 1 }), "Outfit 1 drawn, the match holds another: worn as the match holds, the draw waits");
        b = bind_set(Draw{ 0, 2 }, false, 0, 5);
        check(b.consumed && !b.local && b.set == Draw({ 0, 2 }), "Outfit 1 drawn and held, Color 3 drawn: kept (worn on this PC over Color 1 / 2)");
        b = bind_set(Draw{ 4, 6 }, false, 4, 0);
        check(b.consumed && !b.local && b.set == Draw({ 4, 6 }), "DriveTech in Color 7 drawn, the match holds DriveTech Color 1: Color 7 kept (worn on this PC)");
        b = bind_set(Draw{ 0, 1 }, false, 0, 0);
        check(b.consumed && b.set == Draw({ 0, 0 }), "Outfit 1 in Color 2 drawn, the match holds Color 1: the match's (a profile came between)");
        b = bind_set(Draw{}, false, 3, 0);
        check(!b.consumed && !b.local && b.set == Draw({ 3, 0 }), "no draw yet: what the match holds");
    }
    {   // state.json "random": written, read back; unreadable parts left out
        std::map<int, Fighter> all;
        all[1].outfits = { { 7, 0 }, 0 };
        all[1].next = { 7, 3 };
        all[1].colours[7] = { { 3 }, 3 };
        all[1].colours[0] = { {}, 5 };
        all[2];                                              // nothing to keep: left out
        all[5].next_colour = { 2, entry_value("mc1") };      // a Random colour alone (no Random outfit draw)
        const std::string text = "{ \"random\": " + state_json(all, 4) + " }";
        Json j;
        const bool parsed = json_parse(text, j);
        check(parsed, "state_json is valid JSON");
        auto back = parse_state(j.get("random"));
        check(back.size() == 2 && back.count(1) && back.count(5), "an empty character is not written");
        check(back[5].next_colour == Draw({ 2, entry_value("mc1") }) && !back[5].next.valid() && !back[1].next_colour.valid(),
              "the Random colour's next one read back (an entry's number whole)");
        const Fighter& f = back[1];
        check(f.outfits.drawn == std::vector<int>({ 7, 0 }) && f.outfits.last == 0, "the outfit round read back");
        check(f.next == Draw({ 7, 3 }), "the next draw read back");
        check(f.colours.size() == 2 && f.colours.at(7).drawn == std::vector<int>({ 3 }) && f.colours.at(0).last == 5,
              "the colour rounds read back (outfit 0 included)");
        Json bad;
        json_parse("{ \"random\": { \"x\": 1, \"3\": { \"next\": [1], \"outfits\": 4 }, \"4\": [] } }", bad);
        auto b2 = parse_state(bad.get("random"));
        check(b2.size() == 1 && b2.count(3) && !b2[3].next.valid() && b2[3].outfits.drawn.empty(), "unreadable parts left out");
        check(parse_state(nullptr).empty(), "no \"random\": nothing");
        check(state_json({}, 4) == "{}", "nothing to keep: {}");
    }

    // ---- the player's own list (random_list.json) ----
    auto all_colours = [](int) { return std::vector<int>{ 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }; };
    auto make = [](bool shuffle, std::vector<ListItem> items) { List l; l.shuffle = shuffle; l.items = std::move(items); l.stamp = list_stamp(l); return l; };
    {   // in order: each outfit its repeats in a row, then from the top again
        const List l = make(false, { { 14, 2, {} }, { 0, 1, {} }, { 15, 3, {} } });
        Fighter f;
        Rng rng = seeded(3);
        std::vector<int> got;
        for (int i = 0; i < 12; ++i) got.push_back(draw_listed(f, l, nullptr, all_colours, rng).costume);
        check(got == std::vector<int>({ 14, 14, 0, 15, 15, 15, 14, 14, 0, 15, 15, 15 }), "list in order: 14 x2, 0, 15 x3, then again");
        check(list_places(l) == 6 && list_item_at(l, 0) == 0 && list_item_at(l, 2) == 1 && list_item_at(l, 5) == 2 && list_item_at(l, 6) == -1,
              "places: each item its repeats");
    }
    {   // in order: an outfit that may not be worn now is passed over; nothing can be: no draw (the pool's then)
        const List l = make(false, { { 14, 1, {} }, { 20, 2, {} }, { 15, 1, {} } });
        Fighter f;
        Rng rng = seeded(4);
        std::vector<int> got;
        for (int i = 0; i < 4; ++i) got.push_back(draw_listed(f, l, [](int c) { return c != 20; }, all_colours, rng).costume);
        check(got == std::vector<int>({ 14, 15, 14, 15 }), "list in order: a hidden outfit passed over");
        check(!draw_listed(f, l, [](int) { return false; }, all_colours, rng).valid(), "nothing in the list allowed: no draw");
    }
    {   // shuffled: a round gives every card once (an outfit its repeats), never the last card first in the next round
        const List l = make(true, { { 14, 3, {} }, { 0, 1, {} }, { 15, 2, {} } });
        bool rounds_ok = true;
        for (uint32_t seed = 1; seed <= 200 && rounds_ok; ++seed) {
            Fighter f;
            Rng rng = seeded(seed);
            for (int round = 0; round < 4; ++round) {
                std::map<int, int> count;
                for (int i = 0; i < 6; ++i) ++count[draw_listed(f, l, nullptr, all_colours, rng).costume];
                if (count[14] != 3 || count[0] != 1 || count[15] != 2) rounds_ok = false;
            }
        }
        check(rounds_ok, "list shuffled: each round of 6 gives 14 x3, 0 x1, 15 x2 (200 seeds, 4 rounds)");
    }
    {   // colours: among the item's own; a mirror refusing them all: any colour; colours unknown to the outfit: any
        const List l = make(false, { { 14, 1, { 2, 5 } } });
        Fighter f;
        Rng rng = seeded(9);
        std::set<int> seen;
        for (int i = 0; i < 40; ++i) seen.insert(draw_listed(f, l, nullptr, all_colours, rng).color);
        check(seen == std::set<int>({ 2, 5 }), "an item's colours: only 2 and 5 drawn");
        Fighter g;
        const Draw m = draw_listed(g, l, nullptr, all_colours, rng, [](int, int c) { return c != 2 && c != 5; });
        check(m.valid() && m.color != 2 && m.color != 5, "the mirror takes both its colours: another colour");
        const List odd = make(false, { { 14, 1, { 7 } } });
        Fighter h;
        const Draw o = draw_listed(h, odd, nullptr, [](int) { return std::vector<int>{ 0, 1 }; }, rng);
        check(o.valid() && (o.color == 0 || o.color == 1), "a colour the outfit does not have: one it has");
    }
    {   // a draw made on a copy (a select screen), given in the rounds later: the list goes on from it
        const List l = make(false, { { 14, 1, {} }, { 15, 1, {} }, { 16, 1, {} } });
        Fighter real;
        Rng rng = seeded(5);
        draw_listed(real, l, nullptr, all_colours, rng);                 // 14
        Fighter copy = real;
        const Draw d = draw_listed(copy, l, nullptr, all_colours, rng);  // 15, on the copy
        check(real.list.last == 0 && d.pos == 1 && d.stamp == l.stamp, "the copy's draw leaves the rounds as they were");
        give_draw(real, d);
        check(draw_listed(real, l, nullptr, all_colours, rng).costume == 16 && real.outfits.drawn.empty(), "given: the list goes on (16), the pool's round untouched");
        Fighter fresh;   // never drawn from this list (its stamp empty): the copy's draw starts it
        give_draw(fresh, d);
        check(fresh.list_stamp == l.stamp && fresh.list.last == 1, "a draw given in rounds without the list: they take its stamp");
    }
    {   // another list starts from its top; state.json keeps the place and the stamp
        const List a = make(false, { { 14, 1, {} }, { 15, 1, {} } });
        const List b = make(false, { { 15, 1, {} }, { 14, 1, {} } });
        Fighter f;
        Rng rng = seeded(6);
        draw_listed(f, a, nullptr, all_colours, rng);
        check(draw_listed(f, b, nullptr, all_colours, rng).costume == 15 && a.stamp != b.stamp, "a changed list starts again from its top");
        std::map<int, Fighter> all{ { 4, f } };
        const std::string js = state_json(all, 4);
        Json j;
        const bool parsed = json_parse(js, j);
        const auto back = parse_state(&j);
        check(parsed && back.count(4) && back.at(4).list_stamp == b.stamp && back.at(4).list.last == 0 && back.at(4).list.drawn == std::vector<int>({ 0 }),
              "state.json: the list's place and stamp read back");
        Fighter g = back.at(4);
        check(draw_listed(g, b, nullptr, all_colours, rng).costume == 14, "read back: the list goes on after a restart");
    }
    {   // random_list.json: mods by their mod id, the game's outfits by number, gone mods left out, bad entries skipped
        const std::string text = R"({ "format": "sf6-random-list-1", "fighters": {
            "4": { "mode": "shuffle", "outfits": [ { "mod_id": "aaa", "costume": 14, "base": 1, "repeat": 3, "colours": [2, 5, 2, 12, 64] },
                                                  { "game": 0 }, { "mod_id": "gone", "costume": 9 }, { "repeat": 2 } ] },
            "16": { "mode": "order", "outfits": [ { "mod_id": "gone" } ] },
            "1": { "outfits": [ { "game": 2, "repeat": 99 } ] } } })";
        int asked_base = -2;
        const auto lists = parse_lists(text, [&](int fid, const std::string& mod, int cos, int base) {
            if (fid == 4 && mod == "aaa") { asked_base = base; return cos + 3; }   // the loader numbered it again: now 17
            return -1;
        });
        const bool four = lists.count(4) && lists.at(4).shuffle && lists.at(4).items.size() == 2;
        check(four && lists.at(4).items[0].costume == 17 && lists.at(4).items[0].repeat == 3 && lists.at(4).items[0].colours == std::vector<int>({ 2, 5, 12 })
              && asked_base == 1 && lists.at(4).items[1].costume == 0 && lists.at(4).items[1].repeat == 1,
              "list read: the mod's number now, its repeats, colours 0-63 once (12 kept, 64 not); Outfit 1; the gone mod and a bare entry left out");
        check(!lists.count(16), "a list holding only gone mods: no list (the pool)");
        check(lists.count(1) && !lists.at(1).shuffle && lists.at(1).items[0].repeat == kMaxRepeat, "no mode: in order; repeat capped");
        check(parse_lists(R"({ "format": "other", "fighters": { "4": { "outfits": [ { "game": 0 } ] } } })", nullptr).empty()
              && parse_lists("not json", nullptr).empty(), "another format or no JSON: no lists");
    }

    std::printf(fails ? "%d FAILED\n" : "all ok\n", fails);
    return fails ? 1 : 0;
}
