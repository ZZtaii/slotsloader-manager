// stagetest: runs the stage loader pass on a folder laid out like the game's
// (reframework\stage_mods, reframework\data\SF6_StageSlots_Data\loader\stage_paths.txt, reframework\title_mods,
// reframework\ui_mods), without the game. Usage: stagetest <fake game dir>   (prints the variants, the title screen's
//                               backgrounds (whose copy serves each movie) and the UI packs' tree)
//        stagetest --selftest   unit tests of the tile / variation guess, stage_slots.ini, the menu picture conversion,
//                               the title packs' choice (title_slots.hpp), the UI packs' tree (ui_slots.hpp) and the
//                               loaders' cache folders (common/cache_dirs.hpp, on a scratch folder under %TEMP%)
//        stagetest --check-title-banks <game dir> <bank file>...   (read-only) the game's title event banks against these banks
//        stagetest --make-base <out.pak> <natives path>...   a stand-in for the game's re_chunk_000.pak (each path holds
//                               a few bytes of its own), for the copies of the game's own files
#include <windows.h>
#include <stdio.h>
#include <algorithm>
#include <string>
#include <vector>
#include "pak.hpp"
#include "cache_dirs.hpp"
#include "../loader/preview_tex.hpp"
#include "../loader/stage_classify.hpp"
#include "../loader/stage_loader.hpp"
#include "../loader/stage_log.hpp"
#include "../loader/title_slots.hpp"
#include "../loader/ui_slots.hpp"
#include "../loader/pic_cache.hpp"

namespace {

int g_fails = 0;
void check(bool ok, const char* what) {
    printf("  %-84s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++g_fails;
}

std::string P(const char* rel) { return std::string("natives/stm/product/") + rel; }

// The file lists of real mods (PC3, 2026-11-05), shortened to what decides.
void test_classify() {
    printf("tile / variation guess\n");
    // NULSPACE (pak): scenes only, its root prop scene among them
    StageKind k = classify_stage_variant("ess0000_00", {
        P("camera/scene/ess/ess0000_00_camera.scn.20"), P("environment/stage/scene/ess/ess0000_00/ess0000_00_env.scn.20"),
        P("level/scene/ess/ess0000_00/ess0000_00_level.scn.20"), P("light/scene/ess/ess0000_00/ess0000_00_light.scn.20") }, 0);
    check(k.tile && k.reason.find("root prop scene") != std::string::npos, "NULSPACE: tile (root prop scene)");
    // Suzaku Castle over the Training Room: arena mesh and 188 new textures
    k = classify_stage_variant("ess0000_00", { P("environment/stage/resource/ess/ess0000_00/ess0000_00.mdf2.31"),
        P("environment/stage/resource/ess/ess0000_00/ess0000_00.mesh.230110883"), P("light/scene/ess/ess0000_00/ess0000_00_light_00.scn.20") }, 188);
    check(k.tile && k.reason.find("arena mesh") != std::string::npos && k.reason.find("188 files") != std::string::npos,
          "Suzaku Castle: tile (arena mesh, 188 new files)");
    // Training Void: the _00 mesh and collision
    k = classify_stage_variant("ess0000_00", { P("environment/stage/resource/ess/ess0000_00/ess0000_00_00.mesh.230110883"),
        P("environment/stage/resource/ess/ess0000_00/ess0000_00e.mcol.20021") }, 2);
    check(k.tile && k.reason.find("collision") != std::string::npos, "Training Void: tile (arena mesh, collision)");
    // Stage Lighting Overhaul, Training Room Red: camera, light, material, albedo
    k = classify_stage_variant("ess0000_00", { P("camera/scene/ess/ess0000_00_camera.scn.20"),
        P("environment/stage/resource/ess/ess0000_00/ess0000_00.mdf2.31"), P("environment/stage/resource/ess/ess0000_00/ess0000_00_albd.tex.241101895"),
        P("light/scene/ess/ess0000_00/ess0000_00_light_00.scn.20") }, 0);
    check(!k.tile, "Training Room Red (lighting): variation");
    // Stage Lighting Overhaul, Suval'hal Arena: a level scene and env_props_dynamic are not a new stage
    k = classify_stage_variant("ess0101_00", { P("environment/stage/scene/ess/ess0101_00/ess0101_00_env_props_dynamic.scn.20"),
        P("level/scene/ess/ess0101_00/ess0101_00_level.scn.20"), P("light/scene/ess/ess0101_00/ess0101_00_light_00.scn.20") }, 0);
    check(!k.tile, "Suval'hal Arena (lighting, level scene): variation");
    // Dhalsimer Temple With Banner: a sub-mesh and 12 new textures
    k = classify_stage_variant("ess0700_00", { P("environment/stage/resource/ess/ess0700_00/ess0700_00_03.mdf2.31"),
        P("environment/stage/resource/ess/ess0700_00/ess0700_00_03.mesh.230110883") }, 12);
    check(!k.tile, "Dhalsimer Temple With Banner (sub-mesh, 12 new files): variation");
    // Genbu Temple Night Time: a light scene and a mesh at a path of its own
    k = classify_stage_variant("ess0200_00", { P("environment/stage/resource/ess/ess0200_00/ess0000_00.mesh.230110883"),
        P("light/scene/ess/ess0200_00/ess0200_00_light_00.scn.20") }, 1);
    check(!k.tile, "Genbu Temple Night Time (light, mesh of another name): variation");
    // case and natives prefix do not matter
    k = classify_stage_variant("ESS0000_00", { "natives/STM/Product/Environment/Stage/Resource/ESS/ess0000_00/ESS0000_00.MESH.230110883" }, 0);
    check(k.tile, "upper-case path: still the arena mesh");
    k = classify_stage_variant("ess0000_00", {}, 25);
    check(k.tile, "a pak with 25 unknown files: tile");
}

void test_overrides() {
    printf("stage_slots.ini\n");
    std::vector<std::string> bad;
    auto list = parse_stage_overrides("\xEF\xBB\xBF; comment\n[SIRN Training Room]\nkind = Variation\n\n[Stage Lighting Overhaul-3258-1-0-1774283942.rar]\n"
                                      "kind=tile\n[Training Room Red]\nkind=variation\nname = Red Room\nfoo=bar\nkind=maybe\n# x\n[]\nloose\n", &bad);
    check(list.size() == 3, "three sections (an empty section name is refused)");
    check(list[0].kind == "variation" && list[0].name.empty(), "kind is case-insensitive, spaces trimmed");
    check(list[2].name == "Red Room" && list[2].kind == "variation", "name= kept as written");
    check(bad.size() == 4, "unknown key, bad kind value, empty section, a line outside a section: reported");
    const StageOverride* o = find_stage_override(list, "scbacd1e2e2cf", "SIRN Training Room", "SIRN Training Room.rar/SIRN Training Room");
    check(o == &list[0], "found by the mod's name");
    o = find_stage_override(list, "sd6f5bdab19f0", "Training Room Red", "Stage Lighting Overhaul-3258-1-0-1774283942.rar/Training Room Red");
    check(o == &list[2], "the last matching section wins (a pack, then one of its options)");
    o = find_stage_override(list, "s1ec9cc1c9515", "Training Room Green", "Stage Lighting Overhaul-3258-1-0-1774283942.rar/Training Room Green");
    check(o == &list[1], "found by the archive's name");
    auto l2 = parse_stage_overrides("[Stage Lighting Overhaul-3258-1-0-1774283942]\nkind=tile\n[s800c479d6d25]\nname=Null\n");
    check(find_stage_override(l2, "x", "y", "Stage Lighting Overhaul-3258-1-0-1774283942.rar/Fete Foraine") == &l2[0],
          "found by the archive's name without its extension");
    check(find_stage_override(l2, "s800c479d6d25", "NULSPACE", "NULSPACE-3564-1-0-1781025053.zip/NULSPACE") == &l2[1], "found by the key");
    check(find_stage_override(l2, "s1", "Other", "Other.zip/Other") == nullptr, "no section: the guess stays");
    check(parse_stage_overrides(kStageOverridesTemplate).empty(), "the template is all comments");
}

// A game texture of a solid colour (RGBA8, or BC1 built by our own encoder) converted to our preview and thumbnail.
void test_pictures() {
    printf("menu pictures\n");
    auto rgba_tex = [](int w, int h, uint8_t r, uint8_t g, uint8_t b) {
        std::vector<uint8_t> t(56 + (size_t)w * h * 4, 0);
        memcpy(t.data(), "TEX\0", 4);
        int32_t ver = 241101895, fmt = 28, pitch = w * 4, size = w * h * 4;
        int64_t off = 56;
        memcpy(t.data() + 4, &ver, 4);
        uint16_t W = (uint16_t)w, H = (uint16_t)h;
        memcpy(t.data() + 8, &W, 2);
        memcpy(t.data() + 10, &H, 2);
        t[14] = 1; t[15] = 16;
        memcpy(t.data() + 16, &fmt, 4);
        memcpy(t.data() + 40, &off, 8);
        memcpy(t.data() + 48, &pitch, 4);
        memcpy(t.data() + 52, &size, 4);
        for (size_t i = 0; i < (size_t)w * h; ++i) { t[56 + i * 4] = r; t[57 + i * 4] = g; t[58 + i * 4] = b; t[59 + i * 4] = 255; }
        return t;
    };
    auto dims = [](const std::vector<uint8_t>& t, int& w, int& h, int& fmt) {
        w = h = fmt = 0;
        if (t.size() < 56) return;
        uint16_t W, H;
        memcpy(&W, t.data() + 8, 2); memcpy(&H, t.data() + 10, 2); memcpy(&fmt, t.data() + 16, 4);
        w = W; h = H;
    };
    std::vector<uint8_t> out, rgba;
    std::string err;
    int w, h, fmt;
    check(make_stage_tex_from_game_tex(rgba_tex(1024, 256, 200, 40, 40), false, out, err), "RGBA8 4:1 picture -> preview");
    dims(out, w, h, fmt);
    check(w == 2048 && h == 512 && fmt == 72 && out.size() == 56 + 524288, "preview: 2048x512 BC1 sRGB, one level (the vanilla layout)");
    check(decode_game_tex_rgba(out, rgba, w, h, err) && w == 2048 && h == 512 && rgba[0] > 150 && rgba[1] < 90,
          "our BC1 decodes back to the colour");
    std::vector<uint8_t> bc1;
    check(make_stage_tex_from_game_tex(rgba_tex(128, 256, 10, 200, 10), true, bc1, err), "RGBA8 1:2 picture -> thumbnail");
    dims(bc1, w, h, fmt);
    check(w == 128 && h == 256 && fmt == 72, "thumbnail: 128x256 BC1");
    check(make_stage_tex_from_game_tex(rgba_tex(1920, 1080, 1, 2, 3), true, bc1, err), "16:9 picture -> thumbnail (cut like a screenshot)");
    std::vector<uint8_t> broken = rgba_tex(64, 64, 1, 1, 1);
    int64_t beyond = 1 << 20;
    memcpy(broken.data() + 40, &beyond, 8);
    check(!make_stage_tex_from_game_tex(broken, false, out, err) && err.find("not in the file") != std::string::npos,
          "a streaming base file (mip 0 outside the file) is refused");
    std::vector<uint8_t> odd = rgba_tex(64, 64, 1, 1, 1);
    int32_t bc6 = 95;
    memcpy(odd.data() + 16, &bc6, 4);
    check(!make_stage_tex_from_game_tex(odd, false, out, err) && err.find("unsupported") != std::string::npos, "BC6H is refused");
    check(!make_stage_tex_from_game_tex(std::vector<uint8_t>(10, 0), false, out, err), "garbage is refused");
}

// Title packs: the split of a pack's files, settings.json, the keys, the order and the choice.
void test_titles() {
    printf("title packs\n");
    using namespace title_slots;
    const char* part = "natives/stm/streaming/product/gui/gm/ui10000/part/";
    check(is_title_file(std::string(part) + "title_start.mov.1") && is_title_file(std::string(part) + "title_loop.mov.1"),
          "title_start / title_loop movies: title files");
    check(is_title_file("NATIVES/STM/Streaming/Product/GUI/GM/UI10000/Part/Title_End.mov.1"), "any case: still a title file");
    check(!is_title_file("natives/stm/product/gui/gm/ui10000/ui10001.gui.530035") &&
          !is_title_file("natives/stm/product/gui/gm/ui10000/part/re logo animation.mov.1.x64") &&
          !is_title_file("natives/stm/product/gui/gm/ui10000/part/tex_title_logo.tex.241101895") &&
          !is_title_file("natives/stm/product/sound/wwise/re_logo.sbnk.1.x64"),
          "boot screens, logo textures (even tex_title_*), re_logo.sbnk: startup files");
    const std::string music = "natives/stm/product/sound/wwise/ui_title_m.sbnk.1.x64",
                      narrator = "natives/stm/product/sound/wwise/narration_title_m.sbnk.1.x64";
    check(is_title_file(music) && is_title_file(narrator) && is_title_file("natives/stm/product/sound/wwise/ui_title_es.sbnk.1.x64") &&
          is_title_file("NATIVES/STM/PRODUCT/SOUND/WWISE/Narration_Title_ES.SBNK.1.X64"),
          "the title's sound banks (music, narrator, their event banks; any case): title files");
    check(is_title_sound(music) && !is_title_movie(music) && is_title_movie(std::string(part) + "title_end.mov.1") &&
          !is_title_sound(std::string(part) + "title_end.mov.1"), "a bank is a title sound, not a title movie, and the other way round");
    check(!is_title_file("natives/stm/product/sound/wwise/ui_startmenu_cmn_m.sbnk.1.x64") &&
          !is_title_file("natives/stm/streaming/product/sound/wwise/bgm_ac01001.spck.1.x64") &&
          !is_title_file("natives/stm/product/sound/wwise/title/ui_x.sbnk.1.x64") &&
          !is_title_file("natives/stm/product/sound/resource/snd_container/cnt_ui/snd_cnt_ui_title.user.2"),
          "other banks, a package, a bank in a sub folder, the title's sound container: not title files");
    check(private_title_path("t0123456789ab", music) == "natives/stm/_titleslots/t0123456789ab/product/sound/wwise/ui_title_m.sbnk.1.x64" &&
          resource_file_of(music).empty(), "a bank's copy: under natives/stm/_titleslots/<key>/, no resource file (not streamed)");
    {
        size_t banks = 0, movies = 0;
        for (auto& p : known_paths()) { banks += is_title_sound(p); movies += is_title_movie(p); }
        check(banks == 4 && movies == 3 && !is_title_file("natives/stm/product/sound/wwise/re_logo.sbnk.1.x64"),
              "known paths: the 3 movies and the 4 title banks are title files (a .pak pack's banks are named)");
    }

    Settings st = parse_settings("\xEF\xBB\xBF{\n    \"display_nsfw\": false,\n    \"online_outfit\": \"Outfit 1\",\n"
                                 "    \"nested\": { \"title_pack\": \"no\", \"a\": [1, {\"b\": \"}\"}] },\n"
                                 "    \"esc\": \"a \\\"quoted\\\" }, value\",\n    \"title_pack\": \"t0123456789ab\",\n"
                                 "    \"skip_intro\": true,\n    \"n\": -1.5e3\n}\n");
    check(st.pack == "t0123456789ab" && st.skip_intro, "settings.json: both keys read past other keys, nested values, escapes");
    st = parse_settings("{\"select_options_key\": \"C\"}");
    check(st.pack.empty() && !st.skip_intro, "keys missing: the game's own title, intro kept");
    st = parse_settings("");
    check(st.pack.empty() && !st.skip_intro, "empty file: the defaults");
    st = parse_settings("not json {\"title_pack\": \"random\"}");
    check(st.pack.empty(), "not an object: the defaults");
    st = parse_settings("{\"title_pack\": 3, \"skip_intro\": \"yes\"}");
    check(st.pack.empty() && !st.skip_intro, "keys of another type: their defaults");
    st = parse_settings("{\"title_pack\": \"random\", \"skip_intro\": tr");
    check(st.pack == "random" && !st.skip_intro, "a text cut short keeps what came before");
    st = parse_settings("{\"skip_intro\":true,\"title_pack\":\"\\u0074abc\"}");
    check(st.pack == "tabc" && st.skip_intro, "no spaces, \\u escape");

    check(pack_key("a") == "taf63dc4c8601", "key: \"t\" + 12 hex digits of FNV-1a 64 (test vector of \"a\")");
    check(pack_key("Ryu Title.zip") == pack_key("RYU TITLE.ZIP") && pack_key("Ryu Title.zip") != pack_key("Ken Title.zip"),
          "key: case-insensitive, one per source");
    check(pack_key("Pack.zip/Option A") != pack_key("Pack.zip/Option B"), "key: one per option of a bundle");

    auto mk = [](const char* name, size_t title, size_t startup) {
        Pack p;
        p.name = name;
        p.source = std::string(name) + ".zip";
        p.key = pack_key(p.source);
        p.title_files = title;
        p.startup_files = startup;
        return p;
    };
    std::vector<Pack> packs = { mk("beta", 3, 0), mk("Alpha", 3, 6), mk("alpha 2", 2, 0), mk("Logos only", 0, 6) };
    std::stable_sort(packs.begin(), packs.end(), pack_before);
    check(packs[0].name == "Alpha" && packs[1].name == "alpha 2" && packs[2].name == "beta" && packs[3].name == "Logos only",
          "registry order: by name, case-insensitive");
    Settings s;
    Choice c = resolve_choice(packs, s, 0);
    check(c.title < 0 && c.startup < 0 && !c.random && !c.unknown, "nothing set: the game's own title, nothing written");
    s.skip_intro = true;
    c = resolve_choice(packs, s, 0);
    check(c.title < 0 && c.startup == 0, "own title + skip_intro: the first pack's startup files");
    s.pack = packs[2].key;
    c = resolve_choice(packs, s, 0);
    check(c.title == 2 && c.startup == 0, "a pack without startup files + skip_intro: the first pack's that has some");
    s.pack = packs[1].key;
    s.skip_intro = false;
    c = resolve_choice(packs, s, 0);
    check(c.title == 1 && c.startup < 0, "a pack's key: its title files only");
    s.pack = " " + std::string("T") + packs[0].key.substr(1) + " ";
    s.skip_intro = true;
    c = resolve_choice(packs, s, 0);
    check(c.title == 0 && c.startup == 0, "a key in upper case, with spaces: found; skip_intro: its own startup files");
    s.pack = "t000000000000";
    c = resolve_choice(packs, s, 0);
    check(c.title < 0 && c.unknown && c.startup == 0, "an unknown key: the game's own title (reported)");
    s.pack = "Random";
    s.skip_intro = false;
    bool drawn[4] = {};
    for (uint64_t r = 0; r < 12; ++r) {
        c = resolve_choice(packs, s, r);
        if (c.title >= 0) drawn[c.title] = true;
        if (!c.random || c.title < 0) drawn[3] = true;
    }
    check(drawn[0] && drawn[1] && drawn[2] && !drawn[3], "random: every pack with title files can be drawn, never one without");
    c = resolve_choice({}, s, 7);
    check(c.random && c.title < 0 && c.startup < 0, "random without packs: the game's own title");

    // Random's mosaic: the layout with the largest 16:9 places, then the fewest empty ones, then the most columns
    auto lay_is = [](int n, int cols, int rows) { const MosaicLayout l = title_mosaic_layout(n); return l.cols == cols && l.rows == rows; };
    check(lay_is(1, 1, 1) && title_mosaic_layout(1).place_w == 1920, "mosaic: 1 picture, the whole screen");
    check(lay_is(2, 2, 1) && lay_is(3, 2, 2) && lay_is(4, 2, 2), "mosaic: 2 side by side, 3 and 4 on 2 x 2");
    check(lay_is(5, 3, 2) && lay_is(6, 3, 2), "mosaic: 5 and 6 on 3 columns x 2 rows");
    check(lay_is(7, 3, 3) && lay_is(8, 3, 3) && lay_is(9, 3, 3) && lay_is(10, 4, 3), "mosaic: 7 to 9 on 3 x 3, 10 on 4 x 3");
    bool fits = true;
    for (int n = 1; n <= 40; ++n) {
        const MosaicLayout l = title_mosaic_layout(n);
        fits = fits && l.cols * l.rows >= n && l.cols * l.place_w <= 1920 && l.rows * l.place_h <= 1080 && l.place_h * 16 == l.place_w * 9 &&
               l.cell_w == l.place_w - 2 * TITLE_MOSAIC_GAP;
    }
    check(fits, "mosaic: 1 to 40 pictures, every place 16:9, all of them on the screen");

    // Random's bag: every key once per round, never twice in a row across rounds, dropped keys gone, kept through its text
    const std::vector<std::string> keys = { "ta", "tb", "tc", "td", "te" };
    Bag bag;
    uint32_t seed = 12345;
    auto rnd = [&] { seed = seed * 1664525u + 1013904223u; return seed >> 8; };
    bool rounds_ok = true, no_repeat = true;
    std::string prev;
    for (int round = 0; round < 6; ++round) {
        std::vector<std::string> seen;
        for (size_t i = 0; i < keys.size(); ++i) {
            const std::string k = bag_next(bag, keys, rnd);
            no_repeat = no_repeat && k != prev;
            prev = k;
            seen.push_back(k);
            if (round == 3 && i == 1) bag = parse_bag(bag_text(bag));   // a new launch in the middle of a round
        }
        std::sort(seen.begin(), seen.end());
        rounds_ok = rounds_ok && seen == keys;
    }
    check(rounds_ok && no_repeat, "random bag: each round shows every pack once, never the same twice in a row, through a relaunch");
    Bag b2;
    b2.left = { "ta", "tz", "tb" };
    const std::vector<std::string> pool2 = { "ta", "tb" };
    const std::string k1 = bag_next(b2, pool2, rnd), k2 = bag_next(b2, pool2, rnd);
    check(k1 == "ta" && k2 == "tb" && b2.left.empty(), "random bag: a pack removed from title_mods leaves the bag");
}

// UI packs: modinfo.ini, the keys and paths, Fluffy's tree (shortened from FG Artwork 2.05, Comic Art 2.05a and the Loading
// Animations, 10/11), the winners, what is served, settings.json's ui_choices, the registry, the plugin's overrides.
void test_ui() {
    printf("UI packs\n");
    using namespace ui_slots;

    ModInfo m = parse_modinfo("\xEF\xBB\xBFname= Dhalsim Token Loading \r\nAuthor = BrewedVFX\r\ndescription=Replaces Ryu\x92s loading screen\r\n"
                              "category=!Characters > Multiple\r\ncategory=BattleHUD\r\nDummyMod= True\r\nMenuPriority=8\r\nAddonFor= XX) Other\r\n; name=no\r\n");
    check(m.name == "Dhalsim Token Loading" && m.author == "BrewedVFX" && m.addon_for == "XX) Other" && m.dummy && m.priority == 8,
          "modinfo: keys in any case, spaces round keys and values trimmed");
    check(m.categories.size() == 2 && m.categories[1] == "BattleHUD", "modinfo: category repeats");
    check(m.description == "Replaces Ryu\xE2\x80\x99s loading screen", "modinfo: Windows-1252 text made UTF-8 (typographic apostrophe)");
    check(to_utf8("Caf\xC3\xA9 \xE2\x80\x99") == "Caf\xC3\xA9 \xE2\x80\x99", "modinfo: UTF-8 text kept as it is");
    m = parse_modinfo("name=A\nDummyMod=false\nMenuPriority=-2\n");
    check(!m.dummy && m.priority == -2, "modinfo: DummyMod=false, a negative MenuPriority");

    check(node_id("a") == "naf63dc4c8601" && pack_key("a") == "uaf63dc4c8601", "keys: \"n\" / \"u\" + 12 hex digits of FNV-1a 64 (\"a\")");
    check(node_id("FG.zip/FG Artwork 2.05/001) FG Default") == node_id("fg.zip/fg artwork 2.05/001) fg default"), "keys: any case");
    check(node_id("FG.zip/A") != node_id("FG.zip/B") && node_id("FG.zip/A") != node_id("FG (1).zip/A"), "keys: one per source and node folder");
    check(private_path("u1", "n2", "natives/stm/product/gui/x.tex.241101895") == "natives/stm/_uislots/u1/n2/product/gui/x.tex.241101895" &&
          private_path("u1", "n2", "natives/stm/streaming/product/gui/x.tex.241101895") == "natives/stm/streaming/_uislots/u1/n2/product/gui/x.tex.241101895",
          "private paths, a streamed file's under streaming/");
    check(game_path("natives/STM/product/GUI/x.tex.241101895") == "natives/stm/_uislots/game/product/GUI/x.tex.241101895", "the game's own copy, the case kept");
    check(texture_resource("natives/stm/product/GUI/cmn/ui01900/part/tex_loading_IAM.tex.241101895") == "product/GUI/cmn/ui01900/part/tex_loading_IAM.tex" &&
          texture_resource("natives/STM/product/a/b.tex.241101895.X64") == "product/a/b.tex",
          "a texture's resource path: no natives/stm/, no version or platform suffix, the case kept");
    check(texture_resource("natives/stm/streaming/product/a/b.tex.241101895").empty() && texture_resource("natives/stm/product/a/b.pfb.17").empty() &&
          texture_resource("natives/stm/product/a.tex.d/uvs_loading.uvs.7").empty(), "not a texture, or its streamed half: no resource path");

    auto F = [](const char* path, const char* ini, std::vector<uint64_t> files) {
        Found f;
        f.path = path;
        f.info = parse_modinfo(ini);
        f.files = std::move(files);
        return f;
    };
    const uint64_t A1 = 101, A2 = 102, A3 = 103, CT = 109;   // FG's Arcade, Practice, Versus banners, the controller icon
    std::vector<Found> in = {
        F("FG.zip/FG/000) FG Artwork", "name= FG Artwork\nDummyMod=True\n", {}),
        F("FG.zip/FG/001) FG Default", "name= Default Set\nAddonFor= FG Artwork\n", { A1, A2, A3, CT }),
        F("FG.zip/FG/020) FG Optional", "name= FG Optional\nAddonFor= FG Artwork\nDummyMod=True\n", {}),
        F("FG.zip/FG/023) Arcade", "name= 1) Arcade\nAddonFor= FG Optional\nDummyMod=True\n", {}),
        F("FG.zip/FG/024) Practice", "name= 2) Practice\nAddonFor= FG Optional\nDummyMod=True\n", {}),
        F("FG.zip/FG/Comic FG ttt Luke", "name=Luke\nAddonFor= 1) Arcade\nMenuPriority=7\n", { A1 }),
        F("FG.zip/FG/Comic FG ttt Ryu", "name=Ryu Story\nAddonFor= 1) Arcade\nMenuPriority=8\n", { A1 }),
        F("FG.zip/FG/Comic FG ttt Dark", "name=Dark Reign\nAddonFor= 1) Arcade\nMenuPriority=9\n", { A1 }),
        F("FG.zip/FG/Comic FG ttt Art", "name=Illustration Contest 2nd\nAddonFor= 1) Arcade\nMenuPriority=9\n", { A1 }),
        F("FG.zip/FG/Comic FG uuu Zen", "name=Ryu Zen\nAddonFor= 2) Practice\n", { A2 }),
        F("FG.zip/FG/Comic FG uuu Peace", "name=Peace Out\nAddonFor= 2) Practice\n", { A2 }),
        F("Extra.rar/Arcade Akuma", "name=Akuma\nAddonFor=1) ARCADE\n", { A1 }),    // another archive, the name in another case
        F("Comic.rar/Comic Art Bundle/Comic Art", "name=Comic Art Portraits (Arjun Update)\nDummyMod= True\nauthor=brewedVFX\n", {}),
        F("Comic.rar/Comic Art Bundle/Remove Cart", "name=Remove Cart\nMenuPriority=0\nAddonFor=Comic Art Portraits (Arjun Update)\n", { 201 }),
        F("Comic.rar/Comic Art Bundle/BattleHud", "name=BattleHud & Battlehub\nMenuPriority=8\nAddonFor=Comic Art Portraits (Arjun Update)\n", { 202, 203 }),
        F("Comic.rar/Comic Art Bundle/Select", "name=Character Select\nMenuPriority=9\nAddonFor=Comic Art Portraits (Arjun Update)\n", { 204 }),
        F("Dhalsim Loading.zip/Dalsim Loading", "name= Dhalsim Token Loading\nAddonFor= XX) Other\ncategory=!Other > Misc\n", { 301 }),
        F("Chun Loading.zip/ChunLi Loading", "name=Chun-Li Token Loading\n", { 301 }),
        F("Ingrid 8.zip/Ingrid Loading", "name=Ingrid Loading\n", { 301, 302 }),
        F("Ingrid.zip/Ingrid Loading", "name=Ingrid Loading\n", { 301 }),
    };
    std::vector<std::string> notes;
    const std::vector<Pack> packs = build_packs(in, &notes);
    auto pack_named = [&](const std::string& n) -> const Pack* { for (auto& p : packs) if (p.name == n) return &p; return nullptr; };
    auto node_in = [](const Pack* p, const std::string& n) -> const Node* {
        if (p) for (auto& x : p->nodes) if (x.name == n) return &x;
        static const Node none;
        return &none;
    };
    const Pack* fg = pack_named("FG Artwork");
    const Pack* comic = pack_named("Comic Art Portraits (Arjun Update)");
    const Pack* loading = pack_named("Loading");
    check(packs.size() == 3 && comic == &packs[0] && fg == &packs[1] && loading == &packs[2], "three packs, by name: Comic Art, FG Artwork, the Loadings gathered");
    std::string order;
    if (fg) for (auto& nd : fg->nodes) order += nd.name + ":" + std::to_string(nd.depth) + ";";
    check(order == "FG Artwork:0;Default Set:1;FG Optional:1;1) Arcade:2;Dark Reign:3;Illustration Contest 2nd:3;Ryu Story:3;Luke:3;Akuma:3;"
                   "2) Practice:2;Peace Out:3;Ryu Zen:3;",
          "display order: depth first, options by MenuPriority then name before sub groups by name");
    const Node* arcade = node_in(fg, "1) Arcade");
    check(node_in(fg, "Akuma")->parent == arcade->id && node_in(fg, "Akuma")->depth == 3, "AddonFor across archives, any case: under 1) Arcade");
    bool arcade_one = true;
    for (const char* n : { "Dark Reign", "Illustration Contest 2nd", "Ryu Story", "Luke", "Akuma" })
        arcade_one = arcade_one && node_in(fg, n)->one && node_in(fg, n)->group == arcade->id;
    check(arcade_one && node_in(fg, "Peace Out")->one && node_in(fg, "Peace Out")->group == node_in(fg, "2) Practice")->id,
          "options of a parent that write the same file: one-of groups, their parent's id");
    check(!node_in(fg, "Default Set")->one && !node_in(fg, "Default Set")->title && node_in(fg, "FG Optional")->title && arcade->title,
          "the preset (Default Set) is a box; DummyMods are titles");
    order.clear();
    if (comic) for (auto& nd : comic->nodes) order += nd.name + (nd.title ? "#" : nd.one ? "(o)" : "[]") + ";";
    check(order == "Comic Art Portraits (Arjun Update)#;Character Select[];BattleHud & Battlehub[];Remove Cart[];", "add-ons that share nothing: boxes, by MenuPriority");
    bool gathered = loading && loading->gathered && loading->nodes.size() == 4 && loading->key == pack_key("Chun Loading.zip/ChunLi Loading");
    if (loading) for (auto& nd : loading->nodes) gathered = gathered && nd.depth == 0 && nd.parent.empty() && nd.one && nd.group == loading->key;
    check(gathered, "roots that write the same file (an unknown AddonFor among them): one pack, a one-of group keyed by the pack");
    check(std::any_of(notes.begin(), notes.end(), [](const std::string& s) { return s.find("XX) Other") != std::string::npos; }),
          "an AddonFor that names no node is reported");

    // the same archives downloaded again (other contents, other order): the same keys
    std::vector<Found> again(in.rbegin(), in.rend());
    for (auto& f : again) { for (auto& h : f.files) h += 1000; f.info.description = "v2"; }
    const std::vector<Pack> packs2 = build_packs(again);
    bool same = packs2.size() == packs.size();
    for (size_t i = 0; same && i < packs.size(); ++i) {
        same = packs2[i].key == packs[i].key && packs2[i].nodes.size() == packs[i].nodes.size();
        for (size_t k = 0; same && k < packs[i].nodes.size(); ++k)
            same = packs2[i].nodes[k].id == packs[i].nodes[k].id && packs2[i].nodes[k].group == packs[i].nodes[k].group;
    }
    check(same, "a new download of the same files: the same pack keys, node ids and groups");

    // a name in two archives: the parent in the same archive first, else the first by path
    const std::vector<Pack> dup = build_packs({
        F("A.zip/Root", "name=Root\nDummyMod=True\n", {}), F("A.zip/Opt", "name=Optional\nAddonFor=Root\nDummyMod=True\n", {}),
        F("B.zip/Root", "name=Root\nDummyMod=True\n", {}), F("B.zip/Opt", "name=Optional\nAddonFor=Root\nDummyMod=True\n", {}),
        F("B.zip/X", "name=X\nAddonFor=Optional\n", { 1 }), F("A.zip/Y", "name=Y\nAddonFor=Optional\n", { 2 }),
        F("C.zip/Z", "name=Z\nAddonFor=Optional\n", { 3 }) });
    auto parent_of = [](const std::vector<Pack>& ps, const std::string& n) {
        for (auto& p : ps) for (auto& x : p.nodes) if (x.name == n) return x.parent;
        return std::string("?");
    };
    check(dup.size() == 2 && parent_of(dup, "X") == node_id("B.zip/Opt") && parent_of(dup, "Y") == node_id("A.zip/Opt") &&
          parent_of(dup, "Z") == node_id("A.zip/Opt"), "a name in two archives: the same archive's node, else the first");

    // a loop of AddonFor, AddonFor naming itself, a DummyMod with files, a pack with nothing to tick
    notes.clear();
    const std::vector<Pack> odd = build_packs({
        F("L.zip/P", "name=P\nAddonFor=Q\n", { 1 }), F("L.zip/Q", "name=Q\nAddonFor=P\n", { 2 }),
        F("M.zip/T", "name=Only a title\nDummyMod=True\n", { 5 }), F("N.zip/S", "name=Self\nAddonFor=Self\n", { 6 }) }, &notes);
    check(odd.size() == 2 && odd[0].name == "P" && odd[0].nodes.size() == 2 && odd[0].nodes[1].parent == odd[0].nodes[0].id && odd[1].name == "Self",
          "a loop of AddonFor is cut, AddonFor naming itself is a root");
    check(notes.size() == 4, "reported: the loop, the name that is no node, the DummyMod's files, the pack with nothing to tick");

    // two one-of groups under one parent (one of them transitive), and a box
    const std::vector<Pack> two = build_packs({
        F("G.zip/R", "name=R\nDummyMod=True\n", {}),
        F("G.zip/a1", "name=a1\nAddonFor=R\n", { 1 }), F("G.zip/a2", "name=a2\nAddonFor=R\n", { 1 }),
        F("G.zip/b1", "name=b1\nAddonFor=R\n", { 2 }), F("G.zip/b2", "name=b2\nAddonFor=R\n", { 2, 3 }), F("G.zip/b3", "name=b3\nAddonFor=R\n", { 3 }),
        F("G.zip/c", "name=c\nAddonFor=R\n", { 4 }) });
    bool groups = two.size() == 1 && two[0].nodes.size() == 7;
    if (groups) {
        auto& v = two[0].nodes;
        groups = v[1].group == v[0].id && v[2].group == v[0].id && v[3].group == v[3].id && v[4].group == v[3].id && v[5].group == v[3].id &&
                 !v[6].one && v[6].group.empty();
    }
    check(groups, "two one-of groups under one parent (files shared transitively): the parent's id, then the first member's");

    auto gname = [&](std::vector<Found> v) { const auto p = build_packs(v); return p.size() == 1 ? p[0].name : std::string("?"); };
    check(gname({ F("x.zip/a", "name=Comic Art Portraits - Ingrid\n", { 1 }), F("y.zip/b", "name=Comic Art Portraits - Yasmine\n", { 1 }) }) == "Comic Art Portraits",
          "gathered pack's name: the words the names start with");
    check(gname({ F("x.zip/a", "name=Alpha\ncategory=!Other > Misc\n", { 1 }), F("y.zip/b", "name=Beta\ncategory=Foo\ncategory=!Other > Misc\n", { 1 }) }) == "Misc",
          "gathered pack's name: else a category they all have");
    check(gname({ F("y.zip/b", "name=Beta\n", { 1 }), F("x.zip/a", "name=Alpha\n", { 1 }) }) == "Alpha", "gathered pack's name: else the first name");

    // what each option serves: real hash -> a copy of its own (fake hashes)
    auto copy_of = [](size_t order_in_pack, uint64_t real) { return real * 1000 + order_in_pack; };
    auto served_of = [&](const std::vector<Pack>& ps, const std::vector<Found>& found) {
        std::vector<Served> out;
        for (size_t pi = 0; pi < ps.size(); ++pi) {
            for (size_t k = 0; k < ps[pi].nodes.size(); ++k) {
                const Node& nd = ps[pi].nodes[k];
                if (nd.title) continue;
                Served s{ ps[pi].key, nd.id, nd.depth, int(k), {}, int(pi) };
                for (uint64_t h : found[nd.found].files) s.files.push_back({ h, copy_of(k, h) });
                std::sort(s.files.begin(), s.files.end());
                out.push_back(std::move(s));
            }
        }
        return out;
    };
    const std::vector<Served> opts = served_of(packs, in);
    auto at = [&](const Pack* p, const char* name) { for (size_t k = 0; p && k < p->nodes.size(); ++k) if (p->nodes[k].name == name) return k; return size_t(0); };
    auto id = [&](const Pack* p, const char* name) { return node_in(p, name)->id; };
    auto get = [](const HashMap& w, uint64_t h) { for (auto& e : w) if (e.first == h) return e.second; return uint64_t(0); };
    const std::string fgk = fg ? fg->key : std::string(), ck = comic ? comic->key : std::string();
    Choices launch;
    launch[fgk] = { id(fg, "Default Set"), id(fg, "Dark Reign") };
    const HashMap w = winners(opts, launch);
    check(w.size() == 4 && get(w, A1) == copy_of(at(fg, "Dark Reign"), A1) && get(w, A2) == copy_of(at(fg, "Default Set"), A2) &&
          get(w, CT) == copy_of(at(fg, "Default Set"), CT), "the deepest ticked option wins a file (Dark Reign over the Default Set's banner)");
    Choices both;
    both[fgk] = { id(fg, "Dark Reign"), id(fg, "Luke") };
    check(get(winners(opts, both), A1) == copy_of(at(fg, "Luke"), A1), "at the same depth, the later in display order");
    check(winners(opts, {}).empty(), "nothing ticked: nothing");
    Choices bad;
    bad["u000000000000"] = { id(fg, "Dark Reign") };
    bad[fgk] = { "n000000000000", arcade->id };
    check(winners(opts, bad).empty() && applied(packs, bad).empty(), "an unknown pack, an unknown id, a title: nothing ticked");
    Choices mixed;
    mixed[fgk] = { id(fg, "Dark Reign"), "nzz", id(fg, "Default Set") };
    check(applied(packs, mixed)[fgk] == std::vector<std::string>{ id(fg, "Default Set"), id(fg, "Dark Reign") }, "applied: the options named, in display order");

    const HashMap written = w;                              // the launch wrote its winners at the real paths
    const HashMap game = { { A1, 9001 }, { A2, 9002 }, { CT, 9009 } };   // the game's own copies (none of A3: not in its pak)
    check(serve(opts, launch, written, game).empty(), "the launch's own choices: nothing served (the real paths hold them)");
    const HashMap none = serve(opts, {}, written, game);
    check(none == HashMap{ { A1, 9001 }, { A2, 9002 }, { CT, 9009 } }, "nothing ticked any more: the game's own copies (none where the game has no copy)");
    Choices other;
    other[fgk] = { id(fg, "Default Set"), id(fg, "Ryu Story") };
    check(serve(opts, other, written, game) == HashMap{ { A1, copy_of(at(fg, "Ryu Story"), A1) } }, "another option of the group: its copy, for that file only");
    Choices more = launch;
    more[ck] = { id(comic, "Character Select") };
    check(serve(opts, more, written, game) == HashMap{ { 204, copy_of(at(comic, "Character Select"), 204) } }, "a file the launch did not write, ticked now: its copy");

    const std::map<uint64_t, std::string> names = { { A1, "product/gui/a1.tex" }, { 9001, "_uislots/game/product/gui/a1.tex" }, { A2, "product/gui/a2.tex" } };
    check(overrides_text(none, names) == "product/gui/a1.tex\t_uislots/game/product/gui/a1.tex\n" && overrides_text({}, names).empty(),
          "overrides: one line per texture served, the real resource and the one shown instead");

    // roots gathered only when they are alternatives: the Jaccard index of their trees' files 1/2 or more (in game 10/11, sharing
    // any file put Round Announcement UI with the Loadings, Comic Art with Battlehud Portraits Colour)
    auto count_packs = [&](std::vector<Found> v) { return build_packs(v).size(); };
    std::vector<uint64_t> big;
    for (uint64_t h = 1; h <= 300; ++h) big.push_back(h);
    check(count_packs({ F("x.zip/a", "name=A\n", { 1, 2, 3 }), F("y.zip/b", "name=B\n", { 3, 2, 1 }) }) == 1, "roots with the same files: gathered");
    check(count_packs({ F("x.zip/a", "name=A\n", { 1, 2 }), F("y.zip/b", "name=B\n", { 1 }) }) == 1, "2 files against 1 of them (Jaccard 1/2): gathered");
    check(count_packs({ F("x.zip/a", "name=A\n", big), F("y.zip/b", "name=B\n", { 1 }) }) == 2, "300 files against 1 of them: a pack each");
    check(count_packs({ F("x.zip/a", "name=A\n", { 1, 2, 3, 4 }), F("y.zip/b", "name=B\n", { 3, 4, 5, 6, 7 }) }) == 2, "a partial overlap (2 of 7): a pack each");
    notes.clear();
    const std::vector<Found> cross = {
        F("Comic.rar/Comic", "name=Comic Art\nDummyMod=True\n", {}),
        F("Comic.rar/Hud", "name=BattleHud\nAddonFor=Comic Art\n", { 1, 2, 3 }),
        F("Comic.rar/Select", "name=Character Select\nAddonFor=Comic Art\n", { 10, 11, 12, 13 }),
        F("Hud.rar/Colour", "name=Battlehud Portraits Colour\n", { 1, 2, 3, 4, 5 }),
        F("Hud.rar/Customise", "name=Customise\nAddonFor=Battlehud Portraits Colour\nDummyMod=True\n", {}),
        F("Hud Ryu.rar/Ryu", "name=Ryu C1\nAddonFor=Customise\n", { 1 }),   // depth 2, deeper than Comic Art's BattleHud
    };
    const std::vector<Pack> cp = build_packs(cross, &notes);
    check(cp.size() == 2 && cp[0].name == "Battlehud Portraits Colour" && cp[1].name == "Comic Art" && cp[0].nodes.size() == 3,
          "packs whose trees share some files stay apart (an add-on archive under its own pack)");
    check(notes.size() == 1 && notes[0].find("both have 3 file(s)") != std::string::npos && notes[0].find("\"Comic Art\" wins") != std::string::npos,
          "the files two packs share: reported once per pair, the later by name wins them");
    if (cp.size() == 2) {
        const std::vector<Served> co = served_of(cp, cross);
        Choices ticks;
        ticks[cp[0].key] = { id(&cp[0], "Ryu C1"), id(&cp[0], "Battlehud Portraits Colour") };
        ticks[cp[1].key] = { id(&cp[1], "BattleHud") };
        const HashMap cw = winners(co, ticks);
        check(get(cw, 1) == copy_of(at(&cp[1], "BattleHud"), 1) && get(cw, 2) == copy_of(at(&cp[1], "BattleHud"), 2) &&
              get(cw, 4) == copy_of(at(&cp[0], "Battlehud Portraits Colour"), 4),
              "a file two packs have: the later pack's ticked option wins it (over a deeper one of the other), the rest stays");
    }

    Choices c = parse_settings_choices("\xEF\xBB\xBF{ \"title_pack\": \"t1\", \"nested\": {\"ui_choices\": {\"ux\": [\"no\"]}},\n"
                                       " \"ui_choices\": { \"U0123456789AB\": [\" N1 \", \"n2\", 3, \"n2\"], \"u2\": \"not a list\", \"u3\": [] },\n \"skip_intro\": true }");
    check(c.size() == 2 && c["u0123456789ab"] == std::vector<std::string>{ "n1", "n2" } && c.count("u3") && c["u3"].empty() && !c.count("ux") && !c.count("u2"),
          "settings.json's ui_choices: top level only, any case, spaces, numbers and other values skipped");
    check(parse_settings_choices("{\"select_options_key\": \"C\"}").empty() && parse_settings_choices("").empty() && parse_settings_choices("[1]").empty(),
          "no ui_choices, an empty file, not an object: nothing ticked");
    c = parse_settings_choices("{\"ui_choices\": {\"u1\": [\"n1\"], \"u2\": [\"n");
    check(c.count("u1") && c["u1"] == std::vector<std::string>{ "n1" } && (!c.count("u2") || c["u2"].empty()), "a text cut short keeps what came before");
    c = parse_choices(" {\"u1\": [\"n1\", \"n2\"], \"u2\": []} ");
    check(c.size() == 2 && c["u1"].size() == 2 && parse_choices("{}").empty() && parse_choices("null").empty(), "the ui_choices object itself (SF6UISlots_Apply)");

    const std::string js = registry_text(packs, applied(packs, launch), {});
    check(js.find("\"key\": \"" + fgk + "\", \"name\": \"FG Artwork\"") != std::string::npos &&
          js.find("\"kind\": \"title\", \"choice\": \"\"") != std::string::npos &&
          js.find("\"name\": \"Luke\", \"description\": \"\", \"kind\": \"option\", \"choice\": \"one\", \"group\": \"" + arcade->id + "\"") != std::string::npos &&
          js.find("\"name\": \"Default Set\", \"description\": \"\", \"kind\": \"option\", \"choice\": \"check\", \"group\": \"\", \"files\": 4") != std::string::npos &&
          js.find("\"applied\": { \"" + fgk + "\": [\"" + id(fg, "Default Set") + "\", \"" + id(fg, "Dark Reign") + "\"] }") != std::string::npos,
          "registry: packs, nodes (kind, choice, group, files), what is applied");
    check(registry_text({}, {}, {}) == "{\n  \"version\": 1,\n  \"packs\": [],\n  \"game_textures\": {},\n  \"applied\": {}\n}\n", "registry without packs");

    // fighters: the folder after character_image/, subset choices "<id>@<fid>"
    const std::string fp = "natives/stm/product/gui/data/character_image/013/battlehud/tex_battlehud_012_1p_iam.tex.241101895";
    check(fighter_of_path(fp) == "013", "fighter of a path: the folder decides, not the file name");
    check(fighter_of_path("NATIVES/STM/Product/GUI/Data/Character_Image/021/x.tex.1") == "021", "fighter of a path: any case");
    check(fighter_of_path("natives/stm/product/gui/data/character_image/ryu/x.tex.1").empty() &&
          fighter_of_path("natives/stm/product/gui/data/character_image//x.tex.1").empty(), "fighter of a path: a folder without digits or empty: none");
    check(fighter_of_path("natives/stm/product/gui/cmn/ui01900/x.tex.241101895").empty() &&
          fighter_of_path("natives/stm/streaming/product/gui/cmn/x.tex.241101895").empty(), "fighter of a path: no character_image, a streaming path: none");
    check(fighter_of_path("natives/stm/streaming/product/gui/data/character_image/005/x.tex.1") == "005", "fighter of a path: a streaming path under a fighter folder");

    auto G = [](const char* path, const char* ini, std::vector<uint64_t> files) {
        Found f;
        f.path = path;
        f.info = parse_modinfo(ini);
        f.files = std::move(files);
        return f;
    };
    const std::vector<Found> fin = { G("H.zip/Root", "name=Hud\nDummyMod=True\n", {}), G("H.zip/Colour", "name=Colour\nAddonFor=Hud\n", { 11, 12, 13 }) };
    std::vector<Pack> fpacks = build_packs(fin);
    check(fpacks.size() == 1 && fpacks[0].nodes.size() == 2, "fighter packs: a title and a box");
    if (fpacks.size() == 1 && fpacks[0].nodes.size() == 2) {
        Pack& hp = fpacks[0];
        Node& title = hp.nodes[0];
        Node& box = hp.nodes[1];
        box.fids = { "013", "021" };
        box.fighters["013"] = { "product/gui/data/character_image/013/a.tex" };
        box.fighters["021"] = {};
        box.fighters_only = true;
        const std::string whole = box.id, s13 = subset_id(box.id, "013"), s21 = subset_id(box.id, "021");
        std::string w, fid;
        check(split_choice(s13, w, fid) && w == whole && fid == "013" && !split_choice(whole, w, fid) && w == whole && fid.empty(), "split a choice id");
        Choices ch;
        ch[hp.key] = { s21, s13, subset_id(box.id, "099"), subset_id(title.id, "013") };
        check(applied(fpacks, ch)[hp.key] == std::vector<std::string>{ s13, s21 }, "applied: subset ids kept in fid order, an unknown fid dropped, a title's dropped");
        ch[hp.key] = { s13, whole };
        check(applied(fpacks, ch)[hp.key] == std::vector<std::string>{ whole }, "applied: the whole id drops the same node's subset ids");

        // three files: 11 under 013, 12 under 021, 13 under no fighter; a second option (deeper) has 11 too
        Served a{ hp.key, whole, 1, 1, {}, 0, {} };
        a.files = { { 11, 1101 }, { 12, 1201 }, { 13, 1301 } };
        a.fid = { { 11, "013" }, { 12, "021" } };
        Served b{ hp.key, "nother", 2, 2, {}, 0, {} };
        b.files = { { 11, 2101 } };
        b.fid = { { 11, "013" } };
        const std::vector<Served> fo = { a, b };
        Choices sub;
        sub[hp.key] = { s21 };
        check(winners(fo, sub) == HashMap{ { 12, 1201 } }, "winners: a subset id serves only that fighter's files");
        sub[hp.key] = { s13 };
        check(winners(fo, sub) == HashMap{ { 11, 1101 } }, "winners: a subset id of the other fighter");
        sub[hp.key] = { s13, "nother" };
        check(winners(fo, sub) == HashMap{ { 11, 2101 } }, "winners: a deeper option ticked whole still wins a file the subset has");
        sub[hp.key] = { whole };
        check(winners(fo, sub).size() == 3, "winners: the whole id serves all its files");

        const std::map<std::string, std::string> gt = { { "product/gui/data/character_image/013/a.tex", "_uislots/game/product/gui/data/character_image/013/a.tex" } };
        const std::string rj = registry_text(fpacks, {}, gt);
        check(rj.find("\"fighters\": { \"013\": [\"product/gui/data/character_image/013/a.tex\"], \"021\": [] }, \"fighters_only\": true }") != std::string::npos &&
              rj.find("\"game_textures\": {\n    \"product/gui/data/character_image/013/a.tex\": \"_uislots/game/product/gui/data/character_image/013/a.tex\"\n  }") != std::string::npos &&
              registry_text(packs, applied(packs, launch), {}).find("\"fighters\"") == std::string::npos,
              "registry: fighters and fighters_only per node that has fighters, game_textures at the top");
    }
}

// The picture cache: the key changes with any input, a cache file round-trips and a damaged one is refused
void test_pic_cache() {
    printf("Picture cache\n");
    using namespace pic_cache;
    const std::string k = key("ui", "ui_mods/A.zip/x/shot.png", 1000, 5000, 0, 0);
    check(k.size() == 16 && k == key("ui", "ui_mods/A.zip/x/shot.png", 1000, 5000, 0, 0), "cache key: 16 hex digits, the same inputs the same key");
    check(k == key("ui", "UI_MODS/a.zip/X/SHOT.png", 1000, 5000, 0, 0), "cache key: the path in any case");
    check(k != key("title", "ui_mods/A.zip/x/shot.png", 1000, 5000, 0, 0) && k != key("ui", "ui_mods/A.zip/x/shot2.png", 1000, 5000, 0, 0) &&
          k != key("ui", "ui_mods/A.zip/x/shot.png", 1001, 5000, 0, 0) && k != key("ui", "ui_mods/A.zip/x/shot.png", 1000, 5001, 0, 0) &&
          k != key("ui", "ui_mods/A.zip/x/shot.png", 1000, 5000, 64, 36) && key("t", "p", 1, 2, 64, 36) != key("t", "p", 1, 2, 36, 64),
          "cache key: another namespace, path, size, time or cell size, another key");
    Parts p;
    p.tex = { 1, 2, 3, 4, 5 };
    p.thumb = { 9, 8 };
    const std::vector<uint8_t> raw = pack(p);
    Parts q;
    check(unpack(raw, q) && q.tex == p.tex && q.thumb == p.thumb && q.cell.empty() && q.band.empty(), "cache file: written and read back");
    std::vector<uint8_t> cut(raw.begin(), raw.end() - 1), longer = raw, bad = raw;
    longer.push_back(0);
    bad[0] = 'X';
    Parts none;
    check(!unpack(cut, none) && !unpack(longer, none) && !unpack(bad, none) && !unpack({}, none) && !unpack(pack(Parts{}), none),
          "cache file: cut short, longer, wrong magic, empty or without a .tex: refused");
}

// A pack lacking a title file another background has gets the game's own copy of it on the switch (title_slots::fill_from_game)
void test_title_fill() {
    printf("Title switch: a pack that lacks a movie\n");
    using E = std::vector<std::pair<uint64_t, uint64_t>>;
    const E game = { { 1, 901 }, { 2, 902 }, { 3, 903 } };   // start, loop, end -> the game's own copies
    E start_only = { { 1, 101 } };
    check(title_slots::fill_from_game(start_only, game) == 2 && start_only == E{ { 1, 101 }, { 2, 902 }, { 3, 903 } },
          "title_start alone: its own start, the game's loop and end");
    E whole = { { 1, 201 }, { 2, 202 }, { 3, 203 } };
    check(title_slots::fill_from_game(whole, game) == 0 && whole == E{ { 1, 201 }, { 2, 202 }, { 3, 203 } }, "all three movies: nothing added");
    E loop_only = { { 2, 302 } };
    check(title_slots::fill_from_game(loop_only, game) == 2 && loop_only == E{ { 2, 302 }, { 1, 901 }, { 3, 903 } },
          "title_loop alone: its loop, the game's start and end");
    E none_held = { { 1, 101 } };
    check(title_slots::fill_from_game(none_held, {}) == 0 && none_held.size() == 1, "no game copy in the pak: nothing added");
    // with the title's sound banks (4 music, 5 narrator): the game's own of each, once a pack has them
    const E game_snd = { { 1, 901 }, { 2, 902 }, { 3, 903 }, { 4, 904 }, { 5, 905 } };
    E alpha3 = { { 1, 101 }, { 4, 104 }, { 5, 105 } };     // "Alpha 3 Alternative Intro": title_start, music, narrator
    check(title_slots::fill_from_game(alpha3, game_snd) == 2 && alpha3 == E{ { 1, 101 }, { 4, 104 }, { 5, 105 }, { 2, 902 }, { 3, 903 } },
          "start + banks: its own start and sounds, the game's loop and end");
    E silent = { { 1, 201 }, { 2, 202 }, { 3, 203 } };    // three movies, no bank
    check(title_slots::fill_from_game(silent, game_snd) == 2 && silent == E{ { 1, 201 }, { 2, 202 }, { 3, 203 }, { 4, 904 }, { 5, 905 } },
          "movies without banks: the game's music and narrator (not the launch pack's)");
}

// ---- Title sound banks (title_slots::check_title_bank): synthetic banks laid out as SF6's (version 135) ----
void put32(std::vector<uint8_t>& b, uint32_t v) { for (int k = 0; k < 4; ++k) b.push_back(uint8_t(v >> (8 * k))); }
void put_chunk(std::vector<uint8_t>& b, const char* tag, const std::vector<uint8_t>& data) {
    b.insert(b.end(), tag, tag + 4);
    put32(b, uint32_t(data.size()));
    b.insert(b.end(), data.begin(), data.end());
}
std::vector<uint8_t> bkhd(uint32_t version, uint32_t id) {
    std::vector<uint8_t> h;
    put32(h, version);
    put32(h, id);
    put32(h, 0);
    put32(h, 0);
    put32(h, 0);
    return h;
}
// A WEM of `size` bytes whose fmt chunk says `tag`
// tag 0xFFFE: a plain Microsoft WAV's WAVEFORMATEXTENSIBLE (40-byte fmt, 16-bit, 48 kHz, `field` at fmt +0x14, the PCM GUID) and a
// 4-byte JUNK chunk, as "Alpha 3" ships; tag 1: a 16-byte WAVEFORMATEX; other tags: an 18-byte fmt holding the tag alone
std::vector<uint8_t> wem(uint16_t tag, size_t size, uint16_t channels = 1, uint32_t field = 0x4) {
    std::vector<uint8_t> w = { 'R', 'I', 'F', 'F', 0, 0, 0, 0, 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' };
    auto p16 = [&](uint32_t v) { w.push_back(uint8_t(v)); w.push_back(uint8_t(v >> 8)); };
    if (tag == 0xFFFE || tag == 1) {
        put32(w, tag == 1 ? 16 : 40);
        p16(tag);
        p16(channels);
        put32(w, 48000);
        put32(w, 48000u * 2 * channels);
        p16(2 * channels);
        p16(16);
        if (tag == 0xFFFE) {
            p16(22);
            p16(16);
            put32(w, field);
            const uint8_t guid[16] = { 1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 };
            w.insert(w.end(), guid, guid + 16);
            w.insert(w.end(), { 'J', 'U', 'N', 'K', 4, 0, 0, 0, 0, 0, 0, 0 });
        }
    } else {
        put32(w, 18);
        p16(tag);
    }
    w.resize(std::max<size_t>(size, w.size() + 16), 0x5A);
    return w;
}
std::vector<uint8_t> media_bank(uint32_t id, const std::vector<std::pair<uint32_t, std::vector<uint8_t>>>& media) {
    std::vector<uint8_t> b, idx, data;
    put_chunk(b, "BKHD", bkhd(135, id));
    for (auto& [mid, w] : media) {
        while (data.size() % 16) data.push_back(0);
        put32(idx, mid);
        put32(idx, uint32_t(data.size()));
        put32(idx, uint32_t(w.size()));
        data.insert(data.end(), w.begin(), w.end());
    }
    put_chunk(b, "DIDX", idx);
    put_chunk(b, "DATA", data);
    return b;
}
struct SynthSound { uint32_t object, plugin; uint8_t stream; uint32_t media, size; };
std::vector<uint8_t> event_bank(uint32_t version, const std::vector<SynthSound>& sounds) {
    std::vector<uint8_t> b, h;
    put_chunk(b, "BKHD", bkhd(version, 77));
    put32(h, uint32_t(sounds.size() + 1));
    h.push_back(4);                       // an event first: skipped by its size
    put32(h, 9);
    put32(h, 1234);
    put32(h, 1);
    h.push_back(0);
    for (auto& s : sounds) {
        h.push_back(2);
        put32(h, 48);
        put32(h, s.object);
        put32(h, s.plugin);
        h.push_back(s.stream);
        put32(h, s.media);
        put32(h, s.size);
        h.push_back(0x11);                // uSourceBits
        for (int k = 0; k < 48 - 18; ++k) h.push_back(uint8_t(0xC0 + k));   // the node's other parameters
    }
    put_chunk(b, "HIRC", h);
    return b;
}

void test_title_sounds() {
    printf("Title sound banks against the game's sounds\n");
    using namespace title_slots;
    namespace ww = title_slots::wwise;
    check(ww::codec_of_format(0xFFFF) == ww::kVorbis && ww::codec_of_format(0xFFFE) == ww::kPcm && ww::codec_of_format(1) == ww::kPcm &&
          ww::codec_of_format(2) == ww::kAdpcm && ww::codec_of_format(0x3040) == 0, "codec from a WEM's fmt tag: Vorbis, PCM, ADPCM; others unknown");
    const std::vector<uint8_t> vorbis = wem(0xFFFF, 200), pcm = wem(0xFFFE, 900), music = wem(0xFFFF, 300);
    check(ww::wem_format(pcm.data(), pcm.size()) == 0xFFFE && ww::wem_format(vorbis.data(), 8) == 0, "fmt tag read; a cut WEM: none");
    const std::vector<uint8_t> game_m = media_bank(11, { { 100, vorbis }, { 200, music } });
    std::vector<ww::Media> m;
    check(ww::media_of(game_m, m) && m.size() == 2 && m[0].id == 100 && m[0].size == 200 && m[0].codec == ww::kVorbis && m[1].id == 200,
          "media bank: DIDX against DATA, each media's codec");
    const std::vector<uint8_t> game_es = event_bank(135, { { 5, ww::kVorbis, 0, 100, 200 }, { 6, ww::kVorbis, 0, 200, 300 }, { 7, ww::kVorbis, 0, 999, 50 } });
    std::vector<ww::Sound> s;
    check(ww::sounds_of(game_es, s) && s.size() == 3 && s[0].object == 5 && s[0].media == 100 && s[0].size == 200 && s[0].plugin == ww::kVorbis,
          "event bank: the CAkSound objects past another object");
    const std::vector<EventBank> events = { { "natives/stm/product/sound/wwise/ui_title_es.sbnk.1.x64", game_es, false } };

    BankVerdict v = check_title_bank(game_m, events);
    check(v.media_bank && !v.refuse && v.patches.empty(), "the game's own media: nothing to adapt");
    v = check_title_bank(game_es, events);
    check(!v.media_bank && !v.refuse, "an event bank shipped by a pack: no media, not checked");

    // "Alpha 3": media 100 in PCM of another size, media 200 the game's, media 300 no sound plays
    const std::vector<uint8_t> alpha = media_bank(11, { { 100, pcm }, { 200, music }, { 300, pcm } });
    // as shipped: another codec refused (kAdaptTitleCodec off: silent in game twice, 20/11), the game's codec of another size adapted
    check(!kAdaptTitleCodec && kAdaptTitleSizes, "as shipped: another codec refused, a size of the game's codec adapted");
    v = check_title_bank(alpha, events);
    check(v.refuse && v.patches.empty() && v.why == "PCM where the game plays Vorbis" && v.from == ww::kVorbis && v.to == ww::kPcm,
          "PCM where the sound says Vorbis: the bank refused, the game's own plays");
    const std::vector<uint8_t> vorbis450 = media_bank(11, { { 100, wem(0xFFFF, 450) } });
    v = check_title_bank(vorbis450, events);
    check(!v.refuse && v.adapted == 1 && v.patches.size() == 1 && v.patches[0].plugin == ww::kVorbis && v.patches[0].size == 450 &&
          v.fields.empty() && v.from == ww::kVorbis && v.to == ww::kVorbis, "Vorbis of another size: that sound's size adapted, its codec kept");
    {
        std::vector<uint8_t> sized = game_es;
        apply_sound_patches(sized, 0, v.patches);
        size_t d = 0;
        for (size_t i = 0; i < sized.size(); ++i) d += sized[i] != game_es[i];
        check(ww::sounds_of(sized, s) && s[0].plugin == ww::kVorbis && s[0].size == 450 && s[1].size == 300 && d >= 1 && d <= 4,
              "the size patch: only uInMemoryMediaSize's bytes of that sound changed");
    }
    v = check_title_bank(vorbis450, events, {}, false, false);
    check(v.refuse && v.why.find("another size") != std::string::npos, "Vorbis of another size, sizes not adapted: refused");
    v = check_title_bank(media_bank(11, { { 100, vorbis }, { 300, pcm } }), events, {}, false);
    check(!v.refuse && v.patches.empty(), "the game's codec and size, plus a media no sound plays: plays as it is");
    // the adaptation itself, kept for a later try (adapt = true)
    v = check_title_bank(alpha, events, {}, true);
    check(!v.refuse && v.adapted == 1 && v.patches.size() == 1 && v.patches[0].plugin == ww::kPcm && v.patches[0].size == 900 &&
          v.from == ww::kVorbis && v.to == ww::kPcm, "PCM where the sound says Vorbis: that sound adapted, codec and size; the others left");
    std::vector<uint8_t> patched = game_es;
    apply_sound_patches(patched, 0, v.patches);
    check(ww::sounds_of(patched, s) && s[0].plugin == ww::kPcm && s[0].size == 900 && s[0].media == 100 && s[0].stream == 0 &&
          s[1].plugin == ww::kVorbis && s[1].size == 300 && s[2].size == 50, "the patched copy: that sound's codec and size, the media id kept");
    size_t changed = 0;
    for (size_t i = 0; i < patched.size(); ++i) changed += patched[i] != game_es[i];
    check(patched.size() == game_es.size() && changed <= 8 && changed >= 2, "the patched copy: same size, at most the 8 bytes of the two fields");
    std::vector<uint8_t> other = game_es;
    apply_sound_patches(other, 1, v.patches);
    check(other == game_es, "patches of another event bank: nothing changed");

    // the same media played by two banks' sounds: both adapted
    const std::vector<uint8_t> narr_es = event_bank(135, { { 8, ww::kVorbis, 0, 100, 200 } });
    const std::vector<EventBank> two = { events[0], { "natives/stm/product/sound/wwise/narration_title_es.sbnk.1.x64", narr_es, false } };
    v = check_title_bank(alpha, two, {}, true);
    check(!v.refuse && v.adapted == 1 && v.patches.size() == 2 && v.patches[0].bank == 0 && v.patches[1].bank == 1,
          "a media two banks' sounds play: both sounds adapted, counted once");

    // what cannot be adapted: the bank refused
    v = check_title_bank(media_bank(11, { { 100, wem(0x3040, 900) } }), events, {}, true);
    check(v.refuse && v.patches.empty() && v.why.find("not known") != std::string::npos, "a format not known here: refused");
    v = check_title_bank(media_bank(11, { { 100, wem(0x3040, 200) } }), events, {}, true);
    check(v.refuse, "an unknown format even with the declared size: refused (its codec cannot be named)");
    const uint32_t opus = 0x00140001;
    const std::vector<EventBank> opus_es = { { events[0].path, event_bank(135, { { 5, opus, 0, 100, 200 } }), false } };
    std::vector<ww::Media> game_opus;
    ww::media_of(media_bank(11, { { 100, wem(0x3040, 200) } }), game_opus);
    v = check_title_bank(media_bank(11, { { 100, wem(0x3040, 700) } }), opus_es, game_opus, true);
    check(!v.refuse && v.patches.size() == 1 && v.patches[0].plugin == opus && v.patches[0].size == 700,
          "a format not known here but the game's own for that media: its codec kept, the size adapted");
    v = check_title_bank(media_bank(11, { { 100, wem(0x3040, 700) } }), opus_es, game_opus, false, false);
    check(v.refuse && v.why.find("another size") != std::string::npos, "the same without the adaptation: refused (another size)");
    v = check_title_bank(media_bank(11, { { 100, wem(0x3040, 200) } }), opus_es, game_opus);
    check(!v.refuse && v.patches.empty(), "the game's own unknown format and size: plays as it is");

    // the channel field (fmt +0x14): a Windows speaker mask rewritten to Wwise's packed config, those 4 bytes only
    check(wwise_channel_config(0x4) == 0x4101 && wwise_channel_config(0x63F) == 0x63F108 && wwise_channel_config(0x3) == 0x3102,
          "packed config: (mask << 12) | popcount(mask) | 0x100 (mono 0x4101, 7.1 0x63F108)");
    check(packed_channel_config(0x4101, 1) && packed_channel_config(0x63F108, 8) && !packed_channel_config(0x4, 1) &&
          !packed_channel_config(0x63F, 8), "a packed config read as one; a Windows mask not");
    const std::vector<uint8_t> a3 = media_bank(11, { { 100, wem(0xFFFE, 900, 8, 0x63F) }, { 200, music }, { 300, wem(0xFFFE, 400, 1, 0x4) },
                                                     { 400, wem(0xFFFE, 300, 1, 0x4) } });
    const std::vector<EventBank> a3_es = { { events[0].path, event_bank(135, { { 5, ww::kVorbis, 0, 100, 200 }, { 6, ww::kVorbis, 0, 200, 300 },
                                                                              { 9, ww::kVorbis, 0, 300, 50 } }), false } };
    v = check_title_bank(a3, a3_es, {}, true);
    std::vector<ww::Media> a3m;
    ww::media_of(a3, a3m);
    check(!v.refuse && v.patches.size() == 2 && v.fields.size() == 2 && v.fields[0].first == a3m[0].fmt + 20 && v.fields[0].second == 0x63F108 &&
          v.fields[1].first == a3m[2].fmt + 20 && v.fields[1].second == 0x4101 && v.media_info.size() == 2,
          "plain-WAV PCM: the played media's channel fields rewritten (7.1, mono), not the one no sound plays");
    std::vector<uint8_t> a3c = a3;
    apply_media_fields(a3c, v.fields);
    size_t diff = 0;
    for (size_t i = 0; i < a3c.size(); ++i) diff += a3c[i] != a3[i];
    std::vector<ww::Media> a3cm;
    check(a3c.size() == a3.size() && diff <= 8 && ww::media_of(a3c, a3cm) && a3cm.size() == 4 && a3cm[0].size == a3m[0].size &&
          a3cm[2].channels == 1 && ww::rd32(a3c, a3m[2].fmt + 20) == 0x4101 && ww::rd32(a3c, a3m[0].fmt + 20) == 0x63F108 &&
          ww::rd32(a3c, a3m[3].fmt + 20) == 0x4, "the copy: only those fields changed, DIDX and sizes kept");
    check(v.media_info[0].find("8 ch, 48000 Hz, 768000 bytes/s, block 16, 16 bits, channel field 0x63f -> 0x63f108") != std::string::npos,
          "the log line per media: channels, rate, bytes/s, block, bits, the field");
    v = check_title_bank(media_bank(11, { { 100, wem(0xFFFE, 900, 1, 0x4101) } }), events, {}, true);
    check(!v.refuse && v.fields.empty() && v.patches.size() == 1, "a field already packed: left, the sounds still adapted");
    v = check_title_bank(media_bank(11, { { 100, wem(1, 900) } }), events, {}, true);
    check(v.refuse && v.why.find("channel layout") != std::string::npos, "PCM without an extensible fmt: refused");
    v = check_title_bank(media_bank(11, { { 100, wem(0xFFFE, 900, 2, 0x4) } }), events, {}, true);
    check(v.refuse && v.why.find("channel layout") != std::string::npos, "a mask that does not count the channels: refused");
    v = check_title_bank(media_bank(11, { { 100, wem(0xFFFE, 900, 1, 0x4) } }), events, {}, false);
    check(v.refuse && v.fields.empty(), "without the adaptation: refused, no field rewritten");
    v = check_title_bank(alpha, { { events[0].path, event_bank(135, { { 5, ww::kVorbis, 2, 100, 200 } }), false } }, {}, true);
    check(v.refuse && v.why.find("streamed") != std::string::npos, "a streamed sound: refused");
    v = check_title_bank(alpha, { { events[0].path, game_es, true } }, {}, true);
    check(v.refuse && v.why.find("pack's own") != std::string::npos, "against the pack's own event bank: refused (never patched)");
    v = check_title_bank(alpha, { { events[0].path, event_bank(135, { { 5, ww::kPcm, 0, 100, 900 } }), true } });
    check(!v.refuse && v.patches.empty(), "the pack's own event bank that says PCM and the size: plays as it is");

    // banks that cannot be read: no sounds, nothing decided from them
    check(!ww::sounds_of(event_bank(88, { { 5, ww::kVorbis, 0, 100, 200 } }), s), "an event bank of an unknown layout (version 88): not read");
    std::vector<uint8_t> cut = game_es;
    cut.resize(cut.size() - 10);
    check(!ww::sounds_of(cut, s) && !ww::media_of(std::vector<uint8_t>(game_m.begin(), game_m.end() - 5), m), "a bank cut short: not read");
    v = check_title_bank(alpha, { { events[0].path, cut, false } });
    check(!v.refuse && v.patches.empty(), "no event bank readable: the bank kept as it is");
}

// Offline, on the real game: its title event banks from re_chunk_000.pak, each bank given (and the game's own media banks) checked
// and, when adapted, patched and read back. Read-only.
int check_title_banks(int argc, wchar_t** argv) {
    using namespace title_slots;
    PakReader base;
    const std::wstring game = argv[2];
    if (!base.open(narrow(game + L"\\re_chunk_000.pak").c_str())) { printf("re_chunk_000.pak not readable\n"); return 1; }
    std::vector<EventBank> events;
    std::vector<std::pair<std::string, std::vector<uint8_t>>> files;
    for (auto& path : known_paths()) {
        if (!is_title_sound(path)) continue;
        const PakEntry* e = base.find(pak_path_hash(std::string_view(path)));
        if (!e) continue;
        std::vector<uint8_t> d = base.read(*e);
        std::vector<wwise::Sound> s;
        if (wwise::sounds_of(d, s)) {
            printf("game event bank %s: %zu sound(s)\n", path.c_str(), s.size());
            events.push_back({ path, std::move(d), false });
        } else {
            files.push_back({ "game:" + path, std::move(d) });
        }
    }
    for (int i = 3; i < argc; ++i) {
        FILE* f = nullptr;
        if (_wfopen_s(&f, argv[i], L"rb") || !f) { printf("cannot read %s\n", narrow(argv[i]).c_str()); return 1; }
        std::vector<uint8_t> d;
        uint8_t buf[65536];
        for (size_t n; (n = fread(buf, 1, sizeof(buf), f)) > 0;) d.insert(d.end(), buf, buf + n);
        fclose(f);
        files.push_back({ narrow(argv[i]), std::move(d) });
    }
    std::vector<wwise::Media> game_media;
    for (auto& [name, d] : files) {
        std::vector<wwise::Media> m;
        if (name.compare(0, 5, "game:") == 0 && wwise::media_of(d, m)) game_media.insert(game_media.end(), m.begin(), m.end());
    }
    const bool adapt = kAdaptTitleCodec || _wgetenv(L"STAGETEST_ADAPT") != nullptr;   // STAGETEST_ADAPT: show the codec adaptation anyway
    int bad = 0;
    for (auto& [name, d] : files) {
        const BankVerdict v = check_title_bank(d, events, game_media, adapt);
        printf("%s: %s%s", name.c_str(), !v.media_bank ? "no media" : v.refuse ? "REFUSED: " : v.patches.empty() ? "plays as it is" : "adapted",
               v.refuse ? v.why.c_str() : "");
        if (!v.patches.empty()) printf(" (%zu media, %s -> %s, %zu sound(s))", v.adapted, wwise::codec_name(v.from).c_str(), wwise::codec_name(v.to).c_str(),
                                       v.patches.size());
        printf("\n");
        for (auto& line : v.media_info) printf("  %s\n", line.c_str());
        if (!v.fields.empty()) {
            std::vector<uint8_t> copy = d;
            apply_media_fields(copy, v.fields);
            size_t changed = 0;
            for (size_t k = 0; k < copy.size(); ++k) changed += copy[k] != d[k];
            std::vector<wwise::Media> before, after;
            const bool ok = wwise::media_of(d, before) && wwise::media_of(copy, after) && after.size() == before.size() && copy.size() == d.size();
            printf("  channel field rewritten for %zu media: %s, %zu byte(s) changed\n", v.fields.size(), ok ? "reads back" : "BROKEN", changed);
            if (!ok || changed > 4 * v.fields.size()) ++bad;
        }
        for (size_t e = 0; e < events.size(); ++e) {
            if (std::none_of(v.patches.begin(), v.patches.end(), [&](const SoundPatch& p) { return p.bank == e; })) continue;
            std::vector<uint8_t> copy = events[e].data;
            apply_sound_patches(copy, e, v.patches);
            std::vector<wwise::Sound> before, after;
            wwise::sounds_of(events[e].data, before);
            const bool ok = wwise::sounds_of(copy, after) && after.size() == before.size() && copy.size() == events[e].data.size();
            size_t changed = 0;
            for (size_t k = 0; k < copy.size(); ++k) changed += copy[k] != events[e].data[k];
            printf("  %s patched: %s, %zu byte(s) changed\n", events[e].path.c_str(), ok ? "reads back" : "BROKEN", changed);
            if (!ok || changed > 8 * v.patches.size()) ++bad;
            for (size_t k = 0; k < after.size(); ++k)
                if (after[k].plugin != before[k].plugin || after[k].size != before[k].size)
                    printf("    sound %u (media %u): %s %u -> %s %u\n", after[k].object, after[k].media, wwise::codec_name(before[k].plugin).c_str(),
                           before[k].size, wwise::codec_name(after[k].plugin).c_str(), after[k].size);
        }
    }
    return bad ? 1 : 0;
}

// The loaders' cache folders (common/cache_dirs.hpp), on a scratch folder under %TEMP%: what goes, what stays, links never followed
void test_cache_dirs() {
    printf("Cache folders\n");
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    const std::wstring base = std::wstring(tmp) + L"sf6_cache_dirs_test_" + std::to_wstring(GetCurrentProcessId());
    cache_dirs::remove_tree(base);
    auto put = [](const std::wstring& path, const char* text) {
        for (size_t i = 3; i < path.size(); ++i)
            if (path[i] == L'\\') CreateDirectoryW(path.substr(0, i).c_str(), nullptr);
        FILE* f = nullptr;
        if (_wfopen_s(&f, path.c_str(), L"wb") == 0 && f) { fputs(text, f); fclose(f); }
    };
    auto there = [](const std::wstring& p) { return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES; };
    auto junction = [](const std::wstring& link, const std::wstring& target) {
        const std::wstring cmd = L"cmd /c mklink /J \"" + link + L"\" \"" + target + L"\" >nul 2>&1";
        return _wsystem(cmd.c_str()) == 0 && (GetFileAttributesW(link.c_str()) & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    };
    const std::wstring outside = base + L"\\outside", cache = base + L"\\mods\\.cache";
    put(outside + L"\\precious.txt", "a player's file");
    const std::wstring used = cache + L"\\0123456789abcdef", stale = cache + L"\\fedcba9876543210";
    put(used + L"\\natives\\a.bin", "in use");
    put(stale + L"\\natives\\deep\\b.bin", "stale");
    put(stale + L"\\.complete", "ok\n");
    SetFileAttributesW((stale + L"\\natives\\deep\\b.bin").c_str(), FILE_ATTRIBUTE_READONLY);
    SetFileAttributesW((stale + L"\\natives\\deep").c_str(), FILE_ATTRIBUTE_READONLY);
    put(cache + L"\\_pictures\\0000000000000000.pic", "pic");
    put(cache + L"\\Not_a_key\\c.bin", "someone's");
    put(cache + L"\\abcdefabcdefabcd", "a file with a key's name");
    put(cache + L"\\ABCDEF0123456789\\d.bin", "upper case: not a key the loader writes");
    const bool linked = junction(stale + L"\\natives\\link", outside) && junction(cache + L"\\1111111111111111", outside);
    check(cache_dirs::is_hex16(L"0123456789abcdef") && !cache_dirs::is_hex16(L"0123456789ABCDEF") && !cache_dirs::is_hex16(L"_pictures") &&
          !cache_dirs::is_hex16(L"0123456789abcde") && !cache_dirs::is_hex16(L"0123456789abcdef0"), "is_hex16: 16 lower-case hex digits only");
    const cache_dirs::Pruned r = cache_dirs::prune(cache, cache_dirs::is_hex16, [](const std::wstring& n) { return n == L"0123456789abcdef"; });
    check(r.there && !r.link && !r.list_error && r.kept == 1 && r.removed.size() == 1 && r.removed[0] == L"fedcba9876543210" && r.failed.empty(),
          "prune: the entry in use kept, the stale one removed (read-only file and folder inside)");
    check(r.bytes == 8, "prune: the bytes of the removed files counted (5 + 3)");
    check(!there(stale) && there(used + L"\\natives\\a.bin"), "prune: the stale folder gone, the one in use whole");
    check(there(cache + L"\\_pictures\\0000000000000000.pic") && there(cache + L"\\Not_a_key\\c.bin") && there(cache + L"\\abcdefabcdefabcd") &&
          there(cache + L"\\ABCDEF0123456789\\d.bin"), "prune: _pictures, other names and a file with a key's name left alone");
    if (linked) {
        check(there(outside + L"\\precious.txt"), "a junction inside a stale entry: removed itself, its target untouched");
        check(there(cache + L"\\1111111111111111") && there(cache + L"\\1111111111111111\\precious.txt"),
              "a junction with a key's name in the cache: left alone (never followed)");
        const std::wstring link_root = base + L"\\linked\\.cache";
        CreateDirectoryW((base + L"\\linked").c_str(), nullptr);
        put(outside + L"\\2222222222222222\\e.bin", "behind a linked cache folder");
        const bool root_linked = junction(link_root, outside);
        const cache_dirs::Pruned l = cache_dirs::prune(link_root, cache_dirs::is_hex16, [](const std::wstring&) { return false; });
        check(root_linked && l.there && l.link && l.removed.empty() && there(outside + L"\\2222222222222222\\e.bin"),
              "a cache folder that is a junction: nothing removed behind it");
        RemoveDirectoryW(link_root.c_str());
        RemoveDirectoryW((cache + L"\\1111111111111111").c_str());
    } else {
        check(false, "junctions (mklink /J) could not be made: the link cases were not run");
    }
    const cache_dirs::Pruned none = cache_dirs::prune(base + L"\\no_such\\.cache", cache_dirs::is_hex16, [](const std::wstring&) { return false; });
    check(!none.there && none.removed.empty(), "prune: a cache folder that is not there: nothing");
    check(cache_dirs::remove_tree(base + L"\\no_such") && !cache_dirs::remove_tree(outside + L"\\precious.txt") && there(outside + L"\\precious.txt"),
          "remove_tree: a folder not there is gone already; a file is never removed");
    std::wstring deep = base + L"\\long";                       // past MAX_PATH
    while (deep.size() < 300) deep += L"\\a_rather_long_folder_name";
    put(L"\\\\?\\" + deep + L"\\f.bin", "deep");
    check(cache_dirs::remove_tree(base + L"\\long") && !there(L"\\\\?\\" + base + L"\\long"), "remove_tree: a tree past MAX_PATH");
    check(cache_dirs::remove_tree(base) && !there(base), "remove_tree: the scratch folder removed");
}

// The title screen's backgrounds the pass found: each one's files (title_start / loop / end) and whose copy serves them
void print_titles() {
    const TitleTables& t = stage_loader_titles();
    if (t.backgrounds.empty()) return;
    std::vector<std::string> keys;
    for (auto& [key, r] : t.backgrounds) keys.push_back(key);
    for (auto& [key, r] : t.backgrounds) {
        const bool playlist = std::find(t.playlist_pool.begin(), t.playlist_pool.end(), key) != t.playlist_pool.end();
        printf("title background %s%s:", key.c_str(), playlist ? " (playlist)" : "");
        for (auto& [real, copy] : r) {
            std::string file = "?", from = "?";
            for (auto& p : title_slots::known_paths()) {
                if (!title_slots::is_title_file(p) || pak_path_hash(std::string_view(p)) != real) continue;
                file = p.substr(p.rfind('/') + 1);     // title_start, ui_title_m...
                file = file.substr(0, file.find('.'));
                for (auto& k : keys)
                    if (pak_path_hash(std::string_view(title_slots::private_title_path(k, p))) == copy) from = k;
            }
            printf(" %s<-%s", file.c_str(), from.c_str());
        }
        printf("\n");
    }
    printf("title launch: %s\n", t.launch.c_str());
}

// The UI packs found by the pass: the tree, then what unticking everything in the game would serve
void print_ui() {
    const ui_slots::Tables& ui = stage_loader_ui();
    if (!ui.present) return;
    for (auto& p : ui.packs) {
        printf("UI pack %s  \"%s\"  by %s%s%s\n", p.key.c_str(), p.name.c_str(), p.author.c_str(), p.gathered ? "  (gathered roots)" : "",
               p.picture ? "  picture" : "");
        for (auto& nd : p.nodes) {
            printf("  %*s%s %s", nd.depth * 2, "", nd.title ? "#  " : nd.one ? "(o)" : "[ ]", nd.name.c_str());
            if (!nd.title) printf("  %zu file(s)", nd.files);
            if (nd.one) printf("  group %s", nd.group.c_str());
            printf("%s  %s\n", nd.preview ? "  preview" : "", nd.id.c_str());
        }
    }
    size_t ticked = 0;
    for (auto& [key, ids] : ui.launch) ticked += ids.size();
    const ui_slots::HashMap none = ui_slots::serve(ui.options, {}, ui.written, ui.game);
    const std::string ov = ui_slots::overrides_text(none, ui.names);
    printf("UI launch: %zu option(s) ticked, %zu file(s) at the real paths, %zu copies of the game's own; untick all in the game: %zu file(s) served, "
           "%zu texture override(s)\n", ticked, ui.written.size(), ui.game.size(), none.size(), (size_t)std::count(ov.begin(), ov.end(), '\n'));
    size_t shown = 0;
    for (size_t a = 0; a < ov.size() && shown < 3; ++shown) {
        const size_t e = ov.find('\n', a);
        printf("  %s\n", ov.substr(a, e - a).c_str());
        a = e + 1;
    }
}

// A stand-in for the game's re_chunk_000.pak: each natives path given holds a few bytes of its own, or, given as
// <natives path>=<file>, that file's bytes (real banks, for the title sounds' check)
int make_base(int argc, wchar_t** argv) {
    PakWriter w;
    for (int i = 3; i < argc; ++i) {
        std::string p = narrow(argv[i]), data = "the game's own " + p;
        const size_t eq = p.find('=');
        if (eq != std::string::npos) {
            FILE* f = nullptr;
            if (_wfopen_s(&f, widen(p.substr(eq + 1)).c_str(), L"rb") || !f) { printf("cannot read %s\n", p.substr(eq + 1).c_str()); return 1; }
            data.clear();
            char buf[65536];
            for (size_t n; (n = fread(buf, 1, sizeof(buf), f)) > 0;) data.append(buf, n);
            fclose(f);
            p = p.substr(0, eq);
        }
        w.add_uncompressed(pak_path_hash(std::string_view(p)), std::vector<uint8_t>(data.begin(), data.end()));
    }
    const bool ok = w.write(narrow(argv[2]).c_str());
    printf("%s: %d entr%s%s\n", narrow(argv[2]).c_str(), argc - 3, argc - 3 == 1 ? "y" : "ies", ok ? "" : " NOT WRITTEN");
    return ok ? 0 : 1;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc >= 2 && wcscmp(argv[1], L"--selftest") == 0) {
        test_classify();
        test_overrides();
        test_pictures();
        test_titles();
        test_ui();
        test_pic_cache();
        test_title_fill();
        test_title_sounds();
        test_cache_dirs();
        printf(g_fails ? "%d FAILED\n" : "all ok\n", g_fails);
        return g_fails ? 1 : 0;
    }
    if (argc >= 3 && wcscmp(argv[1], L"--make-base") == 0) return make_base(argc, argv);
    if (argc >= 3 && wcscmp(argv[1], L"--check-title-banks") == 0) return check_title_banks(argc, argv);
    if (argc < 2) { fprintf(stderr, "usage: stagetest <fake game dir> | stagetest --selftest | stagetest --make-base <out.pak> <path>...\n"); return 2; }
    slog_open(argv[1]);
    std::vector<StageVariant> vars;
    stage_loader_run(argv[1], vars);
    for (auto& v : vars)
        printf("%s  stage %u (%s)  \"%s\"  %zu files%s%s  %s: %s\n", v.key.c_str(), v.stage_id, v.ess.c_str(), v.name.c_str(),
               v.redirects.size(), v.has_preview ? "  preview" : "", v.banner.empty() ? "" : (" (" + v.banner + ")").c_str(),
               v.tile ? "TILE" : "variation", v.kind_reason.c_str());
    print_titles();
    print_ui();
    return 0;
}
