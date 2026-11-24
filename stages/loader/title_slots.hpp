#pragma once
// Title screen packs: which pack is chosen and which of its files are written (pure logic, no game, no Windows:
// stagetest --selftest). The stage loader does the rest (stage_loader.cpp).
//
// A title pack is a Fluffy-style mod dropped as it is in reframework\title_mods (an archive or a folder holding
// modinfo.ini and a natives tree or a .pak, or a .pak alone). Its files are of two kinds, by their natives path:
//   title files    the title screen's movies, natives/stm/streaming/product/gui/gm/ui10000/part/title_*.mov.1
//                  (any path holding "/gui/gm/ui10000/part/title_"), and its sound banks, natives/stm/product/sound/
//                  wwise/*title*.sbnk* (ui_title_m: the music, narration_title_m: the narrator; the game's Title.scn
//                  loads both, with the movies). Not streamed: a bank needs no resource file beside its copy.
//   startup files  every other file: the boot logos and the health warning skipped (ui10001 / ui10004 gui, the logo
//                  textures and movie, re_logo.sbnk: the boot logo's sound)
// The chosen pack's title files are written into the stage pak at their real paths. With skip_intro, startup files
// are written too: the chosen pack's, or the first pack's that has some (registry order) when the chosen pack has
// none or the game's own title is chosen. Nothing else.
//
// Settings, in the plugin's reframework\data\SF6_CostumeSlots_data\settings.json (other keys ignored):
//   "title_pack": "" or missing = the game's own title; a pack's key; "random" = one pack drawn at each launch
//   "skip_intro": true / false, missing = false

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace title_slots {

struct Settings {
    std::string pack;            // "title_pack" as written: "", a key or "random"
    bool skip_intro = false;
    bool has_pool = false;       // "title_random_pool" present: the backgrounds Random draws from (keys; "game" the game's own)
    std::vector<std::string> pool;
};

struct Pack {
    std::string key, name, author;
    std::string source;          // where it came from, relative to title_mods ("Pack.zip", "Folder/Option")
    size_t title_files = 0, startup_files = 0;
    bool preview = false;        // its picture is in the stage pak at preview_resource(key)
};

// Where a pack's picture is in the stage pak (natives/stm/<this>.<tex version>), as the game loads it
inline std::string preview_resource(const std::string& key) { return "_titleslots/" + key + "/preview.tex"; }
// The game's own title's picture: reframework\data\SF6_TitleSlots_Data\game_title.png (or .jpg / .jpeg), in the pak under this key
// (a pack's key is "t" and 12 hex digits: never this)
constexpr const char* kGamePictureKey = "game";
// Random's picture: the mosaic of every pack's picture, in the pak under this key
constexpr const char* kRandomPictureKey = "random";

// A background's copy of a title file in the stage pak: natives/stm/_titleslots/<key>/<the path after natives/stm/>, a streamed
// file natives/stm/streaming/_titleslots/<key>/<the path after natives/stm/streaming/>. key: a pack's, or kGamePictureKey for the
// game's own. Used by the switch on a return to the title (stage_redirect's title table, by hash) and, being where the engine
// looks for a streamed resource of path "_titleslots/<key>/product/...", by the core's title.cpp, which gives the title's
// movies those resources while the title stays on screen.
inline std::string private_title_path(const std::string& key, const std::string& path) {
    const std::string root = "natives/stm/", streaming = "natives/stm/streaming/";
    if (path.compare(0, streaming.size(), streaming) == 0) return streaming + "_titleslots/" + key + "/" + path.substr(streaming.size());
    return root + "_titleslots/" + key + "/" + (path.compare(0, root.size(), root) == 0 ? path.substr(root.size()) : path);
}
inline std::string thumb_resource(const std::string& key) { return "_titleslots/" + key + "/thumb.tex"; }
inline std::string band_resource(const std::string& key) { return "_titleslots/" + key + "/band.tex"; }
inline std::string check_resource(bool on) { return on ? "_titleslots/ui/check_on.tex" : "_titleslots/ui/check_off.tex"; }

// A streamed resource is two files: the data under natives/stm/streaming/ and a small resource file at the same path without
// "streaming/" (a movie's: 38 bytes, "REMV", version 1, "dummy.mp4", the same for the three title movies, read 08/11). Its
// copy must sit beside the private data, or a movie given that resource waits for it forever (the game froze, 08/11).
// "" when path is not a streamed file.
inline std::string resource_file_of(const std::string& streamed_path) {
    const std::string root = "natives/stm/", streaming = "natives/stm/streaming/";
    if (streamed_path.compare(0, streaming.size(), streaming) != 0) return {};
    return root + streamed_path.substr(streaming.size());
}

// A pack's entries for the switch on a return to the title (real hash -> its copy's hash), completed with the game's own copy of
// each title file it lacks that another background has (game: the game's own background's entries). A pack with title_start alone
// ("Alpha 3 Startup", 19/11) then shows the game's loop and end, as when it is the launch's pack, and not the movies of the pack
// the launch wrote at the real paths; a pack without sound banks plays the game's music and narrator, not the launch pack's.
// Returns how many were added.
inline size_t fill_from_game(std::vector<std::pair<uint64_t, uint64_t>>& entries, const std::vector<std::pair<uint64_t, uint64_t>>& game) {
    size_t n = 0;
    for (auto& g : game) {
        if (std::any_of(entries.begin(), entries.end(), [&](const std::pair<uint64_t, uint64_t>& e) { return e.first == g.first; })) continue;
        entries.push_back(g);
        ++n;
    }
    return n;
}

struct Choice {
    int title = -1;              // pack whose title files are written; -1: the game's own title
    int startup = -1;            // pack whose startup files are written (skip_intro); -1: none
    bool random = false;         // the settings asked "random" (title is the pack drawn)
    bool unknown = false;        // the settings named a key that no pack has: the game's own title
};

namespace detail {

inline std::string lower(std::string s) {
    for (auto& c : s) if (c >= 'A' && c <= 'Z') c = char(c + 32);
    return s;
}

inline std::string trim(const std::string& s) {
    size_t a = s.find_first_not_of(" \t\r\n"), b = s.find_last_not_of(" \t\r\n");
    return a == std::string::npos ? std::string() : s.substr(a, b - a + 1);
}

inline uint64_t fnv1a64(const std::string& s) {
    uint64_t h = 14695981039346656037ull;   // the FNV-1a 64 offset basis (stage_loader.cpp's fnv64 lacks its last digit: kept there, its keys depend on it)
    for (unsigned char c : s) { h ^= c; h *= 1099511628211ull; }
    return h;
}

inline void skip_ws(const std::string& t, size_t& i) {
    while (i < t.size() && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) ++i;
}

// The JSON string whose opening quote is at t[i]: its value (escapes decoded, \u to UTF-8), i past its closing quote.
inline bool read_string(const std::string& t, size_t& i, std::string& out) {
    if (i >= t.size() || t[i] != '"') return false;
    out.clear();
    for (++i; i < t.size(); ++i) {
        const char c = t[i];
        if (c == '"') { ++i; return true; }
        if (c != '\\') { out += c; continue; }
        if (++i >= t.size()) return false;
        switch (t[i]) {
            case 'n': out += '\n'; break;
            case 't': out += '\t'; break;
            case 'r': out += '\r'; break;
            case 'b': out += '\b'; break;
            case 'f': out += '\f'; break;
            case 'u': {
                if (i + 4 >= t.size()) return false;
                unsigned cp = 0;
                for (int k = 1; k <= 4; ++k) {
                    const char h = t[i + k];
                    cp = cp * 16 + unsigned(h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10
                                            : h >= 'A' && h <= 'F' ? h - 'A' + 10 : 0);
                }
                i += 4;
                if (cp < 0x80) out += char(cp);
                else if (cp < 0x800) { out += char(0xC0 | (cp >> 6)); out += char(0x80 | (cp & 0x3F)); }
                else { out += char(0xE0 | (cp >> 12)); out += char(0x80 | ((cp >> 6) & 0x3F)); out += char(0x80 | (cp & 0x3F)); }
                break;
            }
            default: out += t[i];   // \" \\ \/
        }
    }
    return false;
}

// Steps over any JSON value at t[i] (strings, numbers, true / false / null, nested objects and arrays).
inline bool skip_value(const std::string& t, size_t& i) {
    skip_ws(t, i);
    if (i >= t.size()) return false;
    std::string s;
    if (t[i] == '"') return read_string(t, i, s);
    if (t[i] == '{' || t[i] == '[') {
        int depth = 0;
        while (i < t.size()) {
            const char c = t[i];
            if (c == '"') { if (!read_string(t, i, s)) return false; continue; }
            if (c == '{' || c == '[') ++depth;
            else if ((c == '}' || c == ']') && --depth == 0) { ++i; return true; }
            ++i;
        }
        return false;
    }
    const size_t start = i;
    while (i < t.size() && t[i] != ',' && t[i] != '}' && t[i] != ']' && t[i] != ' ' && t[i] != '\t' && t[i] != '\r' && t[i] != '\n') ++i;
    return i > start;
}

}  // namespace detail

// settings.json's "title_pack" and "skip_intro", at the top level of its object. Read tolerantly: a text that is not a
// JSON object gives the defaults, a key of another type keeps its default, a text cut short keeps what came before.
inline Settings parse_settings(std::string t) {
    Settings s;
    if (t.size() >= 3 && (uint8_t)t[0] == 0xEF && (uint8_t)t[1] == 0xBB && (uint8_t)t[2] == 0xBF) t.erase(0, 3);
    size_t i = 0;
    detail::skip_ws(t, i);
    if (i >= t.size() || t[i] != '{') return s;
    ++i;
    for (;;) {
        detail::skip_ws(t, i);
        std::string key, v;
        if (i >= t.size() || t[i] == '}' || !detail::read_string(t, i, key)) break;
        detail::skip_ws(t, i);
        if (i >= t.size() || t[i] != ':') break;
        ++i;
        detail::skip_ws(t, i);
        if (key == "title_pack" && i < t.size() && t[i] == '"') {
            if (!detail::read_string(t, i, v)) break;
            s.pack = v;
        } else if (key == "skip_intro" && t.compare(i, 4, "true") == 0) {
            s.skip_intro = true;
            i += 4;
        } else if (key == "skip_intro" && t.compare(i, 5, "false") == 0) {
            s.skip_intro = false;
            i += 5;
        } else if (key == "title_random_pool" && i < t.size() && t[i] == '[') {
            s.has_pool = true;
            ++i;
            for (;;) {
                detail::skip_ws(t, i);
                if (i >= t.size() || t[i] == ']') { if (i < t.size()) ++i; break; }
                if (t[i] == ',') { ++i; continue; }
                std::string k;
                if (!detail::read_string(t, i, k)) break;
                if (!k.empty()) s.pool.push_back(detail::lower(k));
            }
        } else if (!detail::skip_value(t, i)) {
            break;
        }
        detail::skip_ws(t, i);
        if (i >= t.size() || t[i] != ',') break;
        ++i;
    }
    return s;
}

// A pack's key: "t" + the first 12 hex digits of the FNV-1a 64 hash of its source in lower case. Not its size or date:
// the choice outlives a new download of the same file.
inline std::string pack_key(const std::string& source) {
    char b[17];
    snprintf(b, sizeof(b), "%016llx", (unsigned long long)detail::fnv1a64(detail::lower(source)));
    return "t" + std::string(b, 12);
}

// The natives paths a title pack's files have (every title pack on PC3, 11/11, holds the twelve first and no other; "Alpha 3
// Alternative Intro", 20/11, adds two title banks; the game's Title.scn loads the four banks below). A pak keeps only the hashes
// of its paths: its entries are named from these and from the paths of the packs shipped as files; an entry named by neither
// is a startup file (a startup file is written at its own hash, its path is never needed).
inline const std::vector<std::string>& known_paths() {
    static const std::vector<std::string> paths = {
        "natives/stm/streaming/product/gui/gm/ui10000/part/title_start.mov.1",
        "natives/stm/streaming/product/gui/gm/ui10000/part/title_loop.mov.1",
        "natives/stm/streaming/product/gui/gm/ui10000/part/title_end.mov.1",
        "natives/stm/product/sound/wwise/ui_title_m.sbnk.1.x64",
        "natives/stm/product/sound/wwise/narration_title_m.sbnk.1.x64",
        "natives/stm/product/sound/wwise/ui_title_es.sbnk.1.x64",
        "natives/stm/product/sound/wwise/narration_title_es.sbnk.1.x64",
        "natives/stm/streaming/product/gui/gm/ui10000/part/re logo animation.mov.1.x64",
        "natives/stm/product/sound/wwise/re_logo.sbnk.1.x64",
        "natives/stm/product/gui/gm/ui10000/ui10001.gui.530035",
        "natives/stm/product/gui/gm/ui10000/ui10004.gui.530035",
        "natives/stm/product/gui/gm/ui10000/part/tex_caplogo.tex.241101895",
        "natives/stm/product/gui/gm/ui10000/part/tex_havok_logo_im.tex.241101895",
        "natives/stm/product/gui/gm/ui10000/part/tex_megascans_logo_im.tex.241101895",
        "natives/stm/product/gui/gm/ui10000/part/tex_microsoft_logo_im.tex.241101895",
        "natives/stm/product/gui/gm/ui10000/part/tex_st_logo_im.tex.241101895",
    };
    return paths;
}

// One of the title screen's movies; path: the file's natives path, any case.
inline bool is_title_movie(const std::string& natives_path) {
    return detail::lower(natives_path).find("/gui/gm/ui10000/part/title_") != std::string::npos;
}
// One of the title screen's sound banks: a .sbnk directly in sound/wwise/ whose name holds "title" (ui_title_m, narration_title_m
// and their event banks _es); re_logo.sbnk (the boot logo's) is not one. Path: the file's natives path, any case.
inline bool is_title_sound(const std::string& natives_path) {
    const std::string p = detail::lower(natives_path), dir = "/sound/wwise/";
    const size_t d = p.rfind(dir);
    if (d == std::string::npos) return false;
    const std::string leaf = p.substr(d + dir.size());
    const size_t ext = leaf.find(".sbnk");
    return leaf.find('/') == std::string::npos && ext != std::string::npos && leaf.substr(0, ext).find("title") != std::string::npos;
}
// A title file (the title screen's movies and sound banks: written for every pack at private paths and switched on a return to
// the title) rather than a startup file; path: the file's natives path, any case.
inline bool is_title_file(const std::string& natives_path) {
    return is_title_movie(natives_path) || is_title_sound(natives_path);
}

// ---- A pack's title sound banks against the game's sounds (20/11) ----
// The title's banks come in pairs: a media bank (ui_title_m, narration_title_m: DIDX + DATA, the encoded sounds by media id)
// and an event bank (ui_title_es, narration_title_es: HIRC, whose CAkSound objects play a media id with a declared codec and
// in-memory size). A pack usually ships the media banks alone, and the game's event banks then decide how they are decoded.
// "Alpha 3 Alternative Intro" ships PCM media where this game's sounds say Vorbis: the title was silent (20/11). So, at pak
// build time, each media of a pack's bank is compared with every sound of the event banks that plays it (the pack's own event
// bank if it ships one, else the game's): codec (the WEM's fmt tag) and size. A difference is adapted in a copy of the game's
// event bank (only those two fields of only those sounds), served as that pack's title file; a difference that cannot be adapted
// (an unknown codec, a streamed sound, the pack's own event bank) refuses the pack's media bank: the game's own sound plays.
// Layout of CAkSound (bank versions 113 to 145; SF6 writes 135, read 20/11): ulID u32, then AkBankSourceData: ulPluginID u32,
// StreamType u8 (0 data in memory, 1 prefetch, 2 streamed), sourceID u32 (the media id), uInMemoryMediaSize u32, uSourceBits u8.
namespace wwise {

inline uint32_t rd32(const std::vector<uint8_t>& d, size_t i) { uint32_t v; memcpy(&v, d.data() + i, 4); return v; }
inline void wr32(std::vector<uint8_t>& d, size_t i, uint32_t v) { memcpy(d.data() + i, &v, 4); }

struct Chunk { std::string tag; size_t data = 0, size = 0; };   // data: offset of its content in the bank
// The bank's chunks (BKHD, DIDX, DATA, HIRC...); false when one runs past the end.
inline bool chunks(const std::vector<uint8_t>& b, std::vector<Chunk>& out) {
    out.clear();
    size_t i = 0;
    while (i + 8 <= b.size()) {
        const size_t n = rd32(b, i + 4);
        if (n > b.size() - i - 8) return false;
        out.push_back({ std::string(reinterpret_cast<const char*>(b.data() + i), 4), i + 8, n });
        i += 8 + n;
    }
    return i == b.size() && !out.empty() && out[0].tag == "BKHD" && out[0].size >= 8;
}
inline const Chunk* chunk(const std::vector<Chunk>& cs, const char* tag) {
    for (auto& c : cs) if (c.tag == tag) return &c;
    return nullptr;
}
inline uint32_t bank_version(const std::vector<uint8_t>& b, const std::vector<Chunk>& cs) { return rd32(b, cs[0].data); }
inline bool layout_known(uint32_t version) { return version >= 113 && version <= 145; }

// Wwise's codec plugin ids, as a CAkSound declares them, from a WEM's fmt tag: 0 for a tag not known here
constexpr uint32_t kPcm = 0x00010001, kAdpcm = 0x00020001, kVorbis = 0x00040001;
inline uint32_t codec_of_format(uint16_t tag) {
    switch (tag) {
        case 0xFFFF: return kVorbis;
        case 0xFFFE: case 0x0001: return kPcm;
        case 0x0002: return kAdpcm;
        default: return 0;
    }
}
inline std::string codec_name(uint32_t plugin) {
    if (plugin == kPcm) return "PCM";
    if (plugin == kAdpcm) return "ADPCM";
    if (plugin == kVorbis) return "Vorbis";
    char b[24];
    snprintf(b, sizeof(b), "codec 0x%x", plugin);
    return b;
}
// A WEM's fmt tag (RIFF / WAVE, its "fmt " chunk); 0 when it is not one
inline uint16_t wem_format(const uint8_t* w, size_t n) {
    if (n < 12 || memcmp(w, "RIFF", 4) != 0 || memcmp(w + 8, "WAVE", 4) != 0) return 0;
    for (size_t i = 12; i + 8 <= n;) {
        uint32_t len;
        memcpy(&len, w + i + 4, 4);
        if (memcmp(w + i, "fmt ", 4) == 0) {
            if (len < 2 || i + 10 > n) return 0;
            uint16_t tag;
            memcpy(&tag, w + i + 8, 2);
            return tag;
        }
        if (len > n - i - 8) return 0;
        i += 8 + len + (len & 1);
    }
    return 0;
}

struct Media {
    uint32_t id = 0, size = 0;
    uint16_t format = 0;
    uint32_t codec = 0;
    size_t fmt = 0;              // offset in the bank of its "fmt " chunk's content (0: none)
    uint32_t fmt_len = 0;
    uint16_t channels = 0;
};
// The fmt chunk of a WEM: offset of its content from the WEM's start and its length; false when there is none
inline bool wem_fmt(const uint8_t* w, size_t n, size_t& at, uint32_t& len) {
    if (n < 12 || memcmp(w, "RIFF", 4) != 0 || memcmp(w + 8, "WAVE", 4) != 0) return false;
    for (size_t i = 12; i + 8 <= n;) {
        memcpy(&len, w + i + 4, 4);
        if (len > n - i - 8) return false;
        if (memcmp(w + i, "fmt ", 4) == 0) { at = i + 8; return len >= 2; }
        i += 8 + len + (len & 1);
    }
    return false;
}
// A media bank's media (DIDX against DATA); false when it has none or they do not fit.
inline bool media_of(const std::vector<uint8_t>& b, std::vector<Media>& out) {
    out.clear();
    std::vector<Chunk> cs;
    if (!chunks(b, cs)) return false;
    const Chunk* idx = chunk(cs, "DIDX");
    const Chunk* data = chunk(cs, "DATA");
    if (!idx || !data || idx->size % 12) return false;
    for (size_t k = 0; k < idx->size / 12; ++k) {
        const uint32_t id = rd32(b, idx->data + k * 12), off = rd32(b, idx->data + k * 12 + 4), size = rd32(b, idx->data + k * 12 + 8);
        if (off > data->size || size > data->size - off) return false;
        Media m;
        m.id = id;
        m.size = size;
        m.format = wem_format(b.data() + data->data + off, size);
        m.codec = codec_of_format(m.format);
        size_t at = 0;
        uint32_t len = 0;
        if (wem_fmt(b.data() + data->data + off, size, at, len)) {
            m.fmt = data->data + off + at;
            m.fmt_len = len;
            if (len >= 4) m.channels = uint16_t(b[m.fmt + 2] | (b[m.fmt + 3] << 8));
        }
        out.push_back(m);
    }
    return !out.empty();
}

struct Sound { size_t at = 0; uint32_t object = 0, plugin = 0, media = 0, size = 0; uint8_t stream = 0; };  // at: ulPluginID's offset
// An event bank's CAkSound objects; false without a HIRC, a bank of an unknown layout, or a HIRC that does not fit.
inline bool sounds_of(const std::vector<uint8_t>& b, std::vector<Sound>& out) {
    out.clear();
    std::vector<Chunk> cs;
    if (!chunks(b, cs) || !layout_known(bank_version(b, cs))) return false;
    const Chunk* h = chunk(cs, "HIRC");
    if (!h || h->size < 4) return false;
    const size_t end = h->data + h->size;
    const uint32_t count = rd32(b, h->data);
    size_t p = h->data + 4;
    for (uint32_t k = 0; k < count; ++k) {
        if (p + 5 > end) return false;
        const uint8_t type = b[p];
        const size_t len = rd32(b, p + 1), payload = p + 5;
        if (len > end - payload) return false;
        if (type == 2) {                                   // CAkSound
            if (len < 18) return false;
            Sound s;
            s.object = rd32(b, payload);
            s.at = payload + 4;
            s.plugin = rd32(b, s.at);
            s.stream = b[s.at + 4];
            s.media = rd32(b, s.at + 5);
            s.size = rd32(b, s.at + 9);
            out.push_back(s);
        }
        p = payload + len;
    }
    return p == end;
}

}  // namespace wwise

struct EventBank { std::string path; std::vector<uint8_t> data; bool pack_own = false; };   // pack_own: the pack ships it
struct SoundPatch { size_t bank = 0; size_t at = 0; uint32_t plugin = 0, size = 0; };        // bank: index in the event banks
struct BankVerdict {
    bool media_bank = false;     // the bank holds media (DIDX + DATA): it was checked
    bool refuse = false;         // a media cannot be played as the sounds declare it, nor adapted: the game's own bank stays
    std::string why;             // for the log ("PCM where the game plays Vorbis", ...)
    std::vector<SoundPatch> patches;
    size_t adapted = 0;          // media whose sounds are patched
    std::vector<std::pair<size_t, uint32_t>> fields;   // the bank's own copy: (offset, value) of each PCM media's channel field rewritten
    std::vector<std::string> media_info;               // each PCM media a sound plays: its fmt values (for the log)
    uint32_t from = 0, to = 0;   // the first differing sound's declared codec and the media's real one (for the log)
};

// Adapting a pack's title sounds, two cases.
// Another codec (kAdaptTitleCodec): OFF. In game on 20/11, "Alpha 3 Alternative Intro"'s PCM media served with the game's sounds
// patched to PCM and its size (426968e), then also with the media's channel field rewritten to Wwise's packed config (e2faf5d):
// the redirect served every bank, the title stayed silent both times. The sounds are in memory (StreamType 0, uSourceBits 0), so
// streaming was not the cause either. Kept, tested offline; a bank in another codec is refused (the game's own sounds play).
// The pack was then converted to the game's own format (Wwise 2019.2.15 Vorbis, quality 5, 5.1: the same codebook hash and
// decoder sizes as the game's media): only the sizes differ from the game's sounds.
// Same codec, another size (kAdaptTitleSizes): ON. Only uInMemoryMediaSize of those sounds is patched, in this pack's copies of
// the game's event banks; anything else that differs refuses the bank.
constexpr bool kAdaptTitleCodec = false;
constexpr bool kAdaptTitleSizes = false;   // (21/11 in game: Alpha 3 re-encoded to the game's Vorbis 5.1, sizes adapted, served, still silent: refused, the game's own sounds play)

inline unsigned bit_count(uint32_t v) { unsigned n = 0; for (; v; v &= v - 1) ++n; return n; }
// Wwise's packed channel config for a Windows speaker mask (AkChannelConfig, standard type 1): 0x4 mono -> 0x4101, 0x63F 7.1 ->
// 0x63F108. The formula of the game's own RIFF parser (read in the exe by PC1, 20/11): (mask << 12) | popcount(mask) | 0x100. On
// bank media that parser reads wChannels (fmt +2) and the dword at fmt +0x14 raw, as this packed config, with no conversion.
inline uint32_t wwise_channel_config(uint32_t windows_mask) { return (windows_mask << 12) | bit_count(windows_mask) | 0x100; }
// A channel field that is already a packed config for that many channels (what the parser takes as it is)
inline bool packed_channel_config(uint32_t v, uint16_t channels) { return (v & 0xFF) == channels && ((v >> 8) & 0xF) <= 2; }

// One bank of a pack against the event banks (the pack's own replace the game's of the same path). A media no sound plays is
// left alone; a media every sound of which already says its codec and size needs nothing. game_media: the game's own media of
// the title banks; a media in a format not known here but the same as the game's media of that id has the codec its sounds say.
// adapt_codec: another codec is patched (codec and size, and a plain WAV's channel field: kAdaptTitleCodec); adapt_size: the same
// codec of another size gets its size patched (kAdaptTitleSizes). A difference neither covers refuses the bank.
inline BankVerdict check_title_bank(const std::vector<uint8_t>& bank, const std::vector<EventBank>& events,
                                    const std::vector<wwise::Media>& game_media = {}, bool adapt_codec = kAdaptTitleCodec,
                                    bool adapt_size = kAdaptTitleSizes) {
    BankVerdict v;
    std::vector<wwise::Media> media;
    if (!wwise::media_of(bank, media)) return v;
    v.media_bank = true;
    std::vector<std::vector<wwise::Sound>> sounds(events.size());
    for (size_t e = 0; e < events.size(); ++e) wwise::sounds_of(events[e].data, sounds[e]);
    char b[256];
    for (auto m : media) {
        bool counted = false, as_game = false;
        if (!m.codec)
            for (auto& g : game_media) as_game |= g.id == m.id && g.format == m.format && m.format != 0;
        // A PCM media some sound plays: its channel field as Wwise reads it (WAVEFORMATEXTENSIBLE's dwChannelMask, 20 bytes into
        // fmt). A Windows speaker mask (a plain WAV) is rewritten to the packed config; one already packed is left; anything else
        // (no extensible fmt, a mask that does not count the channels) cannot be handled: refused.
        bool played = false, differs = false;              // differs: a sound declares another codec or size (the pack's media)
        for (auto& ss : sounds)
            for (auto& s : ss)
                if (s.media == m.id) { played = true; differs |= s.plugin != m.codec || s.size != m.size; }
        if (adapt_codec && played && m.codec == wwise::kPcm) {
            const uint32_t field = m.fmt && m.format == 0xFFFE && m.fmt_len >= 24 ? wwise::rd32(bank, m.fmt + 20) : 0;
            const bool packed = field && packed_channel_config(field, m.channels);
            const bool windows = field && !packed && field < (1u << 20) && bit_count(field) == m.channels;
            if (!packed && !windows && differs) {
                snprintf(b, sizeof(b), "media %u: PCM without a channel layout Wwise reads (fmt 0x%x, %u bytes, %u channels, field 0x%x)", m.id,
                         m.format, m.fmt_len, m.channels, field);
                v = BankVerdict{};
                v.media_bank = true;
                v.refuse = true;
                v.why = b;
                return v;
            }
            if (windows) v.fields.push_back({ m.fmt + 20, wwise_channel_config(field) });
            // what the PCM decoder may check (not read in the exe yet): logged per media, to compare if it stays silent
            if (m.fmt_len >= 16) {
                char to[32] = " (already packed)";
                if (windows) snprintf(to, sizeof(to), " -> 0x%x", wwise_channel_config(field));
                else if (!packed) snprintf(to, sizeof(to), " (left: unreadable)");
                snprintf(b, sizeof(b), "media %u: PCM %u ch, %u Hz, %u bytes/s, block %u, %u bits, channel field 0x%x%s", m.id, m.channels,
                         wwise::rd32(bank, m.fmt + 4), wwise::rd32(bank, m.fmt + 8), unsigned(bank[m.fmt + 12] | (bank[m.fmt + 13] << 8)),
                         unsigned(bank[m.fmt + 14] | (bank[m.fmt + 15] << 8)), field, to);
                v.media_info.push_back(b);
            }
        }
        for (size_t e = 0; e < events.size(); ++e)
            for (auto& s : sounds[e]) {
                if (s.media != m.id) continue;
                if (as_game) m.codec = s.plugin;          // the game's own format: its sounds name its codec
                if (s.plugin == m.codec && s.size == m.size) continue;
                if (!m.codec) snprintf(b, sizeof(b), "a format not known here (0x%x) where the game plays %s", m.format, wwise::codec_name(s.plugin).c_str());
                else if (s.stream != 0) snprintf(b, sizeof(b), "media %u is played streamed", m.id);
                else if (events[e].pack_own) snprintf(b, sizeof(b), "media %u differs from the pack's own %s", m.id, events[e].path.c_str());
                else if (!adapt_codec && m.codec != s.plugin)
                    snprintf(b, sizeof(b), "%s where the game plays %s", wwise::codec_name(m.codec).c_str(), wwise::codec_name(s.plugin).c_str());
                else if (!adapt_codec && !adapt_size) snprintf(b, sizeof(b), "%s of another size than the game's (%u bytes where it plays %u)",
                                          wwise::codec_name(m.codec).c_str(), m.size, s.size);
                else {
                    if (v.patches.empty()) { v.from = s.plugin; v.to = m.codec; }
                    v.patches.push_back({ e, s.at, m.codec, m.size });
                    if (!counted) { ++v.adapted; counted = true; }
                    continue;
                }
                v.refuse = true;
                v.why = b;
                v.patches.clear();
                v.fields.clear();
                v.adapted = 0;
                v.from = s.plugin;
                v.to = m.codec;
                return v;
            }
    }
    return v;
}

// The channel fields of a media bank's copy rewritten (BankVerdict::fields): those 4 bytes each, nothing else.
inline void apply_media_fields(std::vector<uint8_t>& media_bank, const std::vector<std::pair<size_t, uint32_t>>& fields) {
    for (auto& [at, value] : fields)
        if (at + 4 <= media_bank.size()) wwise::wr32(media_bank, at, value);
}

// The patches of one event bank applied to its copy: codec and in-memory size of the sounds named, nothing else.
inline void apply_sound_patches(std::vector<uint8_t>& event_bank, size_t bank_index, const std::vector<SoundPatch>& patches) {
    for (auto& p : patches) {
        if (p.bank != bank_index || p.at + 13 > event_bank.size()) continue;
        wwise::wr32(event_bank, p.at, p.plugin);
        wwise::wr32(event_bank, p.at + 9, p.size);
    }
}

// Registry order: by name, case-insensitive, then by key.
inline bool pack_before(const Pack& a, const Pack& b) {
    const std::string x = detail::lower(a.name), y = detail::lower(b.name);
    if (x != y) return std::lexicographical_compare(x.begin(), x.end(), y.begin(), y.end(),
                                                    [](char p, char q) { return (unsigned char)p < (unsigned char)q; });
    return a.key < b.key;
}

// What is written, given the packs in registry order, the settings and a random number (used for "random" only: one of
// the packs that have title files). Keys and "random" are compared case-insensitively.
inline Choice resolve_choice(const std::vector<Pack>& packs, const Settings& s, uint64_t random) {
    Choice c;
    const std::string want = detail::lower(detail::trim(s.pack));
    if (want == "random") {
        c.random = true;
        std::vector<int> pool;
        for (size_t i = 0; i < packs.size(); ++i) if (packs[i].title_files) pool.push_back(int(i));
        if (!pool.empty()) c.title = pool[size_t(random % pool.size())];
    } else if (!want.empty()) {
        for (size_t i = 0; i < packs.size() && c.title < 0; ++i) if (detail::lower(packs[i].key) == want) c.title = int(i);
        c.unknown = c.title < 0;
    }
    if (s.skip_intro) {
        if (c.title >= 0 && packs[size_t(c.title)].startup_files) c.startup = c.title;
        for (size_t i = 0; i < packs.size() && c.startup < 0; ++i) if (packs[i].startup_files) c.startup = int(i);
    }
    return c;
}

// What Random draws from: every background in `all` ("game" and the packs' keys) that the selection keeps; without a selection, every
// pack (the game's own title only when chosen, Wael 08/11). The selection's order does not matter; `all`'s does.
inline std::vector<std::string> random_selection(const std::vector<std::string>& all, bool has_pool, const std::vector<std::string>& pool) {
    std::vector<std::string> out;
    for (auto& k : all) {
        const bool keep = has_pool ? std::find(pool.begin(), pool.end(), k) != pool.end() : k != kGamePictureKey;
        if (keep) out.push_back(k);
    }
    return out;
}

// ---- Random without repeats (Wael 08/11): a shuffled bag ----
// The keys still to show this round, in order, and the last one shown. bag_next gives the next key and takes it out; when the bag is
// empty it is refilled with every key of `pool`, shuffled (Fisher-Yates on rnd()), the first of the new round never the last one
// shown when there are two keys or more. Keys no longer in the pool are dropped. Kept in SF6_TitleSlots_Data\shuffle.json across
// launches: the launch's draw, each return to the title and each cycle on the title screen all take from it.
struct Bag {
    std::vector<std::string> left;
    std::string last;
};
template <class Rng>
std::string bag_next(Bag& b, const std::vector<std::string>& pool, Rng&& rnd) {
    if (pool.empty()) return {};
    b.left.erase(std::remove_if(b.left.begin(), b.left.end(),
                                [&](const std::string& k) { return std::find(pool.begin(), pool.end(), k) == pool.end(); }),
                 b.left.end());
    if (b.left.empty()) {
        b.left = pool;
        for (size_t i = b.left.size(); i > 1; --i) std::swap(b.left[i - 1], b.left[size_t(rnd() % i)]);
        if (b.left.size() > 1 && b.left.front() == b.last) std::swap(b.left.front(), b.left.back());
    }
    std::string k = b.left.front();
    b.left.erase(b.left.begin());
    b.last = k;
    return k;
}
inline std::string bag_text(const Bag& b) {
    std::string out = "{\n  \"last\": \"" + b.last + "\",\n  \"left\": [";
    for (size_t i = 0; i < b.left.size(); ++i) out += (i ? ", \"" : "\"") + b.left[i] + "\"";
    return out + "]\n}\n";
}
inline Bag parse_bag(const std::string& t) {
    Bag b;
    size_t i = t.find("\"last\"");
    if (i != std::string::npos) {
        i = t.find(':', i);
        if (i != std::string::npos) { ++i; detail::skip_ws(t, i); detail::read_string(t, i, b.last); }
    }
    i = t.find("\"left\"");
    if (i != std::string::npos) i = t.find('[', i);
    if (i == std::string::npos) return b;
    ++i;
    for (;;) {
        detail::skip_ws(t, i);
        if (i >= t.size() || t[i] == ']') break;
        if (t[i] == ',') { ++i; continue; }
        std::string k;
        if (!detail::read_string(t, i, k)) break;
        if (!k.empty()) b.left.push_back(k);
    }
    return b;
}

}  // namespace title_slots
