// SF6 Slots — the Random outfit: what each draw takes. Pure logic, no game access (plugin/tests); the game side is costumes.cpp.
//
// The loader gives every character one more outfit, "Random" (registry.json possession "random": true): the last of its
// Costume row, a copy of Outfit 1 (its preview is Outfit 1), with one colour, which the Color row shows as Random too. Random
// is never worn as such: where the outfit worn is decided, a draw takes its place.
//   - A character's pool (pool_of): the game's outfits its Costume row listed that the game's costume table has (a number
//     neither the loader's nor the table's, a pak another program left, is never drawn: split_listed), then the loader's outfits
//     the player sees now (registry.json), Random left out, never an NSFW outfit while those are hidden (checked again at each
//     draw). No ownership limit (Wael 22/11): ranked draws from the same pool as every other mode. A draw takes one the pool has
//     not given in this round; once every one was given, a new round starts (never with the last one drawn, when there are two
//     or more). A value refused only for now (a mirror's colour, an NSFW outfit hidden) stays given until the round ends.
//   - The colour: the same rule among every colour the drawn outfit has (outfit_colours: the game's table, owned or not, and its
//     Color row; a slot's 0-9) and the Color Mod's RW / MC entries of that outfit (when the Color Mod can dress them in a
//     battle), one round per outfit. An entry rides on colour 02 (1): the game wears 02, the Color Mod dresses the entry on this
//     PC (its battle requests); online the opponent sees 02.
//   - A Random colour of any outfit (the Color Mod's Random value in its Color row, SF6ColorMod_RandomChoice): the same colour
//     draw for the outfit chosen, the same round as the Random outfit's draws of it. Battle Settings keeps its next one
//     (next_colour), as it keeps the Random outfit's next draw.
//   - Local modes (the outfit is picked on a select screen): drawn when the cursor lands on Random, on a copy of the rounds,
//     given in the rounds (give_draw) once a battle wears it.
//   - Online, rooms, Battle Hub (Battle Settings' choice): the next draw is made before matchmaking. Outfit 1 or DriveTech goes
//     in the save in Color 1 / 2, which the fighter profile sends (the opponent sees it); any other outfit (a Slots Loader
//     outfit, the game's Outfit 2, 3...) and a colour from Color 3 on leave the carrier there and the alias wears them on this
//     PC only (save_holds). A match against a new opponent (the first load of its fighters' files) takes the next draw for the
//     whole set (rematches keep it) and makes the one after (bind_set).
// The rounds and the next draw are kept in state.json ("random"): they go on after a restart (forget: numbers no longer known
// leave them).
#pragma once

#include "json.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <map>
#include <string>
#include <vector>

namespace slots::rnd {

// One round of draws: what it gave so far (in order) and the last value drawn (kept across rounds)
struct Pool {
    std::vector<int> drawn;
    int last = -1;
};

// An outfit and its colour (costume -1: none). pos: the place in the character's own list it was drawn from (List below; -1:
// drawn from the pool), stamp: that list's (a draw made on a copy of the rounds is given in the rounds themselves later)
struct Draw {
    int costume = -1, color = -1;
    int pos = -1;
    std::string stamp;
    bool valid() const { return costume >= 0; }
    bool operator==(const Draw& o) const { return costume == o.costume && color == o.color; }
};

// A character's rounds and its next draw for Battle Settings' Random
struct Fighter {
    Pool outfits;
    std::map<int, Pool> colours;   // per outfit drawn
    Draw next;
    Draw next_colour;              // Battle Settings' Random colour: its outfit and its next colour (none: not chosen)
    Pool list;                     // the places of the player's own list given (List below), when the character has one
    std::string list_stamp;        // the list those places are of (List::stamp): another list starts again from its top
};

// ---- the colours a draw takes: the game's numbers (0 = Color 1...) and the Color Mod's RW / MC entries ------------------
// An entry is drawn as a number of its own, from its id (the entry file's path): the same file gives the same number whatever
// the Color Mod's order (a new pack renumbers the labels, not the files), so the rounds kept in state.json stay right.
constexpr int kEntryMin = 1000;    // at or above: an entry
constexpr int kEntryCarrier = 1;   // the game's colour an entry rides on (02)
inline bool is_entry(int v) { return v >= kEntryMin; }
inline int entry_value(const std::string& id) {
    uint32_t h = 2166136261u;                               // FNV-1a
    for (unsigned char ch : id) { h ^= ch; h *= 16777619u; }
    return kEntryMin + static_cast<int>(h % (0x7FFFFFFFu - static_cast<uint32_t>(kEntryMin)));
}
// The colour number the game wears for a value drawn
inline int worn_colour(int v) { return is_entry(v) ? kEntryCarrier : v; }

// A source of random numbers (costumes.cpp: a Mersenne Twister; the tests: a fixed sequence)
using Rng = std::function<uint32_t()>;
using Allowed = std::function<bool(int)>;

inline bool has(const std::vector<int>& v, int x) { return std::find(v.begin(), v.end(), x) != v.end(); }

// One of `candidates` that `allowed` accepts (none given: all) and the round has not given; when every one allowed now was given,
// a new round starts, without the last one drawn while another can be. What the round gave that is no longer a candidate is
// forgotten; one only refused now (a mirror's colour, an NSFW outfit hidden) stays given until the round ends. -1: no candidate
// is allowed (the pool is left as it was).
inline int draw(Pool& p, const std::vector<int>& candidates, const Allowed& allowed, const Rng& rng) {
    std::vector<int> cand, ok;
    for (int c : candidates)
        if (!has(cand, c)) cand.push_back(c);
    for (int c : cand)
        if (!allowed || allowed(c)) ok.push_back(c);
    if (ok.empty()) return -1;
    p.drawn.erase(std::remove_if(p.drawn.begin(), p.drawn.end(), [&](int d) { return !has(cand, d); }), p.drawn.end());
    std::vector<int> left;
    for (int c : ok)
        if (!has(p.drawn, c)) left.push_back(c);
    if (left.empty()) {
        p.drawn.clear();
        for (int c : ok)
            if (c != p.last || ok.size() == 1) left.push_back(c);
    }
    const int v = left[rng() % left.size()];
    p.drawn.push_back(v);
    p.last = v;
    return v;
}

// A value drawn on a copy of the round, given now in the round itself (Random on a select screen: drawn when the cursor lands
// on it, given once the battle wears it), as draw() gives it: a new round when this round gave it already
inline void give(Pool& p, int v) {
    if (v < 0) return;
    if (has(p.drawn, v)) p.drawn.clear();
    p.drawn.push_back(v);
    p.last = v;
}

// A colour of one outfit from its pool: `colours` the candidates (the game's and the entries), `allowed` those it may take
// now (a mirror's colour left out; none given: all). Nothing to take: colour 0 (Color 1, which every outfit has).
inline int draw_colour(Fighter& f, int costume, const std::vector<int>& colours, const Allowed& allowed, const Rng& rng) {
    const int v = draw(f.colours[costume], colours, allowed, rng);
    return v < 0 ? 0 : v;
}

// An outfit from the character's pool, then its colour from that outfit's pool. `colours_of` gives the colours the outfit's
// Color row lists (and its entries); none known: colour 0. `colour_allowed(costume, colour)`: what the colour may be (none
// given: any). Not valid: no outfit allowed.
inline Draw draw_outfit(Fighter& f, const std::vector<int>& outfits, const Allowed& allowed,
                        const std::function<std::vector<int>(int)>& colours_of, const Rng& rng,
                        const std::function<bool(int, int)>& colour_allowed = nullptr) {
    Draw d;
    d.costume = draw(f.outfits, outfits, allowed, rng);
    if (d.costume < 0) return Draw{};
    const std::vector<int> cs = colours_of ? colours_of(d.costume) : std::vector<int>{};
    const int cos = d.costume;
    d.color = draw_colour(f, cos, cs, colour_allowed ? Allowed([&](int v) { return colour_allowed(cos, v); }) : Allowed(), rng);
    return d;
}

// A new round of outfits ("Display NSFW Outfits" changed: the pool is made again from what the row lists now)
inline void refill(Fighter& f) { f.outfits.drawn.clear(); }

// An outfit and its colour drawn on a copy of the character's rounds, given in its rounds (give)
inline void give_draw(Fighter& f, const Draw& d) {
    if (!d.valid()) return;
    if (d.pos >= 0) {   // drawn from the player's list: its place, not the pool's round
        if (f.list_stamp != d.stamp) { f.list = Pool{}; f.list_stamp = d.stamp; }
        give(f.list, d.pos);
    } else give(f.outfits, d.costume);
    if (d.color >= 0) give(f.colours[d.costume], d.color);
}

// ---- the player's own list (random_list.json, written by the Slotsloader Manager) ----------------------------------------
// A character with a list draws from it instead of the pool (ZZtaii 27/11: "drop/reorder random outfits via a pre-decided
// logic that user themselves select ... how many repetitions, color"; "if the order isn't present, then it can be random"):
//   - "order": the list from its top, each outfit `repeat` times in a row, then from the top again.
//   - "shuffle": the list as a deck, an outfit `repeat` cards; a round gives every card once (draw(): never the last card
//     drawn first in a new round).
//   - An outfit's `colours` (none: any colour): its colour is drawn among those, in that outfit's colour round. Colours the
//     draw may not take now (a mirror's) are skipped; none left: any colour.
//   - A place whose outfit the draw may not take now (an NSFW outfit hidden, a mod gone, an outfit the Costume row does not
//     list) is passed over. No place can be taken: the pool's draw, as without a list.
// The places given are kept in the character's rounds (Fighter::list, state.json); the list's stamp tells another list from
// the same one written again.
struct ListItem {
    int costume = -1;
    int repeat = 1;                // 1 to kMaxRepeat
    std::vector<int> colours;      // the game's colour numbers (0 = Color 1, below kMaxColours); empty: any
};
struct List {
    bool shuffle = false;
    std::vector<ListItem> items;
    std::string stamp;             // made from the items and the mode (list_stamp)
};
constexpr int kMaxRepeat = 20;
constexpr int kMaxColours = 64;   // a game outfit can have more than 10 colours

inline std::string list_stamp(const List& l) {
    uint32_t h = 2166136261u;
    auto mix = [&h](int v) { for (int i = 0; i < 4; ++i) { h ^= static_cast<uint32_t>(v >> (8 * i)) & 0xFFu; h *= 16777619u; } };
    mix(l.shuffle ? 1 : 0);
    for (auto& it : l.items) {
        mix(it.costume); mix(it.repeat); mix(static_cast<int>(it.colours.size()));
        for (int c : it.colours) mix(c);
    }
    char b[9];
    std::snprintf(b, sizeof b, "%08x", h);
    return b;
}
// The number of places (each item `repeat` times) and the item at a place
inline int list_places(const List& l) {
    int n = 0;
    for (auto& it : l.items) n += std::clamp(it.repeat, 1, kMaxRepeat);
    return n;
}
inline int list_item_at(const List& l, int pos) {
    for (size_t i = 0; i < l.items.size(); ++i) {
        const int r = std::clamp(l.items[i].repeat, 1, kMaxRepeat);
        if (pos < r) return static_cast<int>(i);
        pos -= r;
    }
    return -1;
}

// The next place of the list `allowed_costume` accepts the outfit of, given in the character's list round. -1: none.
inline int draw_place(Fighter& f, const List& l, const Allowed& allowed_costume, const Rng& rng) {
    if (f.list_stamp != l.stamp) { f.list = Pool{}; f.list_stamp = l.stamp; }
    const int n = list_places(l);
    if (n <= 0) return -1;
    auto ok = [&](int pos) {
        const int i = list_item_at(l, pos);
        return i >= 0 && (!allowed_costume || allowed_costume(l.items[static_cast<size_t>(i)].costume));
    };
    if (l.shuffle) {
        std::vector<int> places;
        for (int p = 0; p < n; ++p) places.push_back(p);
        return draw(f.list, places, ok, rng);
    }
    const int last = f.list.last >= 0 && f.list.last < n ? f.list.last : -1;
    for (int step = 1; step <= n; ++step) {
        const int pos = (last + step) % n;
        if (!ok(pos)) continue;
        if (pos <= last) f.list.drawn.clear();   // from the top again
        f.list.drawn.push_back(pos);
        f.list.last = pos;
        return pos;
    }
    return -1;
}

// An outfit from the character's list, then its colour among the item's colours (`colours_of`: every colour the outfit has, as
// draw_outfit). Not valid: no place of the list can be taken now (the caller draws from the pool).
inline Draw draw_listed(Fighter& f, const List& l, const Allowed& allowed, const std::function<std::vector<int>(int)>& colours_of,
                        const Rng& rng, const std::function<bool(int, int)>& colour_allowed = nullptr) {
    const int pos = draw_place(f, l, allowed, rng);
    if (pos < 0) return Draw{};
    const ListItem& it = l.items[static_cast<size_t>(list_item_at(l, pos))];
    Draw d;
    d.costume = it.costume;
    d.pos = pos;
    d.stamp = l.stamp;
    const std::vector<int> all = colours_of ? colours_of(d.costume) : std::vector<int>{};
    std::vector<int> cs;
    for (int c : all)
        if (has(it.colours, c)) cs.push_back(c);
    const int cos = d.costume;
    auto may = [&](int v) { return !colour_allowed || colour_allowed(cos, v); };
    if (cs.empty() || std::none_of(cs.begin(), cs.end(), may)) cs = all;   // none of its colours now: any
    d.color = draw_colour(f, cos, cs, colour_allowed ? Allowed(may) : Allowed(), rng);
    return d;
}

// random_list.json:
// { "format": "sf6-random-list-1", "fighters": { "<fighter id>": { "mode": "order" | "shuffle", "outfits": [
//     { "mod_id": "<registry mod_id>", "base": <outfit it was made from>, "costume": <its number when written>, "repeat": 2,
//       "colours": [0, 3] },   (colour numbers 0 to 63)
//     { "game": <the game's outfit number, 0 = Outfit 1>, "repeat": 1 } ] } } }
// An extra outfit is named by its mod (the loader can number it again); `resolve(fid, mod_id, costume, base)` gives its number
// now (-1: gone, the item is left out). A character whose list holds nothing it can name has no list.
using Resolve = std::function<int(int, const std::string&, int, int)>;
inline std::map<int, List> parse_lists(const std::string& text, const Resolve& resolve) {
    std::map<int, List> out;
    Json j;
    if (!json_parse(text, j) || j.kind != Json::Object) return out;
    const Json* fmt = j.get("format");
    if (!fmt || fmt->kind != Json::String || fmt->s != "sf6-random-list-1") return out;
    const Json* fs = j.get("fighters");
    if (!fs || fs->kind != Json::Object) return out;
    for (auto& [k, v] : fs->obj) {
        const int fid = std::atoi(k.c_str());
        if (fid <= 0 || v.kind != Json::Object) continue;
        List l;
        const Json* mode = v.get("mode");
        l.shuffle = mode && mode->kind == Json::String && mode->s == "shuffle";
        const Json* os = v.get("outfits");
        if (!os || os->kind != Json::Array) continue;
        for (auto& e : os->arr) {
            if (e.kind != Json::Object) continue;
            ListItem it;
            const Json* game = e.get("game");
            const Json* mod = e.get("mod_id");
            if (game && game->kind == Json::Number) it.costume = static_cast<int>(game->num);
            else if (mod && mod->kind == Json::String && !mod->s.empty() && resolve) {
                const Json* c = e.get("costume");
                const Json* b = e.get("base");
                it.costume = resolve(fid, mod->s, c && c->kind == Json::Number ? static_cast<int>(c->num) : -1,
                                     b && b->kind == Json::Number ? static_cast<int>(b->num) : -1);
            }
            if (it.costume < 0) continue;
            if (const Json* r = e.get("repeat"); r && r->kind == Json::Number) it.repeat = std::clamp(static_cast<int>(r->num), 1, kMaxRepeat);
            if (const Json* cs = e.get("colours"); cs && cs->kind == Json::Array)
                for (auto& c : cs->arr)
                    if (c.kind == Json::Number && c.num >= 0 && c.num < kMaxColours && !has(it.colours, static_cast<int>(c.num)))
                        it.colours.push_back(static_cast<int>(c.num));
            l.items.push_back(it);
        }
        if (l.items.empty()) continue;
        l.stamp = list_stamp(l);
        out[fid] = std::move(l);
    }
    return out;
}

// ---- the pool ----
// What a character's Costume row listed, split: the game's outfits (`in_table`: the game's costume table has it; none given: not
// known, every number is taken as the game's) and the numbers neither one of the loader's (`is_slot`) nor the table's (a pak
// another program left: never drawn). The loader's outfits and Random's own number (`random_no`) are left out: pool_of adds the
// loader's from the registry.
struct Listed { std::vector<int> game, unknown; };
inline Listed split_listed(const std::vector<int>& listed, int random_no, const std::function<bool(int)>& is_slot,
                           const std::function<bool(int)>& in_table) {
    Listed out;
    for (int c : listed) {
        if (c < 0 || c == random_no || (is_slot && is_slot(c)) || has(out.game, c) || has(out.unknown, c)) continue;
        (in_table && !in_table(c) ? out.unknown : out.game).push_back(c);
    }
    return out;
}
// The pool: the game's outfits listed (`game`; never read: Outfit 1 alone), then the loader's outfits the player sees now
inline std::vector<int> pool_of(const std::vector<int>* game, const std::vector<int>& slots) {
    std::vector<int> v = game && !game->empty() ? *game : std::vector<int>{ 0 };
    for (int s : slots)
        if (s >= 0 && !has(v, s)) v.push_back(s);
    return v;
}
// The game's colours of one of its outfits, as a draw takes them: every colour its records list in the game's table (owned or
// not: Wael 22/11, no ownership limit) and every colour its Color row showed, each once (the table's first, entries left out)
inline std::vector<int> outfit_colours(const std::vector<int>& table, const std::vector<int>& row) {
    std::vector<int> v;
    for (const std::vector<int>* src : { &table, &row })
        for (int c : *src)
            if (c >= 0 && !is_entry(c) && !has(v, c)) v.push_back(c);
    return v;
}
// The numbers no longer known (`known` false: neither one of the loader's outfits nor in the game's costume table) dropped from
// a character's rounds and next draws (state.json kept them from a session that had them). Returns how many went.
inline int forget(Fighter& f, const std::function<bool(int)>& known) {
    int n = 0;
    const size_t was = f.outfits.drawn.size();
    f.outfits.drawn.erase(std::remove_if(f.outfits.drawn.begin(), f.outfits.drawn.end(), [&](int c) { return !known(c); }),
                          f.outfits.drawn.end());
    n += static_cast<int>(was - f.outfits.drawn.size());
    if (f.outfits.last >= 0 && !known(f.outfits.last)) { f.outfits.last = -1; ++n; }
    for (auto it = f.colours.begin(); it != f.colours.end();) {
        if (!known(it->first)) { it = f.colours.erase(it); ++n; }
        else ++it;
    }
    if (f.next.valid() && !known(f.next.costume)) { f.next = Draw{}; ++n; }
    if (f.next_colour.valid() && !known(f.next_colour.costume)) { f.next_colour = Draw{}; ++n; }
    return n;
}

// What the save holds outside Battle Settings while Random is the choice: the next draw when it is Outfit 1 or DriveTech
// (`next_is_safe`: what every player has; the profile sends it, the opponent sees it), an entry as the colour it rides on; else
// nothing (the carrier: one of ours or another game outfit the alias wears, `next_is_ours`, or a number nobody knows)
inline Draw save_holds(const Draw& next, bool next_is_ours, bool next_is_safe = true) {
    return next.valid() && !next_is_ours && next_is_safe ? Draw{ next.costume, worn_colour(next.color) } : Draw{};
}

// The first load of an online match's fighters (a new opponent): what the player's fighter wears for the whole set.
// `net_cos` / `net_col`: what the match holds for it (what the opponent sees). A game outfit drawn that the match holds (the
// profile carried it) is worn by the game itself, on both screens. Any other draw is worn on this PC only, over what the match
// holds (the alias, `local`): a Slots Loader outfit always, a game outfit when no profile was sent since the draw. Outfit 1
// (costume 0) cannot be worn that way (0 is the alias's "no outfit"): the set wears what the match holds and the draw waits.
// An entry drawn is worn over colour 02 where the outfit wears it (the Color Mod, on this PC): with the match's outfit when the
// match holds 02, with the alias's always.
// consumed: the next draw was taken (a new one is due).
struct Bind {
    Draw set;
    bool consumed = false;
    bool local = false;
};
// Since 21/11 the match never holds Color 3 or later (Wael: the network sees Color 1 / 2 only): a game colour from Color 3 on
// drawn for the outfit the match holds is kept (worn on this PC over the match's Color 1 / 2, plugin colour_only).
inline Bind bind_set(const Draw& next, bool next_is_ours, int net_cos, int net_col) {
    const Draw net{ net_cos, net_col };
    if (!next.valid()) return { net, false, false };
    if (!next_is_ours && net_cos == next.costume) {
        const bool keep = (is_entry(next.color) && net_col == kEntryCarrier) || (!is_entry(next.color) && next.color >= 2);
        return { Draw{ net_cos, keep ? next.color : net_col }, true, false };
    }
    if (next_is_ours || next.costume > 0) return { next, true, true };
    return { net, false, false };
}

// The same for Battle Settings' Random colour of one of the game's outfits (`next`: next_colour), the match holding `net_cos`
// / `net_col`: taken for the whole set when the match holds that outfit in the colour drawn (the profile carried it; an entry:
// its colour 02, the Color Mod dresses it on this PC). Any other colour in the match (no profile sent since the draw): the set
// wears it, the draw waits.
inline Bind bind_colour(const Draw& next, int net_cos, int net_col) {
    const Draw net{ net_cos, net_col };
    if (!next.valid() || next.costume != net_cos || worn_colour(next.color) != net_col) return { net, false, false };
    return { next, true, false };
}

// ---- state.json "random" ----------------------------------------------------------------------------------------------
// { "<fighter id>": { "outfits": { "drawn": [7, 0], "last": 0 }, "next": [7, 3], "next_colour": [2, 5],
//                     "colours": { "7": { "drawn": [3], "last": 3 } } } }

inline std::string ints_json(const std::vector<int>& v) {
    std::string o = "[";
    for (size_t i = 0; i < v.size(); ++i) o += (i ? ", " : "") + std::to_string(v[i]);
    return o + "]";
}
inline std::string pool_json(const Pool& p) {
    return "{ \"drawn\": " + ints_json(p.drawn) + ", \"last\": " + std::to_string(p.last) + " }";
}
// The object's text, its lines indented by `indent` spaces (its first line not)
inline std::string state_json(const std::map<int, Fighter>& all, int indent) {
    const std::string pad(static_cast<size_t>(indent), ' ');
    std::string o = "{";
    bool first = true;
    for (auto& [fid, f] : all) {
        if (f.outfits.drawn.empty() && f.outfits.last < 0 && !f.next.valid() && !f.next_colour.valid() && f.colours.empty()
            && f.list_stamp.empty()) continue;
        o += std::string(first ? "" : ",") + "\n" + pad + "    \"" + std::to_string(fid) + "\": { \"outfits\": " + pool_json(f.outfits);
        if (f.next.valid()) o += ", \"next\": [" + std::to_string(f.next.costume) + ", " + std::to_string(f.next.color) + "]";
        if (f.next_colour.valid())
            o += ", \"next_colour\": [" + std::to_string(f.next_colour.costume) + ", " + std::to_string(f.next_colour.color) + "]";
        if (!f.colours.empty()) {
            o += ", \"colours\": {";
            bool fc = true;
            for (auto& [cos, p] : f.colours) {
                o += std::string(fc ? " " : ", ") + "\"" + std::to_string(cos) + "\": " + pool_json(p);
                fc = false;
            }
            o += " }";
        }
        if (!f.list_stamp.empty())
            o += ", \"list\": { \"drawn\": " + ints_json(f.list.drawn) + ", \"last\": " + std::to_string(f.list.last) + ", \"stamp\": \""
                 + f.list_stamp + "\" }";
        o += " }";
        first = false;
    }
    return o + (first ? "}" : "\n" + pad + "}");
}

inline std::vector<int> json_ints(const Json* a) {
    std::vector<int> out;
    if (a && a->kind == Json::Array)
        for (auto& e : a->arr)
            if (e.kind == Json::Number) out.push_back(static_cast<int>(e.num));
    return out;
}
inline Pool json_pool(const Json* p) {
    Pool out;
    if (!p || p->kind != Json::Object) return out;
    out.drawn = json_ints(p->get("drawn"));
    if (const Json* l = p->get("last"); l && l->kind == Json::Number) out.last = static_cast<int>(l->num);
    return out;
}
// state.json's "random" (anything unreadable is left out)
inline std::map<int, Fighter> parse_state(const Json* j) {
    std::map<int, Fighter> out;
    if (!j || j->kind != Json::Object) return out;
    for (auto& [k, v] : j->obj) {
        const int fid = std::atoi(k.c_str());
        if (fid <= 0 || v.kind != Json::Object) continue;
        Fighter f;
        f.outfits = json_pool(v.get("outfits"));
        const std::vector<int> nx = json_ints(v.get("next"));
        if (nx.size() == 2 && nx[0] >= 0 && nx[1] >= 0) f.next = { nx[0], nx[1] };
        const std::vector<int> nc = json_ints(v.get("next_colour"));
        if (nc.size() == 2 && nc[0] >= 0 && nc[1] >= 0) f.next_colour = { nc[0], nc[1] };
        if (const Json* cs = v.get("colours"); cs && cs->kind == Json::Object)
            for (auto& [ck, cv] : cs->obj) {
                const int cos = std::atoi(ck.c_str());
                if (cos >= 0 && (ck == "0" || cos > 0)) f.colours[cos] = json_pool(&cv);
            }
        if (const Json* l = v.get("list"); l && l->kind == Json::Object)
            if (const Json* s = l->get("stamp"); s && s->kind == Json::String && !s->s.empty()) {
                f.list = json_pool(l);
                f.list_stamp = s->s;
            }
        out[fid] = std::move(f);
    }
    return out;
}

} // namespace slots::rnd
