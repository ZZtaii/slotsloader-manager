// Unit test of plugin/src/replay_outfits.hpp: the replay's key, the registry's mods, replay_outfits.json, the journal and
// what each side wears Off, on My Own and Forced.
// Build and run: build_replay_outfits_test.bat (prints "all ok" and exits 0 when every check passes).
#define _CRT_SECURE_NO_WARNINGS
#include "../src/replay_outfits.hpp"

#include <cstdio>

using namespace slots::replay;

static int fails = 0;
static void check(bool ok, const char* what) {
    std::printf("  %-80s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++fails;
}

int main() {
    // ---- the key ----
    ReplayFacts a;
    a.seed = 290329851; a.stage = 0; a.fighters = { 1, 31 }; a.round_seeds = { 60748, 21618, 27696, 0, 0 };
    const std::string ka = replay_key(a);
    check(ka.size() == 16 && ka.find_first_not_of("0123456789abcdef") == std::string::npos, "16 hex digits");
    check(replay_key(a) == ka, "the same facts: the same key");
    ReplayFacts b = a; b.seed ^= 1;
    check(replay_key(b) != ka, "another match seed: another key");
    b = a; b.round_seeds[1] = 21619;
    check(replay_key(b) != ka, "another round seed: another key");
    b = a; b.fighters = { 31, 1 };
    check(replay_key(b) != ka, "the sides swapped: another key");
    b = a; b.stage = 3;
    check(replay_key(b) == ka, "another stage: the same key (the match's own replay can hold stage 0, 22/11)");
    check(replay_key(b, true) != replay_key(a, true) && replay_key(a, true) != ka, "the key of before 22/11 held the stage");
    b = a; b.round_seeds = { 60748, 21618, 27696 };
    check(replay_key(b) == ka, "3 rounds played, the server's copy pads to 5 with zeros: the same key");
    b = a; b.round_seeds = { 60748, 21618, 27696, 0, 0, 0, 0 };
    check(replay_key(b) == ka, "more zero padding: the same key");
    b = a; b.round_seeds = { 60748, 21618, 27696, 5 };
    check(replay_key(b) != ka, "one round more: another key");
    b = a; b.round_seeds = { 60748, 0, 27696 };
    check(replay_key(b) != ka, "a zero seed inside the rounds counts");
    b = a; b.round_seeds = { 0, 0, 0, 0, 0 };
    check(replay_key(b).empty(), "every round seed zero: no key");
    b = a; b.round_seeds = { 60748 | 0x10000u, 21618, 27696, 0, 0 };
    check(replay_key(b) == ka, "a round seed is 16 bits: higher bits ignored");
    b = a; b.round_seeds.clear();
    check(replay_key(b).empty(), "no round: no key");
    b = a; b.fighters.clear();
    check(replay_key(b).empty(), "no fighter: no key");

    // ---- the registry ----
    const std::string reg_text = R"({"version":1,"slots":[
        {"mod_id":"aaaa","fighter":1,"original_costume_no":0,"new_costume_no":5},
        {"mod_id":"bbbb","fighter":1,"original_costume_no":0,"new_costume_no":6},
        {"mod_id":"cccc","fighter":32,"original_costume_no":0,"new_costume_no":5},
        {"mod_id":"cccc","fighter":32,"original_costume_no":1,"new_costume_no":6},
        {"mod_id":"cccc","fighter":32,"original_costume_no":2,"new_costume_no":7},
        {"mod_id":"","fighter":2,"original_costume_no":0,"new_costume_no":5},
        {"fighter":2,"original_costume_no":0,"new_costume_no":6}],"possession":[]})";
    const auto reg = parse_registry(reg_text);
    check(reg.size() == 5, "registry: 5 slots with a mod (no mod_id / an empty one left out)");
    check(parse_registry("not json").empty(), "an unreadable registry: no slot");
    check(resolve_mod(reg, 1, "bbbb", 6) == 6, "mod -> its slot");
    check(resolve_mod(reg, 1, "bbbb", 9) == 6, "the slot numbered again: found by its mod");
    check(resolve_mod(reg, 1, "zzzz", 6) == -1, "the mod is gone: -1");
    check(resolve_mod(reg, 2, "bbbb", 6) == -1, "the mod is another character's: -1");
    check(resolve_mod(reg, 32, "cccc", 9, 1) == 6, "a mod with 3 slots: the one made from the same outfit");
    check(resolve_mod(reg, 32, "cccc", 7) == 7, "a mod with 3 slots, no base: the one with the same number");
    check(resolve_mod(reg, 32, "cccc", 9) == 5, "a mod with 3 slots, no base, number gone: the first");
    check(resolve_mod(reg, 32, "cccc", 7, 4) == 7, "a mod with 3 slots, base not among them: the same number");
    check(slot_of(reg, 32, 6) && slot_of(reg, 32, 6)->mod_id == "cccc" && slot_of(reg, 32, 6)->base == 1, "slot_of: its mod and base");
    check(slot_of(reg, 1, 0) == nullptr, "slot_of a game outfit: none");
    {
        std::map<int, int> rnd;
        const auto r2 = parse_registry(R"({"slots":[{"mod_id":"aaaa","fighter":1,"new_costume_no":5}],"possession":[
            {"fighter":1,"costume_no":5,"name":"Aloha"},{"fighter":1,"costume_no":104,"name":"Random","random":true},
            {"fighter":18,"costume_no":104,"random":true},{"fighter":2,"costume_no":7,"random":false}]})", &rnd);
        check(r2.size() == 1 && rnd.size() == 2 && rnd[1] == 104 && rnd[18] == 104, "registry: each character's Random outfit (possession)");
        check(is_random_outfit(rnd, 1, 104) && !is_random_outfit(rnd, 1, 5) && !is_random_outfit(rnd, 2, 7) && !is_random_outfit(rnd, 3, 104),
              "is_random_outfit: that character's Random outfit only");
        parse_registry("not json", &rnd);
        check(rnd.empty(), "an unreadable registry: no Random outfit");
    }

    // ---- replay_outfits.json ----
    std::map<int, Forced> forced;
    check(parse_outfits("\xEF\xBB\xBF{\"version\":1,\"fighters\":{"
                        "\"1\":{\"mode\":\"forced\",\"costume\":6,\"mod_id\":\"bbbb\",\"color\":2},"
                        "\"31\":{\"mode\":\"forced\",\"costume\":3,\"color\":7},"
                        "\"2\":{\"mode\":\"default\",\"costume\":1,\"color\":1},"
                        "\"5\":{\"mode\":\"forced\"},"
                        "\"x\":{\"mode\":\"forced\",\"costume\":1}}}", forced),
          "replay_outfits.json read (with a BOM)");
    check(forced.size() == 2 && forced.count(1) && forced.count(31), "forced: F1 and F31 (default, no costume, bad id left out)");
    {
        std::map<int, Forced> ow;
        check(parse_outfits("{\"version\":1,\"fighters\":{\"1\":{\"mode\":\"own\"},\"2\":{\"mode\":\"forced\",\"costume\":1,\"color\":0}}}", ow) &&
              ow.size() == 2 && ow[1].own && !ow[2].own, "My Own read (mode own, no costume)");
    }
    check(forced[1].mod_id == "bbbb" && forced[1].costume == 6 && forced[1].color == 2, "F1: an extra outfit by its mod");
    check(forced[31].mod_id.empty() && forced[31].costume == 3 && forced[31].color == 7, "F31: a game outfit");
    std::map<int, Forced> kept = forced;
    check(!parse_outfits("{\"version\":1,\"fighters\":{", kept) && kept.size() == 2, "a half-written file: refused, the last choices kept");
    std::map<int, Forced> none;
    check(parse_outfits("{\"version\":1}", none) && none.empty(), "no fighters: nothing forced");
    {
        BgmChoice bg; bg.mode = BgmMode::Stage;
        check(parse_outfits("{\"version\":1}", none, nullptr, &bg) && bg.mode == BgmMode::Random, "music: no key, Random");
        check(parse_outfits("{\"version\":1,\"bgm\":{\"mode\":\"opponent\"}}", none, nullptr, &bg) && bg.mode == BgmMode::Opponent && bgm_set(bg) == 2,
              "music: read for the replay, with no fighters too");
    }
    std::map<int, Forced> cmf;
    check(parse_outfits("{\"version\":1,\"fighters\":{\"1\":{\"mode\":\"forced\",\"costume\":8,\"mod_id\":\"8cd9\",\"color\":4,"
                        "\"colormod\":\"..\\\\color_mods\\\\Ryu\\\\MANUALCOLORS_01\\\\esf001_009_cmd_001.user.2\"}}}", cmf) &&
          cmf[1].colormod == "..\\color_mods\\Ryu\\MANUALCOLORS_01\\esf001_009_cmd_001.user.2" && cmf[1].color == 1,
          "a Color Mod entry is read (its id; worn on colour 1, its carrier)");
    {   // a Random colour: read, carried to the plan (the playback draws it); older files: none
        std::map<int, Forced> rf;
        check(parse_outfits("{\"version\":1,\"fighters\":{\"1\":{\"mode\":\"forced\",\"costume\":2,\"color\":0,\"color_random\":true},"
                            "\"2\":{\"mode\":\"forced\",\"costume\":1,\"color\":4}}}", rf) &&
              rf[1].random_color && rf[1].color == 0 && !rf[2].random_color && rf[2].color == 4, "color_random read, absent: false");
        const auto w = plan({ Side{ 1, 0, 3 }, Side{ 2, 1, 4 } }, nullptr, rf, {});
        check(w[0].forced && w[0].random_color && w[0].costume == 2 && !w[1].random_color, "the plan says which side's colour is Random");
    }
    int stage = 7;
    {
        std::map<int, Forced> fo;
        check(parse_outfits("{\"version\":1,\"fighters\":{\"26\":{\"mode\":\"forced\",\"costume\":0,\"color\":0,"
                            "\"options\":{\"esf026/game outfit 1\":[\"wesker01\"],\"esf026/x\":[]}},"
                            "\"30\":{\"mode\":\"off\",\"options\":{\"esf030/game outfit 1\":[\"body0001\"]}}}}", fo) &&
              fo.size() == 1 && fo.count(26), "a fighter set to Off is not forced, whatever options it keeps");
        check(fo[26].options.size() == 2 && fo[26].options["esf026/game outfit 1"] == std::vector<std::string>{ "wesker01" },
              "a forced fighter's costume options are read, per outfit");
        std::map<int, Forced> fn;
        check(parse_outfits("{\"version\":1,\"fighters\":{\"2\":{\"mode\":\"forced\",\"costume\":1,\"color\":0}}}", fn) && fn[2].options.empty(),
              "a file from before them: no options");
    }
    check(parse_outfits("{\"version\":1,\"fighters\":{},\"stage\":{\"mode\":\"forced\",\"id\":40000}}", none, &stage) && stage == 40000,
          "a forced stage is read");
    check(parse_outfits("{\"version\":1,\"fighters\":{}}", none, &stage) && stage == -1, "no stage entry: the replay's own stage");
    check(parse_outfits("{\"version\":1,\"stage\":{\"mode\":\"auto\",\"id\":3}}", none, &stage) && stage == -1,
          "a stage entry that is not forced: the replay's own stage");

    // ---- the journal ----
    Entry e;
    e.key = ka; e.time = 1793862214; e.mode = 14; e.me = 0;
    e.sides = { { 1, 0, 1 }, { 1, 2, 6 } };
    e.has_slot = true; e.mod_id = "aaaa"; e.slot_costume = 5; e.slot_color = 3; e.slot_base = 0;
    const std::string line = journal_line(e);
    check(line.back() == '\n' && line.find('\n') == line.size() - 1, "one line");
    Entry e2 = e;
    e2.key = "00000000000000ff"; e2.me = 1; e2.has_slot = false;
    std::map<std::string, Entry> j;
    const size_t n = parse_journal(line + "{\"v\":1,\"key\":\"broken\n" + journal_line(e2) + "\n", j);
    check(n == 2 && j.size() == 2, "two lines read, a broken one skipped");
    const Entry& r = j[ka];
    check(r.me == 0 && r.mode == 14 && r.time == 1793862214 && r.sides.size() == 2 && r.sides[1].costume == 2 && r.sides[1].color == 6,
          "a line read back: side, mode, time, both sides");
    check(r.has_slot && r.mod_id == "aaaa" && r.slot_costume == 5 && r.slot_color == 3 && r.slot_base == 0, "a line read back: the slot worn");
    check(!j["00000000000000ff"].has_slot && j["00000000000000ff"].me == 1, "\"slot\": null read back");
    Entry e3 = e; e3.slot_color = 4;
    std::map<std::string, Entry> j2;
    parse_journal(line + journal_line(e3), j2);
    check(j2.size() == 1 && j2[ka].slot_color == 4, "the same key twice: the later line wins");
    check(line.find("short") == std::string::npos && line.find("name") == std::string::npos, "numbers only: no name, no player id");
    check(!j[ka].has_facts, "a line without facts (before 22/11): none read");
    check(!j[ka].has_cm, "a line without a Color Mod entry (before 22/11): not known");
    {   // the Color Mod entry worn (22/11): written, read back, asked for by My Own on colour 02 only
        const std::map<int, Forced> own1 = { { 1, Forced{ -1, 0, "", "", {}, false, true } } };
        const std::vector<Side> rec = { { 1, 0, 1 }, { 1, 0, 1 } };
        Entry ec = e; ec.sides = rec; ec.slot_color = 1; ec.has_cm = true; ec.colormod = "..\\color_mods\\Ryu\\MC_01\\x.user.2";
        std::map<std::string, Entry> jc;
        parse_journal(journal_line(ec), jc);
        check(jc[ka].has_cm && jc[ka].colormod == ec.colormod, "the Color Mod entry worn: written and read back");
        auto wc = plan(rec, &jc[ka], own1, reg);
        check(wc[0].change && wc[0].entry && wc[0].colormod == ec.colormod && !wc[0].forced && !wc[1].entry, "My Own: the entry worn is asked for, the player's side only");
        Entry en = ec; en.colormod.clear();
        parse_journal(journal_line(en), jc);
        wc = plan(rec, &jc[ka], own1, reg);
        check(jc[ka].has_cm && wc[0].entry && wc[0].colormod.empty(), "My Own: no entry worn on colour 02: the plain colour asked for");
        Entry e3c = ec; e3c.slot_color = 3;
        wc = plan(rec, &e3c, own1, reg);
        check(!wc[0].entry, "My Own on another colour: nothing asked of the Color Mod");
        Entry old = ec; old.has_cm = false;
        wc = plan(rec, &old, own1, reg);
        check(!wc[0].entry, "My Own, an older line: the entry is not known, nothing asked");
        Entry same = ec; same.game = true; same.mod_id.clear(); same.slot_costume = 0; same.slot_color = 1;   // Outfit 1, colour 02, an MC entry
        wc = plan(rec, &same, own1, reg);
        check(!wc[0].change && wc[0].entry && wc[0].colormod == ec.colormod, "My Own: Outfit 1 on colour 02 with an entry: nothing written, the entry asked for");
    }
    {   // the key's facts (22/11): written, read back, compared in the log
        Entry ef = e; ef.has_facts = true;
        ef.facts.seed = 77; ef.facts.stage = 40000; ef.facts.fighters = { 1, 1 }; ef.facts.round_seeds = { 1234, 567, 0, 0, 0 };
        std::map<std::string, Entry> jf;
        parse_journal(journal_line(ef), jf);
        const std::string kf = replay_key(ef.facts);
        check(jf.size() == 1 && jf.count(kf) == 1 && kf != ka, "a line with its facts is known by the key of now, not the one written");
        check(jf[kf].has_facts && jf[kf].facts.seed == 77 && jf[kf].facts.stage == 40000 && jf[kf].facts.round_seeds.size() == 5 &&
              jf[kf].facts.fighters == std::vector<int>({ 1, 1 }), "facts written and read back");
        ReplayFacts served = ef.facts; served.stage = 20000;   // the server's copy: another stage, the rounds padded
        check(jf.count(replay_key(served)) == 1, "the replay served later (another stage) finds the match's line");
        ReplayFacts other = ef.facts; other.seed = 78; other.round_seeds = { 1234, 567 };
        const std::string c = closest_lines(other, jf, 3);
        check(c.find("1 sharing") != std::string::npos && c.find("seed differs") != std::string::npos && c.find("rounds same") != std::string::npos,
              "closest lines: the fact that differs is named");
        ReplayFacts far = ef.facts; far.seed = 1; far.round_seeds = { 9 };
        check(closest_lines(far, jf, 3).find("0 sharing") != std::string::npos, "closest lines: nothing shared, none named");
    }
    {   // a game outfit (or colour) worn by the alias over Outfit 1 / Color 1 (21/11)
        Entry g = e; g.mod_id.clear(); g.game = true; g.slot_costume = 2; g.slot_color = 6; g.slot_base = -1;
        std::map<std::string, Entry> jg;
        parse_journal(journal_line(g), jg);
        check(jg[ka].has_slot && jg[ka].game && jg[ka].slot_costume == 2 && jg[ka].slot_color == 6 && jg[ka].mod_id.empty(),
              "a game outfit worn by the alias: written and read back");
        const std::vector<Side> rec = { { 1, 0, 1 }, { 1, 0, 1 } };
        Entry gm = jg[ka]; gm.sides = rec;
        const std::map<int, Forced> own1 = { { 1, Forced{ -1, 0, "", "", {}, false, true } } };
        auto wg = plan(rec, &gm, own1, reg);
        check(wg[0].change && wg[0].costume == 2 && wg[0].color == 6 && !wg[1].change, "My Own: the player's side wears the game outfit of the journal");
    }

    // ---- what each side wears ----
    const std::map<int, Forced> no_forced;
    const std::map<int, Forced> own = { { 1, Forced{ -1, 0, "", "", {}, false, true } } };   // Ryu on My Own
    // a Ryu mirror: the player (P1) wore slot 5 (mod aaaa) over Outfit 1, the opponent was really in Outfit 1
    const std::vector<Side> mirror = { { 1, 0, 1 }, { 1, 0, 1 } };
    Entry m = e; m.sides = mirror;
    auto w = plan(mirror, &m, no_forced, reg);
    check(!w[0].change && !w[1].change, "Off: the game's data, the journal not used (22/11)");
    w = plan(mirror, &m, own, reg);
    check(w[0].change && w[0].costume == 5 && w[0].color == 3, "My Own: the player's side wears the slot of the journal");
    check(!w[1].change, "My Own: the opponent on the same carrier keeps what the replay holds");
    check(!w[0].forced, "My Own: not forced (the player's own Color Mod and options, as worn)");
    w = plan(mirror, nullptr, own, reg);
    check(!w[0].change && !w[1].change, "My Own, no journal line (an old replay): what it recorded");
    Entry mo = m; mo.mod_id = "zzzz";
    w = plan(mirror, &mo, own, reg);
    check(!w[0].change && !w[1].change, "My Own, the worn slot's mod gone: what it recorded");
    Entry mn = m; mn.has_slot = false;
    w = plan(mirror, &mn, own, reg);
    check(!w[0].change && !w[1].change, "My Own, the player wore no slot: what it recorded");
    Entry ms = m; ms.me = 1; ms.sides = { { 1, 0, 1 }, { 31, 0, 1 } };
    const std::vector<Side> other = { { 1, 0, 1 }, { 2, 0, 1 } };
    w = plan(other, &ms, own, reg);
    check(!w[0].change && !w[1].change, "My Own, the journal's side is another fighter: left alone");
    Entry mr = m; mr.slot_costume = 9;   // the slot was numbered 9 then, 5 now
    w = plan(mirror, &mr, own, reg);
    check(w[0].change && w[0].costume == 5, "My Own, the slot numbered again: its number now");
    const std::map<int, Forced> own_ken = { { 2, Forced{ -1, 0, "", "", {}, false, true } } };
    w = plan(mirror, &m, own_ken, reg);
    check(!w[0].change && !w[1].change, "My Own on another character: the player's Ryu as recorded");

    // Forced: both sides of that character, the player's own included; the other character keeps its own setting
    std::map<int, Forced> f1 = { { 1, Forced{ 6, 2, "bbbb" } } };
    w = plan(mirror, &m, f1, reg);
    check(w[0].change && w[0].costume == 6 && w[0].color == 2 && w[1].change && w[1].costume == 6 && w[1].color == 2,
          "Forced extra outfit: both sides of a mirror, the player's own too");
    const std::vector<Side> ryu_cammy = { { 1, 0, 1 }, { 6, 2, 0 } };
    Entry rc = m; rc.sides = ryu_cammy;
    std::map<int, Forced> f6 = { { 6, Forced{ 3, 7, "" } } };
    w = plan(ryu_cammy, &rc, f6, reg);
    check(!w[0].change && w[1].change && w[1].costume == 3 && w[1].color == 7,
          "Forced on the opponent's character, Off on the player's: recorded");
    std::map<int, Forced> f6own = f6;
    f6own[1] = Forced{ -1, 0, "", "", {}, false, true };
    w = plan(ryu_cammy, &rc, f6own, reg);
    check(w[0].change && w[0].costume == 5 && w[1].change && w[1].costume == 3 && w[1].color == 7,
          "Forced on the opponent's character, My Own (journal) on the player's");
    std::map<int, Forced> fgone = { { 1, Forced{ 6, 2, "zzzz" } } };
    w = plan(mirror, &m, fgone, reg);
    check(!w[0].change && !w[1].change, "Forced outfit's mod gone: what the replay recorded");
    std::map<int, Forced> fsame = { { 6, Forced{ 2, 0, "" } } };
    w = plan(ryu_cammy, nullptr, fsame, reg);
    check(!w[1].change, "Forced to what the replay holds already: nothing written");
    std::map<int, Forced> fcol = { { 6, Forced{ 2, 4, "" } } };
    w = plan(ryu_cammy, nullptr, fcol, reg);
    check(w[1].change && w[1].costume == 2 && w[1].color == 4, "Forced, same outfit, another colour: written");
    check(w[1].forced && w[1].colormod.empty() && !w[0].forced, "a forced side is marked (the Color Mod is asked: the game colour)");
    Forced fe{ 2, 1, "" };
    fe.colormod = "..\\color_mods\\Cammy\\MANUALCOLORS_01\\esf006_003_cmd_001.user.2";
    std::map<int, Forced> fcm = { { 6, fe } };
    w = plan(ryu_cammy, nullptr, fcm, reg);
    check(w[1].forced && w[1].colormod == fe.colormod && w[1].color == 1, "Forced Color Mod entry: colour 1 and the entry to ask for");

    // ---- Random at each playback ----
    {
        uint32_t s = 7;
        const std::function<uint32_t()> rng = [&] { s = s * 1103515245u + 12345u; return s >> 8; };
        auto o = draw_order({ 1, 6 }, rng);
        check(o.size() == 2 && o[0] == 0 && o[1] == 1, "draw order: two fighters, team order");
        int first1 = 0;
        for (int k = 0; k < 2000; ++k) {
            o = draw_order({ 1, 1 }, rng);
            if (o.size() != 2 || o[0] + o[1] != 1) { check(false, "draw order: a mirror's order holds both sides"); break; }
            first1 += o[0] == 1;
        }
        check(first1 > 800 && first1 < 1200, "draw order: a mirror's sides draw first about as often each (random at each playback)");
        check(draw_order({ 1, 1 }, nullptr) == std::vector<size_t>({ 0, 1 }) && draw_order({}, rng).empty(), "draw order: no source, or no side");
        o = draw_order({ 1, 6, 1 }, rng);
        check(o.size() == 3 && o[0] + o[1] + o[2] == 3 && o[0] != o[1] && o[1] != o[2] && o[0] != o[2], "draw order: three sides, each once");

        PlaybackDraws pd;
        check(!pd.begin(0x10, "k1", 1000, 5000), "playback draws: a first setup is a new playback");
        pd.keep(0, Drawn{ 1, 104, false, 8, 3, "" });
        check(pd.begin(0x10, "k1", 1100, 5000) && pd.find(0, 1, 104, false) && pd.find(0, 1, 104, false)->cos == 8,
              "the same flow map setting up the same replay at once: the same playback, its draw kept");
        check(!pd.find(0, 10, 104, false), "a draw is kept for its fighter only (every Random outfit is 104)");
        check(!pd.find(1, 1, 104, false) && !pd.find(0, 1, 104, true), "a draw is kept for its side and kind (outfit / colour) only");
        check(!pd.begin(0x10, "k2", 1200, 5000) && !pd.find(0, 1, 104, false), "another replay on the same flow map: a new playback");
        pd.keep(0, Drawn{ 1, 104, false, 8, 3, "" });
        check(!pd.begin(0x20, "k2", 1300, 5000) && !pd.find(0, 1, 104, false), "another flow map: a new playback");
        pd.keep(0, Drawn{ 1, 104, false, 8, 3, "" });
        check(!pd.begin(0x20, "k2", 7000, 5000) && !pd.find(0, 1, 104, false), "the same replay watched again (5 s on): a new playback");
        pd.keep(0, Drawn{ 1, 104, false, 8, 3, "" });
        check(!pd.begin(0x20, "", 7100, 5000) && !pd.begin(0x20, "", 7200, 5000), "a replay with no key: never the same playback");

        Wear rw;
        rw.forced = true; rw.costume = 104; rw.color = 0;
        Drawn dr{ 1, 104, false, 8, 1, "mc1" };
        wear_draw(rw, Side{ 1, 0, 0 }, &dr, true, false);
        check(rw.forced && rw.change && rw.costume == 8 && rw.color == 1 && rw.colormod == "mc1" && rw.random_outfit,
              "a Random outfit's draw worn: its outfit, colour 02 and entry, marked for its options");
        Wear rf;
        rf.forced = true; rf.costume = 104; rf.change = true;
        wear_draw(rf, Side{ 1, 0, 0 }, nullptr, true, false);
        check(!rf.forced && !rf.change && !rf.entry && !rf.random_outfit && std::string(rf.why).find("recorded") != std::string::npos,
              "nothing drawn: what the replay recorded (not the Random outfit, no request, no options)");
        Wear rc2;
        rc2.forced = true; rc2.costume = 2; rc2.random_color = true;
        Drawn dc{ 6, 2, true, 2, 0, "" };
        wear_draw(rc2, Side{ 6, 2, 0 }, &dc, false, true);
        check(rc2.forced && !rc2.change && !rc2.random_outfit && rc2.colormod.empty(), "a Random colour drawn as recorded: nothing written, still asked");
    }

    // ---- the Color Mod's requests ----
    {
        const std::vector<Side> mir = { { 1, 0, 1 }, { 1, 0, 1 } };
        std::vector<Wear> wr(2);
        wr[0].forced = true; wr[0].costume = 0; wr[0].color = 1; wr[0].colormod = "mc1";
        wr[1].forced = true; wr[1].costume = 0; wr[1].color = 4; wr[1].colormod = "";
        auto q = replay_requests(wr, mir);
        check(q.size() == 2 && q[0].side == 1 && q[1].side == 2 && q[0].id == "mc1" && q[1].id.empty() && q[1].color == 4,
              "requests: one per side that asks, its battle side (team + 1)");
        check(q[0].shared && q[1].shared && requests_shared(wr, mir),
              "a Random colour mirror on one outfit, two requests: each names its side (shared by an older Color Mod)");
        auto fo = fallback_order(q);
        check(fo.size() == 2 && fo.back().side == 1 && fo.back().id == "mc1", "older Color Mod: the side on colour 02 asked last (its entry wins)");
        wr[1].costume = 4;
        q = replay_requests(wr, mir);
        check(!requests_shared(wr, mir) && !q[0].shared && !q[1].shared, "the two sides on different outfits: any side, as before");
        wr[1].costume = 0; wr[1].colormod = "mc1"; wr[1].color = 1;
        check(!requests_shared(wr, mir), "both sides asking the same: nothing lost, any side");
        std::vector<Wear> myown(2);
        myown[0].entry = true; myown[0].costume = 0; myown[0].color = 1; myown[0].colormod = "mc1";
        q = replay_requests(myown, mir);
        check(q.size() == 1 && q[0].side == 1 && q[0].shared, "My Own, the opponent on the same outfit: the player's side named");
        check(requests_shared(myown, mir), "My Own in a mirror, the opponent on the same outfit: shared by an older Color Mod");
        check(!requests_shared(myown, { { 1, 0, 1 }, { 1, 2, 1 } }) && !requests_shared(myown, { { 1, 0, 1 }, { 6, 0, 1 } }) &&
              !replay_requests(myown, { { 1, 0, 1 }, { 6, 0, 1 } })[0].shared,
              "the opponent on another outfit, or another fighter: any side, as before");
        std::vector<Wear> one(2);
        one[1].forced = true; one[1].costume = 3; one[1].color = 7;
        q = replay_requests(one, { { 1, 0, 1 }, { 6, 2, 0 } });
        check(q.size() == 1 && q[0].side == 2 && !q[0].shared, "one forced side against another fighter: any side");
        check(replay_requests(std::vector<Wear>(2), mir).empty(), "Off on both sides: no request");
    }

    if (fails) { std::printf("%d FAILED\n", fails); return 1; }
    std::printf("all ok\n");
    return 0;
}
