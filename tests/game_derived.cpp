// game_derived.exe: runs the loader's game-derived code (structural.cpp, vanilla_inventory.cpp,
// fighter_names.cpp) on given paks, read-only, and writes what it builds, so that a driver can compare it
// with the shipped loader data (tests/game_derived_real_input.py, tests/game_derived_fixture.py).
//
//   game_derived structural <out dir> <base.pak> [<dlc.pak> ...]
//       the structural files under the names static/ gives them, records.tsv, summary.txt
//   game_derived inventory <out.tsv> [--heuristic] <base.pak> [<dlc.pak> ...]
//       the vanilla index rows of every costume of every fighter of the game's costume table, in the
//       format of vanilla_costume_index.tsv (suffix section: the suffixes found)
//   game_derived names <base.pak> [<dlc.pak> ...]
//       fighter id, English name and costume_mods folder name, one per line
#include "pak.hpp"
#include "structural.hpp"
#include "vanilla_inventory.hpp"
#include "fighter_names.hpp"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace {

struct Paks {
    PakReader base;
    std::vector<std::unique_ptr<PakReader>> dlc;
    bool open(char** first, char** last) {
        if (first == last || !base.open(*first)) { fprintf(stderr, "cannot open base pak\n"); return false; }
        for (char** p = first + 1; p < last; p++) {
            auto r = std::make_unique<PakReader>();
            if (!r->open(*p)) { fprintf(stderr, "cannot open %s\n", *p); return false; }
            base.add_overlay(r.get());   // read under the base pak, as the loader does
            dlc.push_back(std::move(r));
        }
        return true;
    }
};

bool write_file(const std::string& path, const std::vector<uint8_t>& data) {
    FILE* f = fopen(path.c_str(), "wb");
    if (!f) return false;
    bool ok = fwrite(data.data(), 1, data.size(), f) == data.size();
    return (fclose(f) == 0) && ok;
}

// The local name loader/static/ gives a structural file
std::string local_name(const std::string& pak) {
    const std::string ver = "/flow/version_update/";
    size_t v = pak.find(ver);
    if (v != std::string::npos) return pak.substr(v + ver.size(), 11) + "_esf.scn.20";
    return pak.substr(pak.rfind('/') + 1);
}

double ms_since(std::chrono::steady_clock::time_point t0) {
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
}

int structural(const std::string& out, Paks& paks) {
    auto t0 = std::chrono::steady_clock::now();
    GameStructural st = build_game_structural(paks.base, 5, 104, 5000);
    const double ms = ms_since(t0);
    std::string summary = std::string("ok\t") + (st.ok ? "1" : "0") + "\nwhy\t" + st.why + "\nms\t" + std::to_string(ms) + "\n";
    for (auto& n : st.notes) summary += "note\t" + n + "\n";
    std::string fighters;
    for (int f : st.fighters) fighters += (fighters.empty() ? "" : ",") + std::to_string(f);
    summary += "fighters\t" + fighters + "\n";
    for (auto& f : st.files) {
        if (!write_file(out + "\\" + local_name(f.pak), f.data)) { fprintf(stderr, "cannot write\n"); return 1; }
        summary += "file\t" + local_name(f.pak) + "\t" + f.pak + "\t" + std::to_string(f.data.size()) + "\n";
    }
    std::string recs;
    for (auto& r : st.records)
        recs += std::to_string(r.fighter) + "\t" + std::to_string(r.costume_no) + "\t" + std::to_string(r.record_id) + "\n";
    write_file(out + "\\records.tsv", std::vector<uint8_t>(recs.begin(), recs.end()));
    write_file(out + "\\summary.txt", std::vector<uint8_t>(summary.begin(), summary.end()));
    printf("%s", summary.c_str());
    return st.ok ? 0 : 2;
}

int inventory(const std::string& out, bool heuristic, Paks& paks) {
    std::vector<int> fighters;
    if (!game_costume_fighters(paks.base, fighters)) { fprintf(stderr, "costume table not readable\n"); return 1; }
    vanilla_force_heuristic(heuristic);
    auto t0 = std::chrono::steady_clock::now();
    SuffixMap suffixes = vanilla_known_suffixes();
    std::vector<std::string> costumes, files;
    int failed = 0, fallback = 0;
    for (int f : fighters)
        for (int no : vanilla_costume_numbers(paks.base, f, suffixes)) {
            VanillaCostume vc;
            std::string why;
            if (!vanilla_costume_inventory(paks.base, f, no, suffixes, vc, &why)) {
                fprintf(stderr, "%s: %s\n", vanilla_costume_label(f, no).c_str(), why.c_str());
                failed++;
                continue;
            }
            if (!vc.fallback.empty()) {
                fprintf(stderr, "%s: layout not recognised (%s)\n", vanilla_costume_label(f, no).c_str(), vc.fallback.c_str());
                fallback++;
            }
            costumes.push_back(vanilla_costume_row(vc));
            for (auto& r : vanilla_file_rows(vc)) files.push_back(r);
        }
    std::map<std::string, std::string> sorted(suffixes.begin(), suffixes.end());
    std::string text = "#SUFFIXES\n";
    for (auto& [ext, ver] : sorted) text += ext + "\t" + ver + "\n";
    text += "#COSTUMES\n";
    for (auto& c : costumes) text += c + "\n";
    text += "#FILES\n";
    for (auto& r : files) text += r + "\n";
    if (!write_file(out, std::vector<uint8_t>(text.begin(), text.end()))) return 1;
    printf("%zu costumes, %zu files, %d failed, %d by path scan, %.0f ms\n",
           costumes.size(), files.size(), failed, fallback, ms_since(t0));
    return failed ? 2 : 0;
}

int names(Paks& paks) {
    auto n = game_fighter_names(paks.base);
    for (auto& [id, name] : n) printf("%d\t%s\t%s\n", id, name.c_str(), fighter_folder_name(name).c_str());
    return n.empty() ? 2 : 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) { fprintf(stderr, "usage: see tests/game_derived.cpp\n"); return 1; }
    const std::string cmd = argv[1];
    Paks paks;
    if (cmd == "structural" && argc >= 4) return paks.open(argv + 3, argv + argc) ? structural(argv[2], paks) : 1;
    if (cmd == "inventory" && argc >= 4) {
        bool heuristic = strcmp(argv[3], "--heuristic") == 0;
        char** first = argv + (heuristic ? 4 : 3);
        return paks.open(first, argv + argc) ? inventory(argv[2], heuristic, paks) : 1;
    }
    if (cmd == "names") return paks.open(argv + 2, argv + argc) ? names(paks) : 1;
    fprintf(stderr, "unknown command\n");
    return 1;
}
