// SF6 Slots — Replay Outfits: what each fighter wears while a replay plays. Pure logic, no game access (plugin/tests);
// the game side is replay_outfits.cpp.
//
// Per character, in a replay only (never in a live battle):
//   - Off (no entry in replay_outfits.json, or any mode but "forced" / "own"): what the replay recorded, the game's data
//     (Wael 22/11: "OFF = the game's data"). Since the online alias (v1.49) that is Outfit 1 / DriveTech in Color 1 / 2
//     for the player's own fighter, what the opponent saw.
//   - My Own ("mode": "own", the Costume row's first value): what the player really wore in each match. The replay only
//     holds the carrier of the online alias (carrier.hpp) for the player's own fighter, so the player's real outfit is
//     taken from the journal (replay_journal.jsonl, one line per online match, written when the game builds the match's
//     replay); only the player's side changes, the opponent keeps what the replay recorded. A replay with no journal
//     line shows what it recorded. (Until 22/11 this was what Off did.)
//   - Forced: the chosen outfit and colour on every side that plays that character, the player's own included.
//     The character's Random outfit (registry possession "random"): each side a draw of its pool at each playback
//     (costumes::replay_random), the sides of a mirror match in a random order (draw_order); a Random colour likewise
//     (costumes::replay_random_colour). A draw that fails: what the replay recorded on that side.
// An extra outfit is named by its mod (registry.json "mod_id"), because the loader numbers slots again; a mod that is
// gone leaves the character on what the replay recorded.
// The Color Mod is asked per battle side (SF6ColorMod_RequestSide: the two fighters of a mirror match on the same outfit each
// wear their own); an older Color Mod has one request per fighter / outfit (fallback_order).
#pragma once

#include "json.hpp"
#include "replay_bgm.hpp"
#include "replay_options.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <map>
#include <string>
#include <vector>

namespace slots::replay {

// ---- the replay's key ------------------------------------------------------------------------------------------------
// A replay is known by its app.ReplayInfo, the same in the replay the game builds at the end of a match and in the one
// the server gives back later: the match's random seed, the stage, the fighters and each round's random seed. The
// outfits and colours are left out of the key: the copy that holds the carrier and a copy that might hold the worn
// outfit must give the same key.
struct ReplayFacts {
    uint32_t seed = 0;
    int stage = 0;
    std::vector<int> fighters;            // FighterID per side (ReplayInfo.Fighters order: P1, P2)
    std::vector<uint32_t> round_seeds;    // ReplayRoundInfo.RandomSeed per round
};

inline uint64_t fnv1a(uint64_t h, uint64_t v, int bytes) {
    for (int i = 0; i < bytes; ++i) { h ^= (v >> (8 * i)) & 0xFF; h *= 0x100000001B3ull; }
    return h;
}
// 16 hex digits; "" when the facts cannot name a match (no fighter, no round). The rounds' trailing zero seeds are left
// out: a replay given by the server has a round entry per possible round (5, the rounds not played at 0), the one built at
// the end of the match one per round played (seen 05/11).
// The stage is not in the key (version 2, 22/11): the replay built at the end of a match can hold stage 0 where the server's
// copy holds the stage played (a Battle Hub cabinet match, 22/11 20:54: journal stage 0, replay stage 20000, seed and rounds
// the same; the first replay My Own ever dressed, 20:40, was a ranked match on stage 0 on both). The seeds name the match.
// with_stage: the key of the lines written before (version 1), still looked up for the lines that hold no facts.
inline std::string replay_key(const ReplayFacts& r, bool with_stage = false) {
    size_t rounds = r.round_seeds.size();
    while (rounds && (r.round_seeds[rounds - 1] & 0xFFFF) == 0) --rounds;
    if (r.fighters.empty() || !rounds) return {};
    uint64_t h = 0xCBF29CE484222325ull;
    h = fnv1a(h, with_stage ? 1 : 2, 1);  // key version
    h = fnv1a(h, r.seed, 4);
    if (with_stage) h = fnv1a(h, static_cast<uint32_t>(r.stage), 4);
    h = fnv1a(h, r.fighters.size(), 1);
    for (int f : r.fighters) h = fnv1a(h, static_cast<uint32_t>(f), 4);
    h = fnv1a(h, rounds, 1);
    for (size_t i = 0; i < rounds; ++i) h = fnv1a(h, r.round_seeds[i] & 0xFFFF, 2);
    char b[17];
    std::snprintf(b, sizeof b, "%016llx", static_cast<unsigned long long>(h));
    return b;
}

// ---- the registry's slots (registry.json "slots") ----------------------------------------------------------------------
struct RegSlot { int fighter = 0, costume = 0, base = -1; std::string mod_id; };

// `random` (when given): each character's Random outfit, fighter -> its costume number (registry "possession" entries with
// "random": true; no slot entry, no mod)
inline std::vector<RegSlot> parse_registry(const std::string& text, std::map<int, int>* random = nullptr) {
    std::vector<RegSlot> out;
    if (random) random->clear();
    Json j;
    if (!json_parse(text, j)) return out;
    if (const Json* ps = j.get("possession"); random && ps && ps->kind == Json::Array)
        for (auto& e : ps->arr) {
            const Json* f = e.get("fighter"); const Json* c = e.get("costume_no"); const Json* r = e.get("random");
            if (!f || !c || !r || r->kind != Json::Bool || !r->b) continue;
            const int fid = static_cast<int>(f->as_int()), cos = static_cast<int>(c->as_int(-1));
            if (fid > 0 && cos > 0) (*random)[fid] = cos;
        }
    const Json* ss = j.get("slots");
    if (!ss || ss->kind != Json::Array) return out;
    for (auto& e : ss->arr) {
        const Json* f = e.get("fighter"); const Json* n = e.get("new_costume_no"); const Json* m = e.get("mod_id");
        if (!f || !n || !m || m->kind != Json::String || m->s.empty()) continue;
        RegSlot s;
        s.fighter = static_cast<int>(f->as_int());
        s.costume = static_cast<int>(n->as_int());
        if (const Json* o = e.get("original_costume_no")) s.base = static_cast<int>(o->as_int(-1));
        s.mod_id = m->s;
        if (s.fighter > 0 && s.costume > 0) out.push_back(s);
    }
    return out;
}

// The slot's costume number now, from its mod: -1 when the mod has no slot for that character any more. A mod may have
// one slot per outfit it was made from (one mod, Outfits 1-3): the one made from `base` (when known), else the one that
// still has the number `costume`, else the first.
inline int resolve_mod(const std::vector<RegSlot>& reg, int fid, const std::string& mod_id, int costume, int base = -1) {
    const RegSlot* first = nullptr;
    const RegSlot* same_no = nullptr;
    for (auto& s : reg) {
        if (s.fighter != fid || s.mod_id != mod_id) continue;
        if (base >= 0 && s.base == base) return s.costume;
        if (s.costume == costume && !same_no) same_no = &s;
        if (!first) first = &s;
    }
    return same_no ? same_no->costume : first ? first->costume : -1;
}
// The registry's slot of a costume number (its mod and the outfit it was made from); nullptr: one of the game's outfits
inline const RegSlot* slot_of(const std::vector<RegSlot>& reg, int fid, int costume) {
    for (auto& s : reg) if (s.fighter == fid && s.costume == costume) return &s;
    return nullptr;
}
// The character's Random outfit (parse_registry's `random`)
inline bool is_random_outfit(const std::map<int, int>& random, int fid, int costume) {
    auto it = random.find(fid);
    return it != random.end() && it->second == costume;
}

// ---- replay_outfits.json (written by the Replay Outfits tab) -----------------------------------------------------------
// {"version":1,"fighters":{"<fighter id>":{"mode":"forced","costume":<n>,"mod_id":"<mod>"(extra outfit),"color":<n>}}}
// "mode":"own": My Own, the journal's outfit on the player's side (no costume, no colour)
// colormod: a Color Mod entry's id (its file path) on colour 1, the entries' carrier (colormod/include/sf6colormod_entries.h)
// options: the costume options saved per outfit (replay_options.hpp)
// color_random: the colour is Random, a draw of that outfit's colours at each playback (absent in older files: false)
// own: My Own (costume -1): the journal's outfit on the player's own side, every other side as recorded
struct Forced { int costume = -1, color = 0; std::string mod_id, colormod; OutfitOptions options; bool random_color = false; bool own = false; };

// false: not a file of this format (the caller keeps what it had)
// `stage` (when given): "stage":{"mode":"forced","id":N} -> N (StageDesc.StageId), -1 = the replay's own stage
// `bgm` (when given): the Battle BGM row (replay_bgm.hpp), Random when absent
inline bool parse_outfits(const std::string& text, std::map<int, Forced>& out, int* stage = nullptr, BgmChoice* bgm = nullptr) {
    Json j;
    std::string t = text;
    if (t.compare(0, 3, "\xEF\xBB\xBF") == 0) t.erase(0, 3);
    if (!json_parse(t, j) || j.kind != Json::Object) return false;
    out.clear();
    if (bgm) *bgm = parse_bgm(j);
    if (stage) {
        *stage = -1;
        if (const Json* st = j.get("stage"); st && st->kind == Json::Object) {
            const Json* mode = st->get("mode");
            const Json* id = st->get("id");
            if (mode && mode->kind == Json::String && mode->s == "forced" && id && id->kind == Json::Number && id->num >= 0)
                *stage = static_cast<int>(id->num);
        }
    }
    const Json* fs = j.get("fighters");
    if (!fs || fs->kind != Json::Object) return true;
    for (auto& [k, v] : fs->obj) {
        const int fid = std::atoi(k.c_str());
        const Json* mode = v.get("mode");
        if (fid <= 0 || !mode || mode->kind != Json::String) continue;
        if (mode->s == "own") { Forced f; f.own = true; out[fid] = f; continue; }
        if (mode->s != "forced") continue;
        const Json* c = v.get("costume");
        if (!c || c->kind != Json::Number || c->num < 0) continue;
        Forced f;
        f.costume = static_cast<int>(c->num);
        if (const Json* col = v.get("color"); col && col->kind == Json::Number && col->num >= 0) f.color = static_cast<int>(col->num);
        if (const Json* m = v.get("mod_id"); m && m->kind == Json::String) f.mod_id = m->s;
        if (const Json* cm = v.get("colormod"); cm && cm->kind == Json::String && !cm->s.empty()) { f.colormod = cm->s; f.color = 1; }
        if (const Json* rc = v.get("color_random"); rc && rc->kind == Json::Bool && rc->b) { f.random_color = true; f.color = 0; f.colormod.clear(); }
        parse_outfit_options(v, f.options);
        out[fid] = f;
    }
    return true;
}

// ---- replay_journal.jsonl (this plugin's, one line per online match) ---------------------------------------------------
// {"v":1,"key":"<16 hex>","time":<unix s>,"mode":<game mode>,"me":<0|1>,"sides":[{"fid":..,"costume":..,"color":..},..],
//  "slot":{"mod_id":"<mod>","costume":<n>,"color":<n>,"base":<n>}|null}
// Numbers only: no names, no player IDs. "sides" is what the replay holds; "slot" what the player really wore (an extra
// outfit of the alias), null when the player wore what the replay holds. Since 21/11 the alias also wears the game's own
// outfits and colours (the network holds Outfit 1 / DriveTech in Color 1 / 2): "slot":{"game":true,"costume":<n>,"color":<n>}.
// Since 22/11 "colormod":"<a Color Mod entry's id>" in "slot" too ("" = the plain colour; absent = not known: an older line,
// no Color Mod): the entry worn on colour 02 (MC / RW), which My Own asks the Color Mod for.
// Since 22/11 the key's facts too, "seed":<n>,"stage":<n>,"rounds":[<n>,..] (every round entry as read): a replay whose key
// is not in the journal is compared with them in the log (which fact differs), never dressed from them.
struct Side { int fid = 0, costume = -1, color = 0; };
struct Entry {
    std::string key;
    long long time = 0;
    int mode = -1, me = -1;
    std::vector<Side> sides;
    bool has_slot = false;
    bool game = false;                // the slot is one of the game's outfits (no mod_id): its number as it is
    std::string mod_id;
    int slot_costume = -1, slot_color = 0, slot_base = -1;
    bool has_cm = false;              // the Color Mod entry worn is known (lines from 22/11 on, with a Color Mod)
    std::string colormod;             // its id, "" = the plain colour
    bool has_facts = false;           // the key's facts (lines from 22/11 on)
    ReplayFacts facts;
};

inline std::string journal_line(const Entry& e) {
    std::string o = "{\"v\":1,\"key\":\"" + json_escape(e.key) + "\",\"time\":" + std::to_string(e.time) +
                    ",\"mode\":" + std::to_string(e.mode) + ",\"me\":" + std::to_string(e.me) + ",\"sides\":[";
    for (size_t i = 0; i < e.sides.size(); ++i)
        o += (i ? "," : "") + std::string("{\"fid\":") + std::to_string(e.sides[i].fid) + ",\"costume\":" +
             std::to_string(e.sides[i].costume) + ",\"color\":" + std::to_string(e.sides[i].color) + "}";
    o += "],\"slot\":";
    const std::string cm = e.has_cm ? ",\"colormod\":\"" + json_escape(e.colormod) + "\"" : std::string();
    if (e.has_slot && e.game)
        o += "{\"game\":true,\"costume\":" + std::to_string(e.slot_costume) + ",\"color\":" + std::to_string(e.slot_color) + cm + "}";
    else if (e.has_slot)
        o += "{\"mod_id\":\"" + json_escape(e.mod_id) + "\",\"costume\":" + std::to_string(e.slot_costume) +
             ",\"color\":" + std::to_string(e.slot_color) + ",\"base\":" + std::to_string(e.slot_base) + cm + "}";
    else
        o += "null";
    if (e.has_facts) {
        o += ",\"seed\":" + std::to_string(e.facts.seed) + ",\"stage\":" + std::to_string(e.facts.stage) + ",\"rounds\":[";
        for (size_t i = 0; i < e.facts.round_seeds.size(); ++i) o += (i ? "," : "") + std::to_string(e.facts.round_seeds[i]);
        o += "]";
    }
    return o + "}\n";
}

// Every readable line, by key (a later line of the same key wins: a rematch can never give the same key, a line written
// twice is the same line). A broken line (a crash in the middle of a write) is skipped.
inline size_t parse_journal(const std::string& text, std::map<std::string, Entry>& out) {
    size_t n = 0, pos = 0;
    while (pos < text.size()) {
        size_t e = text.find('\n', pos);
        if (e == std::string::npos) e = text.size();
        std::string line = text.substr(pos, e - pos);
        pos = e + 1;
        Json j;
        if (line.empty() || !json_parse(line, j) || j.kind != Json::Object) continue;
        const Json* k = j.get("key"); const Json* me = j.get("me"); const Json* ss = j.get("sides");
        if (!k || k->kind != Json::String || k->s.empty() || !me || me->kind != Json::Number || !ss || ss->kind != Json::Array) continue;
        Entry en;
        en.key = k->s;
        en.me = static_cast<int>(me->num);
        if (const Json* t = j.get("time")) en.time = t->as_int();
        if (const Json* m = j.get("mode")) en.mode = static_cast<int>(m->as_int(-1));
        for (auto& s : ss->arr) {
            Side sd;
            if (const Json* f = s.get("fid")) sd.fid = static_cast<int>(f->as_int());
            if (const Json* c = s.get("costume")) sd.costume = static_cast<int>(c->as_int(-1));
            if (const Json* c = s.get("color")) sd.color = static_cast<int>(c->as_int());
            en.sides.push_back(sd);
        }
        if (const Json* sl = j.get("slot"); sl && sl->kind == Json::Object) {
            const Json* m = sl->get("mod_id"); const Json* c = sl->get("costume");
            const Json* g = sl->get("game");
            if (g && g->kind == Json::Bool && g->b && c && c->kind == Json::Number) {
                en.has_slot = true;
                en.game = true;
                en.slot_costume = static_cast<int>(c->num);
                if (const Json* col = sl->get("color")) en.slot_color = static_cast<int>(col->as_int());
            } else if (m && m->kind == Json::String && !m->s.empty() && c && c->kind == Json::Number) {
                en.has_slot = true;
                en.mod_id = m->s;
                en.slot_costume = static_cast<int>(c->num);
                if (const Json* col = sl->get("color")) en.slot_color = static_cast<int>(col->as_int());
                if (const Json* b = sl->get("base")) en.slot_base = static_cast<int>(b->as_int(-1));
            }
            if (const Json* cm = sl->get("colormod"); en.has_slot && cm && cm->kind == Json::String) { en.has_cm = true; en.colormod = cm->s; }
        }
        if (const Json* sd = j.get("seed"); sd && sd->kind == Json::Number) {
            en.has_facts = true;
            en.facts.seed = static_cast<uint32_t>(sd->num);
            if (const Json* st = j.get("stage")) en.facts.stage = static_cast<int>(st->as_int());
            if (const Json* rs = j.get("rounds"); rs && rs->kind == Json::Array)
                for (auto& r : rs->arr) en.facts.round_seeds.push_back(static_cast<uint32_t>(r.as_int()));
            for (auto& s : en.sides) en.facts.fighters.push_back(s.fid);
            // a line with its facts is known by the key of now, whatever key it was written with (version 1 held the stage)
            if (const std::string k2 = replay_key(en.facts); !k2.empty()) en.key = k2;
        }
        out[en.key] = en;
        ++n;
    }
    return n;
}

// The key's facts in one line, for the log: "seed 123 stage 40000 fighters 1,1 rounds 5:1234,567,0,0,0"
inline std::string facts_text(const ReplayFacts& r) {
    std::string o = "seed " + std::to_string(r.seed) + " stage " + std::to_string(r.stage) + " fighters ";
    for (size_t i = 0; i < r.fighters.size(); ++i) o += (i ? "," : "") + std::to_string(r.fighters[i]);
    o += " rounds " + std::to_string(r.round_seeds.size()) + ":";
    for (size_t i = 0; i < r.round_seeds.size(); ++i) o += (i ? "," : "") + std::to_string(r.round_seeds[i]);
    return o;
}
// A replay whose key the journal does not have: which of its facts each line of the same fighters shares (for the log, to find
// the fact that differs between the replay built at the end of the match and the one played; never used to dress). At most
// `max` lines named.
inline std::string closest_lines(const ReplayFacts& r, const std::map<std::string, Entry>& journal, size_t max) {
    auto played = [](const std::vector<uint32_t>& v) {
        std::vector<uint32_t> out;
        for (uint32_t s : v) out.push_back(s & 0xFFFF);
        while (!out.empty() && out.back() == 0) out.pop_back();
        return out;
    };
    std::string o;
    size_t n = 0, same_fighters = 0;
    for (auto& [k, e] : journal) {
        if (!e.has_facts || e.facts.fighters != r.fighters) continue;
        ++same_fighters;
        const bool seed = e.facts.seed == r.seed, stage = e.facts.stage == r.stage;
        const bool rounds = played(e.facts.round_seeds) == played(r.round_seeds);
        if (!seed && !rounds) continue;
        if (n++ < max)
            o += "; " + k + ": seed " + (seed ? "same" : "differs") + ", stage " + (stage ? "same" : "differs") + ", rounds " +
                 (rounds ? "same" : "differ") + " (" + facts_text(e.facts) + ")";
    }
    return std::to_string(same_fighters) + " journal line(s) with facts and the same fighters, " + std::to_string(n) +
           " sharing its seed or rounds" + o;
}

// ---- what each side wears ----------------------------------------------------------------------------------------------
struct Wear {
    bool change = false;                  // false: what the replay recorded
    int costume = -1, color = 0;
    const char* why = "recorded";         // for the log
    bool forced = false;                  // a forced outfit: the Color Mod is asked to dress it exactly (colormod or "")
    std::string colormod;                 // the forced Color Mod entry ("" = the game's own colours)
    bool random_color = false;            // the forced colour is Random: the playback draws it (replay_outfits.cpp)
    bool entry = false;                   // My Own on colour 02, the entry worn known: the Color Mod is asked for exactly `colormod`
                                          // ("" = the plain colour), as it dressed the match
    bool random_outfit = false;           // a draw of the Random outfit: its costume options are the ones saved for the outfit drawn,
                                          // else the player's own (Wael 22/11), never its defaults
};

// `recorded`: the replay's sides (fighter, outfit, colour, in team order). `entry`: the journal's line of this replay,
// nullptr when there is none. A side changes only to an outfit it does not wear already. A character with no entry in
// `forced` keeps what the replay recorded (Off).
inline std::vector<Wear> plan(const std::vector<Side>& recorded, const Entry* entry, const std::map<int, Forced>& forced,
                              const std::vector<RegSlot>& reg) {
    std::vector<Wear> out(recorded.size());
    for (size_t i = 0; i < recorded.size(); ++i) {
        const Side& r = recorded[i];
        Wear& w = out[i];
        auto f = forced.find(r.fid);
        if (f == forced.end()) continue;                                  // Off: the game's data
        if (!f->second.own) {
            int cos = f->second.costume;
            if (!f->second.mod_id.empty()) cos = resolve_mod(reg, r.fid, f->second.mod_id, f->second.costume);
            if (cos >= 0) {
                w.costume = cos; w.color = f->second.color; w.why = "forced";
                w.forced = true;
                w.colormod = f->second.colormod;
                w.random_color = f->second.random_color;
                w.change = cos != r.costume || w.color != r.color;
                continue;
            }
            w.why = "forced outfit's mod gone: recorded";
            continue;
        }
        // My Own: the player's own side, when the journal knows the match and the player wore an extra outfit there. The
        // side must still be the journal's fighter (a replay of the same seeds with other fighters is another match).
        w.why = "my own: recorded";
        if (!entry) { w.why = "my own, not in the journal: recorded"; continue; }
        if (entry->me != static_cast<int>(i) || !entry->has_slot) continue;
        if (i >= entry->sides.size() || entry->sides[i].fid != r.fid) { w.why = "journal side mismatch"; continue; }
        const int cos = entry->game ? entry->slot_costume : resolve_mod(reg, r.fid, entry->mod_id, entry->slot_costume, entry->slot_base);
        if (cos < 0) { w.why = "worn outfit's mod gone: recorded"; continue; }
        w.costume = cos; w.color = entry->slot_color; w.why = "my own: worn (journal)";
        if (entry->has_cm && w.color == 1) { w.entry = true; w.colormod = entry->colormod; }
        w.change = cos != r.costume || w.color != r.color;
    }
    return out;
}

// ---- Random at each playback (replay_outfits.cpp draws) ------------------------------------------------------------------
// The order the sides draw in: team order, but when two sides play the same fighter (a mirror match) a random order at each
// playback. Both draw from that fighter's one round, and a new round never starts with the last value drawn: with a pool of
// two (Outfit 1 + DriveTech) the side drawing first got the same outfit at every playback (22/11).
inline std::vector<size_t> draw_order(const std::vector<int>& fighters, const std::function<uint32_t()>& rng) {
    std::vector<size_t> o(fighters.size());
    for (size_t i = 0; i < o.size(); ++i) o[i] = i;
    bool shared = false;
    for (size_t i = 0; i < fighters.size() && !shared; ++i)
        for (size_t j = i + 1; j < fighters.size(); ++j)
            if (fighters[i] == fighters[j]) { shared = true; break; }
    if (!shared || !rng) return o;
    for (size_t k = o.size(); k > 1; --k) std::swap(o[k - 1], o[rng() % k]);
    return o;
}

// One side's draw: for that fighter and the outfit forced (its Random outfit, or an outfit whose colour is Random)
struct Drawn { int fid = 0, forced = -1; bool colour = false; int cos = -1, col = 0; std::string cm; };
// The draws of the playback being set up. The same flow map setting up the same replay (its key) again within `window_ms` of
// its last setup is the same playback: its draws are kept (the game asking twice must not draw twice and change an outfit whose
// files it may be loading already). Anything else is a new playback. The window stays: the same replay watched again may set
// up from the same flow map object, and that playback draws anew.
class PlaybackDraws {
public:
    // true: the same playback as the last setup (its draws kept)
    bool begin(uintptr_t map, const std::string& key, uint64_t now_ms, uint64_t window_ms) {
        const bool same = map && !key.empty() && map == map_ && key == key_ && now_ms - at_ <= window_ms;
        if (!same) d_.clear();
        map_ = map;
        key_ = key;
        at_ = now_ms;
        return same;
    }
    // this playback's draw of `side`, when made for that fighter and forced outfit (Random outfit: colour false)
    const Drawn* find(size_t side, int fid, int forced, bool colour) const {
        auto it = d_.find(side);
        if (it == d_.end() || it->second.fid != fid || it->second.forced != forced || it->second.colour != colour) return nullptr;
        return &it->second;
    }
    void keep(size_t side, const Drawn& d) { d_[side] = d; }
private:
    uintptr_t map_ = 0;
    std::string key_;
    uint64_t at_ = 0;
    std::map<size_t, Drawn> d_;
};

// A forced side's draw worn (`d`). nullptr: nothing could be drawn: the side keeps what the replay recorded (not the Random
// outfit itself, no Color Mod request, no options). kept: the draw of an earlier setup of this playback.
inline void wear_draw(Wear& w, const Side& recorded, const Drawn* d, bool random_outfit, bool kept) {
    if (!d) {
        w = Wear{};
        w.why = random_outfit ? "Random: nothing drawn: recorded" : "Random colour: nothing drawn: recorded";
        return;
    }
    w.costume = d->cos;
    w.color = d->col;
    w.colormod = d->cm;
    w.random_outfit = random_outfit;
    w.why = random_outfit ? (kept ? "forced: Random, this playback's draw" : "forced: Random")
                          : (kept ? "forced: Random colour, this playback's draw" : "forced: Random colour");
    w.change = d->cos != recorded.costume || d->col != recorded.color;
}

// ---- the Color Mod's requests of a replay --------------------------------------------------------------------------------
// Side `i` asks the Color Mod and shares its fighter and outfit with another side that asks something else or nothing (a mirror
// match on one outfit; My Own's player with the opponent on the same outfit): one request per fighter / outfit would dress both
inline bool side_shared(const std::vector<Wear>& wear, const std::vector<Side>& recorded, size_t i) {
    const size_t n = std::min(wear.size(), recorded.size());
    auto asks = [&](size_t j) { return wear[j].forced || wear[j].entry; };
    auto worn = [&](size_t j) { return asks(j) || wear[j].change ? wear[j].costume : recorded[j].costume; };
    if (i >= n || !asks(i)) return false;
    for (size_t j = 0; j < n; ++j)
        if (j != i && recorded[j].fid == recorded[i].fid && worn(j) == worn(i) && (!asks(j) || wear[j].colormod != wear[i].colormod))
            return true;
    return false;
}
// One per side that asks (team order; side = team + 1, the Color Mod's P1 / P2): a forced side exactly its colour (its entry,
// or "" the game's own colours), My Own the entry worn in the match. shared (side_shared): the request must name its side
// (SF6ColorMod_RequestSide); any other one is asked for any side, as before 22/11 (the side is then never needed).
struct SideRequest { int side = 0, fid = 0, costume = -1, color = 0; std::string id; bool shared = false; };
inline std::vector<SideRequest> replay_requests(const std::vector<Wear>& wear, const std::vector<Side>& recorded) {
    std::vector<SideRequest> out;
    for (size_t i = 0; i < wear.size() && i < recorded.size(); ++i) {
        const Wear& w = wear[i];
        if (!w.forced && !w.entry) continue;
        out.push_back({ static_cast<int>(i) + 1, recorded[i].fid, w.costume, w.color, w.colormod, side_shared(wear, recorded, i) });
    }
    return out;
}
// Any side shares (an older Color Mod, without SF6ColorMod_RequestSide, then dresses both with one request)
inline bool requests_shared(const std::vector<Wear>& wear, const std::vector<Side>& recorded) {
    for (size_t i = 0; i < wear.size(); ++i)
        if (side_shared(wear, recorded, i)) return true;
    return false;
}
// The order for an older Color Mod: the sides on colour 02 last, so that of two sides sharing a fighter / outfit the one an
// entry is worn on wins (an entry is worn on 02 only; on any other colour the side wears the game's own colours whatever the
// request names)
inline std::vector<SideRequest> fallback_order(std::vector<SideRequest> r) {
    std::stable_partition(r.begin(), r.end(), [](const SideRequest& q) { return q.color != 1; });
    return r;
}

} // namespace slots::replay
