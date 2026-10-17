#include "patch.hpp"
#include <string>
#include <cstring>
#include <cctype>
#include <algorithm>
#define BCDEC_IMPLEMENTATION
#include "bcdec.h"

namespace swaprepair {
uint32_t u32(const std::vector<uint8_t>& d, size_t o) { uint32_t n; memcpy(&n, d.data() + o, 4); return n; }
uint64_t u64(const std::vector<uint8_t>& d, size_t o) { uint64_t n; memcpy(&n, d.data() + o, 8); return n; }
bool range(uint64_t start, uint64_t count, uint64_t width, size_t length) {
    return start <= length && count <= (length - start) / width;
}
SwapRepairResult refused(const std::string& reason) { return {SwapRepairState::Refused, 0, reason}; }
std::string lower(std::string s) { for (char& c : s) { if (c == '\\') c = '/'; else c = (char)std::tolower((unsigned char)c); } return s; }
std::string fold(std::string s) { for (char& c : s) c = (char)std::tolower((unsigned char)c); return s; }
bool digits(const std::string& s) { return !s.empty() && std::all_of(s.begin(), s.end(), [](char c) { return c >= '0' && c <= '9'; }); }
bool fighter(const std::string& s) { return s.size() == 6 && s.substr(0, 3) == "esf" && digits(s.substr(3)); }

// Only the root RSZ instance table is inspected. No raw byte search and no speculative layout edit.
bool rsz_table(const std::vector<uint8_t>& d, bool scene, size_t& table, uint32_t& count) {
    const size_t header = scene ? 64 : 48;
    if (d.size() < header + 48 || memcmp(d.data(), scene ? "SCN\0" : "USR\0", 4)) return false;
    uint64_t r = u64(d, scene ? 56 : 32);
    if (r < header || r % 2 || !range(r, 48, 1, d.size()) || memcmp(d.data() + r, "RSZ\0", 4) || u32(d, (size_t)r + 4) != 16) return false;
    if (scene) {
    std::vector<std::pair<uint64_t, uint64_t>> spans;
    auto top = [&](uint64_t offset, uint32_t n, size_t width) {
        if (!n) return true;
        if (offset < 64 || offset % 4 || !range(offset, n, width, (size_t)r)) return false;
        spans.push_back({offset, offset + uint64_t(n) * width}); return true;
    };
    if (!top(64, u32(d, 4), 32) || !top(u64(d, 24), u32(d, 12), 8)
        || !top(u64(d, 32), u32(d, 8), 8) || !top(u64(d, 40), u32(d, 16), 8)
        || !top(u64(d, 48), u32(d, 20), 16)) return false;
    std::sort(spans.begin(), spans.end());
    for (size_t i = 1; i < spans.size(); ++i) if (spans[i].first < spans[i - 1].second) return false;
    }
    uint32_t objects = u32(d, (size_t)r + 8), users = u32(d, (size_t)r + 16);
    count = u32(d, (size_t)r + 12);
    uint64_t io = u64(d, (size_t)r + 24), data = u64(d, (size_t)r + 32), uo = u64(d, (size_t)r + 40);
    size_t remain = d.size() - (size_t)r;
    if (!count || !range(48, objects, 4, remain) || io < 48ull + 4ull * objects || io % 4 || !range(io, count, 8, remain)
        || data < io + 8ull * count || data > remain || (r + data) % 16 || !range(uo, users, 16, remain)
        || (users && (uo < io + 8ull * count || uo + 16ull * users > data))
        || (!users && uo && uo < io + 8ull * count)) return false;
    table = (size_t)(r + io);
    if (u32(d, table) || u32(d, table + 4)) return false;
    for (uint32_t i = 0; i < objects; ++i) if (u32(d, (size_t)r + 48 + 4ull * i) >= count) return false;
    return true;
}
bool scene_table(const std::vector<uint8_t>& d, size_t& table, uint32_t& count) { return rsz_table(d, true, table, count); }

// Recipe prefixes have fixed lengths. Match at UTF-16 boundaries and require the next slash so
// that 001 cannot match 0010. Everything outside those exact directory prefixes remains byte-exact.
size_t prefix(std::vector<uint8_t>& d, const std::string& from, const std::string& to) {
    if (from.size() != to.size()) return 0;
    size_t count = 0;
    for (size_t i = 0; i + 1 < d.size(); i += 2) {
        size_t start = i; std::string s;
        while (i + 1 < d.size() && !d[i + 1] && d[i] >= 32 && d[i] < 127) { s += (char)d[i]; i += 2; }
        if (i + 1 >= d.size() || d[i] || d[i + 1]) continue;
        size_t lead = s.find_first_not_of("@ ");
        if (lead == std::string::npos || fold(s.substr(lead)).compare(0, from.size() + 1, fold(from) + "/")) continue;
        for (size_t j = 0; j < to.size(); ++j) { d[start + 2 * (lead + j)] = (uint8_t)to[j]; d[start + 2 * (lead + j) + 1] = 0; }
        ++count;
    }
    return count;
}
std::string model_dir(const std::string& path) {
    const std::string head = "natives/stm/product/model/esf/";
    if (path.compare(0, head.size(), head)) return {};
    std::string tail = path.substr(head.size());
    if (tail.size() < 12 || tail[6] != '/' || tail[10] != '/' || !fighter(tail.substr(0, 6)) || !digits(tail.substr(7, 3)) || tail.find('/', 11) != std::string::npos) return {};
    return "Product/Model/esf/" + tail.substr(0, 10);
}
bool user_header(const std::vector<uint8_t>& d) {
    size_t table = 0; uint32_t count = 0;
    return rsz_table(d, false, table, count);
}
std::string model_parts_target(const std::vector<uint8_t>& d) {
    std::unordered_set<std::string> dirs;
    const std::string head = "product/model/esf/";
    for (size_t i = 0; i + 1 < d.size(); i += 2) {
        std::string s;
        while (i + 1 < d.size() && !d[i + 1] && d[i] >= 32 && d[i] < 127) { s += (char)d[i]; i += 2; }
        if (i + 1 >= d.size() || d[i] || d[i + 1]) continue;
        s = fold(s); auto lead = s.find_first_not_of("@ ");
        if (lead == std::string::npos) continue;
        s = s.substr(lead);
        if (s.compare(0, head.size(), head)) continue;
        s = s.substr(head.size());
        if (s.size() < 12 || s[6] != '/' || s[10] != '/' || !fighter(s.substr(0, 6)) || !digits(s.substr(7, 3))) continue;
        if (s.substr(7, 3) == "000") continue;
        if (s.substr(0, 6) != "esf030") return {};
        dirs.insert(s.substr(0, 10));
    }
    return dirs.size() == 1 ? "Product/Model/esf/" + *dirs.begin() : std::string();
}
}

SwapRepairResult repair_swap_scene(std::vector<uint8_t>& data, const std::vector<uint8_t>& current_official) {
    using namespace swaprepair;
    size_t table = 0; uint32_t count = 0;
    if (!scene_table(data, table, count)) return refused("malformed or unsupported SCN/RSZ instance table");
    constexpr uint32_t type = 0x63515A59, old_crc = 0xADA9B6AE, new_crc = 0xCD9A2E7B;
    std::vector<size_t> offsets;
    for (uint32_t i = 1; i < count; ++i) {
        size_t o = table + 8ull * i;
        if (u32(data, o) != type) continue;
        uint32_t crc = u32(data, o + 4);
        if (crc == old_crc) offsets.push_back(o + 4);
        else if (crc != new_crc) return refused("unknown Alex scene type CRC; scene preserved");
    }
    if (offsets.empty()) return {};
    if (!scene_table(current_official, table, count)) return refused("current official scene unavailable or unsupported");
    bool confirmed = false;
    for (uint32_t i = 1; i < count; ++i) {
        size_t o = table + 8ull * i;
        if (u32(current_official, o) != type) continue;
        if (u32(current_official, o + 4) != new_crc) return refused("current official scene does not confirm the audited Alex CRC");
        confirmed = true;
    }
    if (!confirmed) return refused("current official scene lacks the audited Alex type");
    for (size_t o : offsets) memcpy(data.data() + o, &new_crc, 4);
    return {SwapRepairState::Repaired, offsets.size(), "Alex scene type signature updated"};
}

SwapRepairResult repair_swap_user(std::vector<uint8_t>& data, const std::string& mod_path,
                                  const std::vector<SwapUserProfile>& profiles,
                                  const SwapOfficialRead& official_read, const SwapSha256& sha256) {
    using namespace swaprepair;
    const std::string path = lower(mod_path), name = path.substr(path.rfind('/') + 1);
    bool candidate_seen = false, repaired = false;
    std::string reason = "unrecognized or customized swap USER; preserved";
    std::vector<uint8_t> output;
    for (const auto& profile : profiles) {
        if (name != lower(profile.basename)) continue;
        std::string target;
        if (profile.kind == "chain" || profile.kind == "mai_self_chain") target = model_dir(path);
        else if (profile.kind == "model_parts" && path == "natives/stm/product/charparam/esf/esf030/battle_ud/esf030v01_model_parts.user.2") target = model_parts_target(data);
        if (target.empty()) continue;
        const std::string mai = "Product/Model/esf/esf028/";
        if (profile.kind == "mai_self_chain" && (target.compare(0, mai.size(), mai) || target == model_dir(lower(profile.donor_path)))) continue;
        candidate_seen = true;
        if (!official_read || !sha256 || !user_header(data)) return refused("swap USER input or repair services unavailable");
        std::string donor = profile.kind == "model_parts" ? "Product/Model/esf/esf030/001" : model_dir(lower(profile.donor_path));
        if (donor.empty() || profile.current_sha256.size() != 64 || profile.old_normalized_sha256.size() != 64) { reason = "incomplete swap repair profile"; continue; }
        auto current = official_read(profile.donor_path);
        if (!user_header(current) || lower(sha256(current)) != lower(profile.current_sha256)) { reason = "current official donor missing or changed; USER preserved"; continue; }
        if (!prefix(current, donor, target)) { reason = "current official donor lacks the documented model directory"; continue; }
        if (current == data) return {SwapRepairState::Unchanged, 0, "documented swap USER already current"};
        auto normalized = data;
        if (!prefix(normalized, target, "Product/Model/esf/esf000/000") || lower(sha256(normalized)) != lower(profile.old_normalized_sha256)) continue;
        if (repaired && output != current) return refused("ambiguous swap repair profile; USER preserved");
        output = std::move(current); repaired = true;
    }
    if (!candidate_seen) return {};
    if (!repaired) return refused(reason);
    data.swap(output);
    return {SwapRepairState::Repaired, 1, "documented swap USER rebuilt from current official donor"};
}

const std::vector<SwapUserProfile>& swap_user_profiles() {
    static const std::vector<SwapUserProfile> profiles = {
        {"chain", "esf028_001_01_chain.user.2", "natives/stm/product/model/esf/esf028/001/esf028_001_01_chain.user.2",
         "545126e3dbd0b816c87345a85d369966c28238ddca57df54078dc972987483c2", "1447e93a3804f3d679830b5ab1ff9d961d96d8ab0f14072d11844d98cbbaff01"},
        {"chain", "esf029_001_01_chain.user.2", "natives/stm/product/model/esf/esf029/001/esf029_001_01_chain.user.2",
         "b820dbb3fcd680ff9f7e3cba06be2bb35d1b86da97bad9d608fa546a54693c75", "abb9d96fa7b4666964c4e87c67ed7328e105322c4da4171d343bffa75e890495"},
        {"mai_self_chain", "esf028_001_30_chain.user.2", "natives/stm/product/model/esf/esf028/001/esf028_001_30_chain.user.2",
         "a7341623c4ffe40d0f364fd982f8b857c267fec8d4f70229be58a3baaf3ea557", "5327c7ecd5d6b373903bb84675705078cfe7e027f26f70e0be35def6a53d47f0"},
        {"mai_self_chain", "esf028_001_30_throw_chain.user.2", "natives/stm/product/model/esf/esf028/001/esf028_001_30_throw_chain.user.2",
         "eab461fb038e507a65219dd42dbd58d2418127fb0cdccbcafdd5e521ad15e33d", "c4a97af806f3da8fcd9ce315b1671e48ed23f8345cd98c4dc60567210c473f20"},
        {"mai_self_chain", "esf028_002_30_chain.user.2", "natives/stm/product/model/esf/esf028/002/esf028_002_30_chain.user.2",
         "1fd8da67664765a278639897b08a69c77cd8d2633c7d927b119705359ec2b3e6", "e100765efb0c5eb152aeb63dc026fd75ee2c42844635a4f121ec08354cd0e98f"},
        {"mai_self_chain", "esf028_002_30_throw_chain.user.2", "natives/stm/product/model/esf/esf028/002/esf028_002_30_throw_chain.user.2",
         "8019251583c9ee672cfe258a8904852a58f369e90d5c04d4451eada4b9778a78", "a8b7332b7ee6b60e57bb79c7fd53a223991c3b8b7f178279751124091ab4f628"},
    };
    return profiles;
}
SwapRepairResult repair_swap_file(std::vector<uint8_t>& data, const std::string& mod_path,
                                  const SwapOfficialRead& official_read, const SwapSha256& sha256) {
    const std::string path = swaprepair::lower(mod_path);
    if (path.compare(0, 8, "natives/")) return {};
    if (path.size() >= 7 && path.compare(path.size() - 7, 7, ".scn.20") == 0) {
        std::vector<uint8_t> current;
        if (official_read) {
            current = official_read(path);
            if (current.empty()) for (const char* fallback : {
                "natives/stm/product/charparam/esf/esf001/esf001v00.scn.20",
                "natives/stm/product/charparam/esf/esf002/esf002v00.scn.20",
                "natives/stm/product/charparam/esf/esf003/esf003v00.scn.20",
                "natives/stm/product/charparam/esf/esf005/esf005v00.scn.20",
                "natives/stm/product/charparam/esf/esf030/esf030v00.scn.20"}) {
                current = official_read(fallback); if (!current.empty()) break;
            }
        }
        return repair_swap_scene(data, current);
    }
    auto result = repair_swap_user(data, path, swap_user_profiles(), official_read, sha256);
    if (result.state == SwapRepairState::Unchanged && result.reason.empty()
        && path == "natives/stm/product/charparam/esf/esf030/battle_ud/esf030v01_model_parts.user.2")
        return swaprepair::refused("model-parts repair needs an approved older file profile; preserved");
    return result;
}

// ============================================================================
// Helpers
// ============================================================================

// Simple in-place search-and-replace for equal-length byte patterns.
static size_t replace_all(std::vector<uint8_t>& data,
                          const uint8_t* pat, const uint8_t* rep, size_t len) {
    if (len == 0 || data.size() < len) return 0;
    size_t count = 0;
    size_t limit = data.size() - len;
    for (size_t i = 0; i <= limit; ) {
        if (std::memcmp(data.data() + i, pat, len) == 0) {
            std::memcpy(data.data() + i, rep, len);
            i += len;
            ++count;
        } else {
            ++i;
        }
    }
    return count;
}

// Encode an ASCII string as UTF-16LE bytes (each char -> 2 bytes).
static std::vector<uint8_t> to_utf16le(const std::string& s) {
    std::vector<uint8_t> v;
    v.reserve(s.size() * 2);
    for (char c : s) {
        v.push_back(static_cast<uint8_t>(c));
        v.push_back(0);
    }
    return v;
}

static std::string to_upper(const std::string& s) {
    std::string r = s;
    for (auto& c : r) c = static_cast<char>(
        std::toupper(static_cast<unsigned char>(c)));
    return r;
}

// ============================================================================
// patch_paths
// ============================================================================

size_t patch_paths(std::vector<uint8_t>& data,
                   std::string_view fighter_dir,
                   std::string_view old_folder,
                   std::string_view new_folder) {
    // Build the two replacement pairs
    std::string old_slash = std::string(fighter_dir) + "/" +
                            std::string(old_folder) + "/";
    std::string new_slash = std::string(fighter_dir) + "/" +
                            std::string(new_folder) + "/";

    std::string old_under = std::string(fighter_dir) + "_" +
                            std::string(old_folder) + "_";
    std::string new_under = std::string(fighter_dir) + "_" +
                            std::string(new_folder) + "_";

    struct Pair { std::string old_s, new_s; };
    Pair pairs[] = { {old_slash, new_slash}, {old_under, new_under} };

    size_t total = 0;

    for (auto& [os, ns] : pairs) {
        // -- UTF-16LE, original case --
        auto o16 = to_utf16le(os);
        auto n16 = to_utf16le(ns);
        total += replace_all(data, o16.data(), n16.data(), o16.size());

        // -- UTF-8, original case (ASCII = UTF-8) --
        total += replace_all(data,
                             reinterpret_cast<const uint8_t*>(os.data()),
                             reinterpret_cast<const uint8_t*>(ns.data()),
                             os.size());

        // -- Uppercase variants --
        std::string os_up = to_upper(os);
        std::string ns_up = to_upper(ns);

        auto o16u = to_utf16le(os_up);
        auto n16u = to_utf16le(ns_up);
        total += replace_all(data, o16u.data(), n16u.data(), o16u.size());

        total += replace_all(data,
                             reinterpret_cast<const uint8_t*>(os_up.data()),
                             reinterpret_cast<const uint8_t*>(ns_up.data()),
                             os_up.size());
    }
    return total;
}

// ============================================================================
// patch_scene_minimal  (UTF-16LE scan, mesh/mdf2/CCVD only)
// ============================================================================

static std::string to_lower(const std::string& s) {
    std::string r = s;
    for (auto& c : r) c = static_cast<char>(
        std::tolower(static_cast<unsigned char>(c)));
    return r;
}

size_t patch_resource_references(std::vector<uint8_t>& data,
    const std::unordered_map<std::string, std::string>& relocated_resources) {
    if (relocated_resources.empty()) return 0;
    size_t changed = 0;
    for (size_t i = 0; i + 1 < data.size(); ) {
        const size_t start = i;
        std::string ref;
        while (i + 1 < data.size() && data[i + 1] == 0
               && data[i] >= 0x20 && data[i] < 0x7f) {
            ref += static_cast<char>(data[i]);
            i += 2;
        }
        const bool terminated = i + 1 < data.size() && data[i] == 0 && data[i + 1] == 0;
        i += 2;
        if (!terminated || ref.empty()) continue;
        size_t prefix = ref.find_first_not_of("@ ");
        if (prefix == std::string::npos) continue;
        std::string key = to_lower(ref.substr(prefix));
        for (char& c : key) if (c == '\\') c = '/';
        auto it = relocated_resources.find(key);
        if (it == relocated_resources.end() || it->second.size() != key.size()) continue;
        bool replaced = false;
        for (size_t k = 0; k < key.size(); ++k) {
            if (key[k] == it->second[k]) continue;
            data[start + 2 * (prefix + k)] = static_cast<uint8_t>(it->second[k]);
            replaced = true;
        }
        if (replaced) ++changed;
    }
    return changed;
}

// Read a null-terminated UTF-16LE string from buf starting at 'start'.
// Returns end position (exclusive, at the null terminator start).
static std::string read_u16_string(const uint8_t* buf, size_t len,
                                   size_t start, size_t* out_end) {
    std::string s;
    size_t i = start;
    while (i + 1 < len) {
        uint16_t c = buf[i] | (uint16_t(buf[i+1]) << 8);
        if (c == 0) { *out_end = i; return s; }
        s += static_cast<char>(c & 0xFF);
        i += 2;
    }
    *out_end = i;
    return s;
}

// Walk backward from pos (UTF-16LE aligned) to find string start
static size_t find_u16_string_start(const uint8_t* buf, size_t pos) {
    size_t s = pos;
    while (s >= 2) {
        if (buf[s - 2] == 0 && buf[s - 1] == 0) break;
        s -= 2;
    }
    return s;
}

static bool ends_with(const std::string& s, const char* suffix) {
    size_t sl = std::strlen(suffix);
    return s.size() >= sl && s.compare(s.size() - sl, sl, suffix) == 0;
}

size_t patch_scene_minimal(std::vector<uint8_t>& data,
                           std::string_view fighter_dir,
                           std::string_view old_folder,
                           std::string_view new_folder,
                           const std::unordered_set<std::string>* relocated_paths,
                           const std::unordered_map<std::string, std::string>* suffixes) {
    auto old_dir_b = to_utf16le(std::string(fighter_dir) + "/" +
                                std::string(old_folder) + "/");
    auto new_dir_b = to_utf16le(std::string(fighter_dir) + "/" +
                                std::string(new_folder) + "/");
    auto old_pfx_b = to_utf16le(std::string(fighter_dir) + "_" +
                                std::string(old_folder) + "_");
    auto new_pfx_b = to_utf16le(std::string(fighter_dir) + "_" +
                                std::string(new_folder) + "_");

    std::string old_dir_s = std::string(fighter_dir) + "/" +
                            std::string(old_folder) + "/";
    std::string new_dir_s = std::string(fighter_dir) + "/" +
                            std::string(new_folder) + "/";
    std::string old_pfx_s = std::string(fighter_dir) + "_" +
                            std::string(old_folder) + "_";
    std::string new_pfx_s = std::string(fighter_dir) + "_" +
                            std::string(new_folder) + "_";

    size_t total = 0;
    size_t dlen = old_dir_b.size();
    size_t plen = old_pfx_b.size();
    // Track per-string occurrence count for CCVD rule
    std::unordered_map<std::string, int> seen_count;

    for (size_t i = 0; i + dlen <= data.size(); ) {
        if (std::memcmp(data.data() + i, old_dir_b.data(), dlen) != 0) {
            i += 2; continue;
        }
        // Found old_dir pattern. Read full enclosing null-terminated UTF-16LE string.
        size_t str_start = find_u16_string_start(data.data(), i);
        size_t str_end;
        std::string full_str = read_u16_string(data.data(), data.size(),
                                                str_start, &str_end);
        std::string low = to_lower(full_str);

        // (.jcns: joint constraints a scene names directly, DriveTech's; followed like a mesh when
        // the slot has its own)
        bool is_mesh_mdf2 = ends_with(low, ".mesh") || ends_with(low, ".mdf2") || ends_with(low, ".jcns");
        bool is_ccvd = low.find("ccvd.user") != std::string::npos;
        // joint constraints, shapes and AO geometry: user data named after the outfit, like the
        // chain settings (a mod's own were left behind: sleeves held by them stayed in bind pose)
        bool is_chain_user = low.find("chain.user") != std::string::npos
                          || low.find("havok.user") != std::string::npos
                          || low.find("_jcs.user") != std::string::npos
                          || low.find("_shape.user") != std::string::npos
                          || low.find("_aogeo.user") != std::string::npos;
        bool is_userdata = is_ccvd || is_chain_user;
        bool is_visual = is_mesh_mdf2 || is_userdata;

        if (!is_visual) { i += dlen; continue; }

        // Existence check against relocated set
        if (relocated_paths && suffixes) {
            std::string wb = low;
            { auto p = wb.find(old_dir_s);
              if (p != std::string::npos) wb.replace(p, old_dir_s.size(), new_dir_s); }
            // For CCVD/chain.user: dir-only (filename prefix is semantic)
            if (!is_userdata) {
                auto p = wb.find(old_pfx_s);
                if (p != std::string::npos) wb.replace(p, old_pfx_s.size(), new_pfx_s);
            }
            // Strip leading '@'
            while (!wb.empty() && wb[0] == '@') wb.erase(0, 1);
            while (!wb.empty() && wb[0] == ' ') wb.erase(0, 1);
            // Get extension and build candidate pak path
            std::string bn = wb;
            { auto sl = bn.rfind('/'); if (sl != std::string::npos) bn = bn.substr(sl+1); }
            auto dot = bn.rfind('.');
            std::string ext = (dot != std::string::npos) ? bn.substr(dot+1) : "";
            std::string cand;
            auto sit = suffixes->find(ext);
            if (sit != suffixes->end()) {
                cand = "natives/stm/" + wb + "." + sit->second;
            }
            if (!cand.empty() && relocated_paths->find(cand) == relocated_paths->end()) {
                i += dlen; continue;
            }
        }

        // CCVD occurrence tracking
        std::string norm = low;
        while (!norm.empty() && norm.back() == '\0') norm.pop_back();
        seen_count[norm]++;
        // A scene names each user file twice: in its user data table, loaded with the scene, and
        // in the component that uses it. Both follow the slot. With the table left on the original
        // file, the slot's own was loaded late: Battle Settings, which does not wait, showed a
        // slot without its colours and without its cloth simulation (04/10).

        // Patch: replace dir segment
        std::memcpy(data.data() + i, new_dir_b.data(), dlen);
        total++;
        // For mesh/mdf2: also replace filename prefix.
        // For CCVD/chain.user: dir-only (the filename prefix is semantic).
        if (!is_userdata) {
            for (size_t j = i; j + plen <= str_end + 40 && j + plen <= data.size(); j += 2) {
                if (std::memcmp(data.data() + j, old_pfx_b.data(), plen) == 0) {
                    std::memcpy(data.data() + j, new_pfx_b.data(), plen);
                    total++;
                    break;
                }
            }
        }
        i += dlen;
    }
    return total;
}

// ============================================================================
// patch_mdf2_minimal  (UTF-16LE scan, mod textures only)
// ============================================================================

size_t patch_mdf2_minimal(std::vector<uint8_t>& data,
                          std::string_view fighter_dir,
                          std::string_view old_folder,
                          std::string_view new_folder,
                          const std::unordered_set<std::string>& mod_tex_keys) {
    std::string fd(fighter_dir), of(old_folder), nf(new_folder);
    auto old_dir = to_utf16le(fd + "/" + of + "/");
    auto new_dir = to_utf16le(fd + "/" + nf + "/");
    std::string old_pfx = to_lower(fd + "_" + of + "_");
    auto new_pfx16 = to_utf16le(to_lower(fd + "_" + nf + "_"));
    size_t dlen = old_dir.size();
    size_t total = 0;

    // References written with backslashes ("product\\model\\esf\\esf030\\002\\01\\glove_ALBD.tex", C. Viper
    // Trench Coat) are the same files as the game reads them, but not for the matching below: they are
    // written with slashes, in place, so that they follow the slot instead of pointing at a folder the
    // mod's textures left
    {
        const std::string pat = "product\\";
        for (size_t i = 0; i + 2 * pat.size() <= data.size(); i += 2) {
            bool eq = true;
            for (size_t k = 0; k < pat.size() && eq; k++)
                eq = data[i + 2 * k + 1] == 0 && std::tolower(data[i + 2 * k]) == pat[k];
            if (!eq) continue;
            for (size_t j = i; j + 1 < data.size() && data[j + 1] == 0 && data[j] >= 0x20 && data[j] < 0x7f; j += 2)
                if (data[j] == '\\') data[j] = '/';
        }
    }

    for (size_t i = 0; i + dlen <= data.size(); ) {
        if (std::memcmp(data.data() + i, old_dir.data(), dlen) != 0) {
            i += 2; continue;
        }
        // Rest of the reference after "<fighter>/<old>/", e.g. "01/esf009_001_01_ClothB_ALBD.tex"
        size_t str_end;
        std::string rest = read_u16_string(data.data(), data.size(), i + dlen, &str_end);
        while (!rest.empty() && rest.back() == '\0') rest.pop_back();
        std::string rest_low = to_lower(rest);

        // A doubled slash in a reference ("002/01//Knit_CMASK.tex") names the same file as the game
        // reads it (paths are hashed with slashes collapsed): match it as the single-slash path
        std::string key = rest_low;
        for (size_t q; (q = key.find("//")) != std::string::npos; ) key.erase(q, 1);
        if (mod_tex_keys.count(key)) {
            std::memcpy(data.data() + i, new_dir.data(), dlen);
            // Same stem rename as relocate_path(): the first "<fighter>_<old>_" of the file name
            size_t name_at = rest_low.rfind('/');
            name_at = (name_at == std::string::npos) ? 0 : name_at + 1;
            size_t p = rest_low.find(old_pfx, name_at);
            if (p != std::string::npos && new_pfx16.size() == old_pfx.size() * 2)
                std::memcpy(data.data() + i + dlen + 2 * p, new_pfx16.data(), new_pfx16.size());
            // Written back with a single slash where the mod had doubled it: the game is not known to
            // resolve "01//Knit_CMASK.tex" (a resource that never resolves stalls the character); the
            // string gets shorter, its end is filled with zeros
            if (key.size() != rest_low.size()) {
                // the rest with the renamed prefix, read again from the data
                std::string cur = read_u16_string(data.data(), data.size(), i + dlen, &str_end);
                for (size_t q; (q = cur.find("//")) != std::string::npos; ) cur.erase(q, 1);
                const size_t old_chars = rest.size();
                for (size_t k = 0; k < old_chars; k++) {
                    const uint16_t ch = k < cur.size() ? uint16_t((unsigned char)cur[k]) : 0;
                    data[i + dlen + 2 * k] = (uint8_t)(ch & 0xFF);
                    data[i + dlen + 2 * k + 1] = 0;
                }
            }
            total++;
        }
        i += dlen;
    }
    return total;
}

// ============================================================================
// Costume table reduction
// ============================================================================
// RSZ layout of the static table: USR header, RSZ header, one root in the object table, instance
// infos (type, crc), then instance data. Instances 1..root are the game's own messages and records,
// the root (app.FighterCostumeUserData, DataArray of record indices) comes next, then the table
// generator's 100 shared messages and the reserved records. Removing records at that tail only
// renumbers the tail, so the vanilla bytes are copied as they are and the tail is serialized again
// with the RSZ alignment rules (relative to the start of the data block, which is 16-aligned).

namespace {
constexpr uint32_t T_COSTUME_ROOT = 0x2f581bd0;  // app.FighterCostumeUserData
constexpr uint32_t T_COSTUME_REC  = 0x3bbf93ac;  // app.FighterCostumeUserDataRecord
constexpr uint32_t T_COSTUME_MSG  = 0x9b851e9b;  // ...Record.FighterCostumeMessage (id, GUID)
// id, ManageId, fighterId, isDefault(+3), costumeNo, messageId, isShowName(+3), sortNo, visualNo,
// arrangeId: every field is 4-aligned, so a record is always 40 bytes.
constexpr size_t COSTUME_REC_SIZE = 40;
constexpr size_t COSTUME_REC_MSG  = 20;          // offset of messageId in a record

size_t align_up(size_t x, size_t a) { return (x + a - 1) / a * a; }
template <class T> T rd(const std::vector<uint8_t>& b, size_t o) { T v; memcpy(&v, b.data() + o, sizeof(T)); return v; }
template <class T> void wr(std::vector<uint8_t>& b, size_t o, T v) { memcpy(b.data() + o, &v, sizeof(T)); }
template <class T> void put(std::vector<uint8_t>& b, T v) {
    size_t o = b.size(); b.resize(o + sizeof(T)); memcpy(b.data() + o, &v, sizeof(T));
}

struct CostumeTableLayout {
    size_t r = 0, d = 0;
    uint32_t root = 0, count = 0;
    std::vector<uint32_t> types, array;
    std::vector<size_t> start;
    std::unordered_map<uint32_t, uint32_t> crcs;
};

bool costume_table_layout(const std::vector<uint8_t>& b, CostumeTableLayout& t,
                          std::string& reason) {
    auto fail = [&](const char* s) { reason = s; return false; };
    // This format has no resource/user-data entries. Do not guess after a schema change.
    if (b.size() < 104 || memcmp(b.data(), "USR\0", 4) != 0
        || rd<uint32_t>(b, 4) || rd<uint32_t>(b, 8) || rd<uint32_t>(b, 12)
        || rd<uint64_t>(b, 16) != 48 || rd<uint64_t>(b, 24) != 48
        || rd<uint64_t>(b, 32) != 48)
        return fail("unsupported costume table USR header");
    t.r = 48;
    if (memcmp(b.data() + t.r, "RSZ\0", 4) != 0 || rd<uint32_t>(b, t.r + 4) != 16
        || rd<uint32_t>(b, t.r + 8) != 1 || rd<uint32_t>(b, t.r + 16) != 0
        || rd<uint64_t>(b, t.r + 24) != 52)
        return fail("unsupported costume table RSZ header");
    t.count = rd<uint32_t>(b, t.r + 12);
    t.root = rd<uint32_t>(b, t.r + 48);
    const uint64_t d = rd<uint64_t>(b, t.r + 32);
    if (t.count < 2 || t.count > (b.size() - t.r - 52) / 8
        || t.root == 0 || t.root >= t.count || d != rd<uint64_t>(b, t.r + 40)
        || d != align_up(52 + 8ull * t.count, 16) || d > b.size() - t.r
        || rd<uint64_t>(b, t.r + 52) != 0)
        return fail("invalid costume table offsets or instance count");
    t.d = t.r + size_t(d);
    t.types.resize(t.count); t.start.resize(t.count);
    size_t pos = 0;
    for (uint32_t i = 1; i < t.count; ++i) {
        uint32_t type = rd<uint32_t>(b, t.r + 52 + 8ull * i);
        uint32_t crc = rd<uint32_t>(b, t.r + 56 + 8ull * i);
        t.types[i] = type;
        auto c = t.crcs.emplace(type, crc);
        if (!c.second && c.first->second != crc) return fail("inconsistent costume table type signature");
        size_t s = align_up(pos, 4), end = 0;
        if (s > b.size() - t.d) return fail("truncated costume table data");
        if (type == T_COSTUME_MSG) end = align_up(s + 4, 8) + 16;
        else if (type == T_COSTUME_REC) end = s + COSTUME_REC_SIZE;
        else if (type == T_COSTUME_ROOT && i == t.root) {
            if (b.size() - t.d - s < 4) return fail("truncated costume table root");
            uint32_t n = rd<uint32_t>(b, t.d + s);
            if (n > (b.size() - t.d - s - 4) / 4) return fail("invalid costume table root array");
            end = s + 4 + 4ull * n;
            for (uint32_t k = 0; k < n; ++k) t.array.push_back(rd<uint32_t>(b, t.d + s + 4 + 4ull * k));
        } else return fail("unsupported costume table instance type or root");
        if (end > b.size() - t.d) return fail("truncated costume table instance");
        t.start[i] = s; pos = end;
    }
    if (t.types[t.root] != T_COSTUME_ROOT || pos != b.size() - t.d)
        return fail("invalid costume table root or trailing data");
    std::unordered_set<uint32_t> record_indices, record_ids;
    for (uint32_t x : t.array)
        if (x == 0 || x >= t.count || t.types[x] != T_COSTUME_REC || !record_indices.insert(x).second)
            return fail("invalid or duplicate costume table record reference");
    for (uint32_t i = 1; i < t.count; ++i) if (t.types[i] == T_COSTUME_REC) {
        size_t s = t.d + t.start[i];
        uint32_t m = rd<uint32_t>(b, s + COSTUME_REC_MSG);
        if (!record_indices.count(i) || !record_ids.insert(rd<uint32_t>(b, s)).second
            || m == 0 || m >= t.count || t.types[m] != T_COSTUME_MSG)
            return fail("invalid costume record id, membership or message reference");
    }
    return true;
}
}

bool refresh_costume_table(std::vector<uint8_t>& b,
                           const std::vector<uint8_t>& current_vanilla,
                           std::string* reason) {
    std::string error;
    auto fail = [&](const std::string& s) { if (reason) *reason = s; return false; };
    CostumeTableLayout old, now;
    if (!costume_table_layout(b, old, error)) return fail("shipped table: " + error);
    if (!costume_table_layout(current_vanilla, now, error)) return fail("current table: " + error);
    if (now.root + 1 != now.count) return fail("current vanilla table has instances after its root");
    if (old.crcs != now.crcs) return fail("costume table type signatures differ from the current game");

    std::unordered_set<uint32_t> record_ids, message_ids;
    std::unordered_set<uint64_t> costume_keys;
    std::unordered_set<std::string> message_guids;
    auto guid = [](const std::vector<uint8_t>& src, const CostumeTableLayout& t, uint32_t i) {
        size_t p = t.d + align_up(t.start[i] + 4, 8);
        return std::string(reinterpret_cast<const char*>(src.data() + p), 16);
    };
    for (uint32_t i = 1; i < now.count; ++i) {
        size_t p = now.d + now.start[i];
        if (now.types[i] == T_COSTUME_MSG) {
            message_ids.insert(rd<uint32_t>(current_vanilla, p));
            message_guids.insert(guid(current_vanilla, now, i));
        } else if (now.types[i] == T_COSTUME_REC) {
            record_ids.insert(rd<uint32_t>(current_vanilla, p));
            costume_keys.insert((uint64_t(rd<uint32_t>(current_vanilla, p + 8)) << 32)
                                  | rd<uint32_t>(current_vanilla, p + 16));
        }
    }
    std::vector<uint32_t> remap(old.count, UINT32_MAX);
    const uint64_t count = uint64_t(now.count) + old.count - old.root - 1;
    if (count > UINT32_MAX) return fail("too many costume table instances");
    for (uint32_t i = old.root + 1; i < old.count; ++i) {
        remap[i] = now.count + i - old.root - 1;
        size_t p = old.d + old.start[i];
        if (old.types[i] == T_COSTUME_MSG) {
            if (!message_ids.insert(rd<uint32_t>(b, p)).second || !message_guids.insert(guid(b, old, i)).second)
                return fail("generated costume message id or GUID collides with the current table");
        } else {
            uint32_t m = rd<uint32_t>(b, p + COSTUME_REC_MSG);
            if (m <= old.root) return fail("generated costume record references a vanilla message");
            if (!record_ids.insert(rd<uint32_t>(b, p)).second)
                return fail("generated costume record id collides with the current table");
            uint64_t key = (uint64_t(rd<uint32_t>(b, p + 8)) << 32) | rd<uint32_t>(b, p + 16);
            if (!costume_keys.insert(key).second)
                return fail("generated costume slot overlaps a current costume");
        }
    }
    std::vector<uint32_t> array = now.array;
    for (uint32_t i : old.array) if (i > old.root) array.push_back(remap[i]);
    const size_t new_data_off = align_up(52 + 8ull * count, 16);
    std::vector<uint8_t> out(current_vanilla.begin(), current_vanilla.begin() + now.r + 52 + 8ull * now.count);
    wr<uint32_t>(out, now.r + 12, uint32_t(count));
    wr<uint64_t>(out, now.r + 32, new_data_off);
    wr<uint64_t>(out, now.r + 40, new_data_off);
    out.insert(out.end(), b.begin() + old.r + 52 + 8ull * (old.root + 1),
                          b.begin() + old.r + 52 + 8ull * old.count);
    out.resize(now.r + new_data_off, 0);
    // Preserve the current game's vanilla bytes and instance indices exactly, including metadata.
    std::vector<uint8_t> data(current_vanilla.begin() + now.d,
                              current_vanilla.begin() + now.d + now.start[now.root]);
    auto pad = [&](size_t a) { data.resize(align_up(data.size(), a), 0); };
    pad(4); put<uint32_t>(data, uint32_t(array.size()));
    for (uint32_t x : array) put<uint32_t>(data, x);
    for (uint32_t i = old.root + 1; i < old.count; ++i) {
        size_t p = old.d + old.start[i];
        pad(4);
        if (old.types[i] == T_COSTUME_MSG) {
            put<uint32_t>(data, rd<uint32_t>(b, p)); pad(8);
            const std::string g = guid(b, old, i);
            data.insert(data.end(), g.begin(), g.end());
        } else {
            size_t at = data.size();
            data.insert(data.end(), b.begin() + p, b.begin() + p + COSTUME_REC_SIZE);
            wr<uint32_t>(data, at + COSTUME_REC_MSG, remap[rd<uint32_t>(b, p + COSTUME_REC_MSG)]);
        }
    }
    out.insert(out.end(), data.begin(), data.end());
    CostumeTableLayout checked;
    if (!costume_table_layout(out, checked, error)) return fail("rebased table: " + error);
    b.swap(out);
    if (reason) reason->clear();
    return true;
}

bool trim_costume_table(std::vector<uint8_t>& b,
                        const std::unordered_set<uint32_t>& keep_ids,
                        size_t* kept_records, size_t* removed_records,
                        const std::unordered_map<uint32_t, uint32_t>* message_for_record) {
    CostumeTableLayout layout;
    std::string error;
    if (!costume_table_layout(b, layout, error)) return false;
    const size_t R = layout.r, D = layout.d, inst_off = 52;
    const uint32_t ic = layout.count, root = layout.root;
    const auto& type = layout.types;
    const auto& start = layout.start;

    // Tail instances to keep: all messages, the records whose id is wanted
    std::vector<uint32_t> remap(ic, UINT32_MAX), kept;
    for (uint32_t i = 0; i <= (uint32_t)root; i++) remap[i] = i;
    size_t n_kept = 0, n_removed = 0;
    for (uint32_t i = (uint32_t)root + 1; i < ic; i++) {
        if (type[i] == T_COSTUME_REC) {
            if (!keep_ids.count(rd<uint32_t>(b, D + start[i]))) { n_removed++; continue; }
            n_kept++;
        }
        remap[i] = (uint32_t)root + 1 + (uint32_t)kept.size();
        kept.push_back(i);
    }
    // Messages of the tail by message id, at their new index
    std::unordered_map<uint32_t, uint32_t> msg_index;
    for (uint32_t i : kept)
        if (type[i] == T_COSTUME_MSG) msg_index[rd<uint32_t>(b, D + start[i])] = remap[i];
    const uint32_t n_arr = rd<uint32_t>(b, D + start[root]);
    std::vector<uint32_t> arr;
    for (uint32_t k = 0; k < n_arr; k++) {
        uint32_t x = rd<uint32_t>(b, D + start[root] + 4 + 4ull * k);
        if (x < ic && remap[x] != UINT32_MAX) arr.push_back(remap[x]);
    }

    // Headers, object table, instance infos
    const uint32_t new_ic = (uint32_t)root + 1 + (uint32_t)kept.size();
    const size_t new_data_off = align_up(inst_off + 8ull * new_ic, 16);
    std::vector<uint8_t> out(b.begin(), b.begin() + R + inst_off);
    wr<uint32_t>(out, R + 12, new_ic);
    wr<uint64_t>(out, R + 32, new_data_off);
    wr<uint64_t>(out, R + 40, new_data_off);
    out.insert(out.end(), b.begin() + R + inst_off, b.begin() + R + inst_off + 8ull * (root + 1));
    for (uint32_t i : kept)
        out.insert(out.end(), b.begin() + R + inst_off + 8ull * i, b.begin() + R + inst_off + 8ull * i + 8);
    out.resize(R + new_data_off, 0);

    // Data: vanilla instances as they are, then root, messages and kept records
    std::vector<uint8_t> data(b.begin() + D, b.begin() + D + start[root]);
    auto pad_to = [&](size_t a) { data.resize(align_up(data.size(), a), 0); };
    pad_to(4);
    put<uint32_t>(data, (uint32_t)arr.size());
    for (uint32_t x : arr) put<uint32_t>(data, x);
    for (uint32_t i : kept) {
        if (type[i] == T_COSTUME_MSG) {
            size_t guid = align_up(start[i] + 4, 8);
            pad_to(4); put<uint32_t>(data, rd<uint32_t>(b, D + start[i]));
            pad_to(8); data.insert(data.end(), b.begin() + D + guid, b.begin() + D + guid + 16);
        } else {
            uint32_t msg = rd<uint32_t>(b, D + start[i] + COSTUME_REC_MSG);
            if (msg >= ic || remap[msg] == UINT32_MAX) return false;
            uint32_t new_msg = remap[msg];
            if (message_for_record) {
                auto want = message_for_record->find(rd<uint32_t>(b, D + start[i]));
                if (want != message_for_record->end()) {
                    auto mi = msg_index.find(want->second);
                    if (mi != msg_index.end()) new_msg = mi->second;
                }
            }
            pad_to(4);
            size_t o = data.size();
            data.insert(data.end(), b.begin() + D + start[i], b.begin() + D + start[i] + COSTUME_REC_SIZE);
            wr<uint32_t>(data, o + COSTUME_REC_MSG, new_msg);
        }
    }
    out.insert(out.end(), data.begin(), data.end());
    b.swap(out);
    if (kept_records) *kept_records = n_kept;
    if (removed_records) *removed_records = n_removed;
    return true;
}

bool costume_table_records(const std::vector<uint8_t>& b, std::vector<std::pair<int, int>>& out) {
    CostumeTableLayout t;
    std::string error;
    if (!costume_table_layout(b, t, error) || t.root + 1 != t.count) return false;
    out.clear();
    for (uint32_t i = 1; i < t.count; i++)
        if (t.types[i] == T_COSTUME_REC)
            out.push_back({ (int)rd<uint32_t>(b, t.d + t.start[i] + 8), (int)rd<uint32_t>(b, t.d + t.start[i] + 16) });
    return true;
}

int costume_slot_index(int fighter) {
    if (fighter >= 1 && fighter <= 22) return fighter - 1;
    if (fighter >= 25) return fighter - 3;
    return 1000 + fighter;
}

bool guid_bytes(std::string_view text, std::vector<uint8_t>& out) {
    std::string hex;
    for (char c : text) if (c != '-') hex += c;
    if (hex.size() != 32) return false;
    uint8_t raw[16];
    for (int i = 0; i < 16; i++) {
        unsigned v = 0;
        for (int k = 0; k < 2; k++) {
            char c = (char)tolower((unsigned char)hex[2 * i + k]);
            if (c >= '0' && c <= '9') v = v * 16 + (c - '0');
            else if (c >= 'a' && c <= 'f') v = v * 16 + (c - 'a' + 10);
            else return false;
        }
        raw[i] = (uint8_t)v;
    }
    out.assign(16, 0);
    static const int order[16] = { 3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15 };
    for (int i = 0; i < 16; i++) out[i] = raw[order[i]];
    return true;
}

bool add_costume_records(std::vector<uint8_t>& b, const std::vector<CostumeSlotRecord>& records,
                         uint32_t msg_id_base, const std::vector<std::vector<uint8_t>>& message_guids,
                         std::string* err) {
    auto fail = [&](const std::string& why) { if (err) *err = why; return false; };
    CostumeTableLayout t;
    std::string error;
    if (!costume_table_layout(b, t, error)) return fail(error);
    if (t.root + 1 != t.count) return fail("the game's table already has instances after its root");
    uint32_t tmpl_rec = 0, tmpl_msg = 0;
    for (uint32_t i = 1; i < t.count; i++) {
        if (!tmpl_msg && t.types[i] == T_COSTUME_MSG) tmpl_msg = i;
        if (!tmpl_rec && t.types[i] == T_COSTUME_REC && rd<uint32_t>(b, t.d + t.start[i] + 8) == 1
            && rd<uint32_t>(b, t.d + t.start[i] + 16) == 0) tmpl_rec = i;
    }
    if (!tmpl_rec || !tmpl_msg) return fail("no record of fighter 1 / Outfit 1, or no message, to copy");
    for (auto& g : message_guids) if (g.size() != 16) return fail("a message GUID is not 16 bytes");
    for (auto& r : records) if (r.message >= message_guids.size()) return fail("a record names a missing message");

    const size_t R = t.r, inst_off = 52;
    const uint32_t n_msg = (uint32_t)message_guids.size(), n_rec = (uint32_t)records.size();
    const uint64_t new_count = uint64_t(t.count) + n_msg + n_rec;
    if (new_count > UINT32_MAX) return fail("too many costume table instances");
    const uint32_t new_ic = (uint32_t)new_count;
    const size_t new_data_off = align_up(inst_off + 8ull * new_ic, 16);

    // Headers and instance infos: the game's, then a copy of its message's and record's (type, crc)
    std::vector<uint8_t> out(b.begin(), b.begin() + R + inst_off + 8ull * t.count);
    wr<uint32_t>(out, R + 12, new_ic);
    wr<uint64_t>(out, R + 32, new_data_off);
    wr<uint64_t>(out, R + 40, new_data_off);
    const size_t info = R + inst_off;
    for (uint32_t k = 0; k < n_msg; k++)
        out.insert(out.end(), b.begin() + info + 8ull * tmpl_msg, b.begin() + info + 8ull * tmpl_msg + 8);
    for (uint32_t k = 0; k < n_rec; k++)
        out.insert(out.end(), b.begin() + info + 8ull * tmpl_rec, b.begin() + info + 8ull * tmpl_rec + 8);
    out.resize(R + new_data_off, 0);

    // Data: the game's instances up to the root, the root with the new records at the end of its array,
    // the messages, the records
    std::vector<uint8_t> data(b.begin() + t.d, b.begin() + t.d + t.start[t.root]);
    auto pad_to = [&](size_t a) { data.resize(align_up(data.size(), a), 0); };
    pad_to(4);
    put<uint32_t>(data, (uint32_t)t.array.size() + n_rec);
    for (uint32_t x : t.array) put<uint32_t>(data, x);
    for (uint32_t k = 0; k < n_rec; k++) put<uint32_t>(data, t.count + n_msg + k);
    for (uint32_t k = 0; k < n_msg; k++) {
        pad_to(4); put<uint32_t>(data, msg_id_base + k);
        pad_to(8); data.insert(data.end(), message_guids[k].begin(), message_guids[k].end());
    }
    for (auto& r : records) {
        pad_to(4);
        const size_t o = data.size();
        data.insert(data.end(), b.begin() + t.d + t.start[tmpl_rec], b.begin() + t.d + t.start[tmpl_rec] + COSTUME_REC_SIZE);
        wr<uint32_t>(data, o + 0, r.id);
        wr<uint32_t>(data, o + 4, r.id);                    // ManageId
        wr<uint32_t>(data, o + 8, r.fighter);
        wr<uint32_t>(data, o + 12, 0);                      // isDefault = false (+3 padding)
        wr<uint32_t>(data, o + 16, r.costume_no);
        wr<uint32_t>(data, o + COSTUME_REC_MSG, t.count + r.message);
        data[o + 25] = data[o + 26] = data[o + 27] = 0;     // isShowName kept, its padding cleared
        wr<uint32_t>(data, o + 28, r.sort_no);
        wr<uint32_t>(data, o + 32, r.costume_no);           // visualNo
        wr<uint32_t>(data, o + 36, 0);                      // arrangeId
    }
    out.insert(out.end(), data.begin(), data.end());
    CostumeTableLayout checked;
    if (!costume_table_layout(out, checked, error)) return fail("table with slot records: " + error);
    b.swap(out);
    return true;
}

// ============================================================================
// User file type signatures
// ============================================================================

namespace {
// Instance info table of a user file: {offset, count}, or {0, 0} when data is not one.
std::pair<size_t, uint32_t> user_instance_table(const std::vector<uint8_t>& b) {
    if (b.size() < 48 || memcmp(b.data(), "USR\0", 4) != 0) return {0, 0};
    const size_t R = (size_t)rd<uint64_t>(b, 32);
    if (R + 48 > b.size() || memcmp(b.data() + R, "RSZ\0", 4) != 0) return {0, 0};
    const uint32_t ic = rd<uint32_t>(b, R + 12);
    const size_t io = R + (size_t)rd<uint64_t>(b, R + 24);
    if (io + 8ull * ic > b.size()) return {0, 0};
    return {io, ic};
}
}

bool user_type_crcs(const std::vector<uint8_t>& b, std::unordered_map<uint32_t, uint32_t>& out) {
    auto [io, ic] = user_instance_table(b);
    if (!io) return false;
    for (uint32_t i = 1; i < ic; i++) out[rd<uint32_t>(b, io + 8ull * i)] = rd<uint32_t>(b, io + 8ull * i + 4);
    return true;
}

size_t upgrade_user_crcs(std::vector<uint8_t>& b,
                         const std::unordered_map<uint32_t, uint32_t>& current,
                         const std::unordered_set<uint32_t>& upgradable,
                         std::unordered_set<uint32_t>* stale_left) {
    auto [io, ic] = user_instance_table(b);
    if (!io) return 0;
    size_t n = 0;
    for (uint32_t i = 1; i < ic; i++) {
        const uint32_t type = rd<uint32_t>(b, io + 8ull * i), crc = rd<uint32_t>(b, io + 8ull * i + 4);
        auto it = current.find(type);
        if (it == current.end() || it->second == crc) continue;
        if (upgradable.count(type)) { wr<uint32_t>(b, io + 8ull * i + 4, it->second); n++; }
        else if (stale_left) stale_left->insert(type);
    }
    return n;
}

// ============================================================================
// Costume colour files in an older layout
// ============================================================================
// The colour files hold one tree of small records per garment and material (app.CostumeMaterialData.*).
// Their layouts are simple enough to be read and written here without a type database: every field is
// a Bool, a 4-byte value, an object reference, an array of references or a string.

namespace {
// b = Bool (1 byte), w = 4-byte value (F32, S32, via.Color), o = object, O = object array, s = string
struct CmdType { uint32_t type; const char* fields; };
const CmdType CMD_TYPES[] = {
    {0x6785d3d6, "O"},          // app.CostumeMaterialData: Parts
    {0x9e32f1f4, "wO"},         // MaterialData: Type, Clusters
    {0xd01d4c14, "sOoo"},       // ClusterData: Name, CustomizeColors, Emissive, Body
    {0x123a0e06, "bwo"},        // CustomizeColorData: Enable, Color, Option
    {0xac2691c9, "ooo"},        // CustomizeColorData.ColorOption: BlendRate, Roughness, Metalness
    {0xe6b613fb, "ooo"},        // BodyData: Cloth, Stitch, Hair
    {0x9f67268b, "oooo"},       // Cloth: DetailA_AOColor, Aniso specular, FakeCloth_Color, ClothFur_Color
    {0x739a9342, "ooooo"},      // ShellFurColor: fur front/deep colours, velvet rim pow/shading/rim intensity
    {0x87bc25ed, "oo"},         // Stitch
    {0x7fb59d14, "ooooooooo"},  // Hair
    {0x77577f78, "bw"},         // ColorData
    {0x016461b0, "bw"},         // FreeFloatData
    {0x2c5538dd, "bw"},         // MetalnessData
    {0x982fbf08, "bw"},         // RoughnessData
    {0xb7cfcbed, "bw"},         // EmissiveData
    {0xf3356698, "bw"},         // FloatData
    {0xf4f30deb, "bw"},         // VelvetData
    {0x9d1c5550, "bwwww"},      // Float4Data_Offset
};
constexpr uint32_t T_CLOTH = 0x9f67268b, T_SHELLFUR = 0x739a9342;
constexpr uint32_t CLOTH_CRC_2023 = 3731713379u;   // Cloth without ClothFur_Color (KenSFV, 2023)
constexpr size_t FUR_BLOCK = 6;                    // five values + the ShellFurColor

const char* cmd_layout(uint32_t type, uint32_t crc) {
    if (type == T_CLOTH && crc == CLOTH_CRC_2023) return "ooo";
    for (auto& t : CMD_TYPES) if (t.type == type) return t.fields;
    return nullptr;
}

struct CmdField { char kind; std::vector<uint8_t> raw; std::vector<uint32_t> refs; };
struct CmdInst { uint32_t type = 0, crc = 0; std::vector<CmdField> fields; };
struct CmdDoc {
    std::vector<uint8_t> head;      // USR header and RSZ header, rewritten on output
    size_t R = 0;
    std::vector<uint32_t> objects;
    std::vector<CmdInst> inst;      // inst[0] is the null instance
};

bool cmd_parse(const std::vector<uint8_t>& b, CmdDoc& doc) {
    if (b.size() < 48 || memcmp(b.data(), "USR\0", 4) != 0) return false;
    const size_t R = (size_t)rd<uint64_t>(b, 32);
    if (R + 48 > b.size() || memcmp(b.data() + R, "RSZ\0", 4) != 0) return false;
    const uint32_t oc = rd<uint32_t>(b, R + 8), ic = rd<uint32_t>(b, R + 12), udc = rd<uint32_t>(b, R + 16);
    const size_t inst_off = (size_t)rd<uint64_t>(b, R + 24), data_off = (size_t)rd<uint64_t>(b, R + 32);
    if (udc != 0 || data_off != (size_t)rd<uint64_t>(b, R + 40) || inst_off != 48 + 4ull * oc) return false;
    if (ic == 0 || R + inst_off + 8ull * ic > b.size() || R + data_off > b.size()) return false;
    doc.R = R;
    doc.head.assign(b.begin(), b.begin() + R + 48);
    doc.objects.resize(oc);
    for (uint32_t i = 0; i < oc; i++) doc.objects[i] = rd<uint32_t>(b, R + 48 + 4ull * i);
    doc.inst.assign(ic, CmdInst{});
    for (uint32_t i = 0; i < ic; i++) {
        doc.inst[i].type = rd<uint32_t>(b, R + inst_off + 8ull * i);
        doc.inst[i].crc  = rd<uint32_t>(b, R + inst_off + 8ull * i + 4);
    }
    const size_t D = R + data_off;
    size_t pos = 0;
    auto need = [&](size_t n) { return D + pos + n <= b.size(); };
    for (uint32_t i = 1; i < ic; i++) {
        const char* lay = cmd_layout(doc.inst[i].type, doc.inst[i].crc);
        if (!lay) return false;
        for (const char* k = lay; *k; k++) {
            CmdField f; f.kind = *k;
            if (*k == 'b') {
                if (!need(1)) return false;
                f.raw.push_back(b[D + pos]); pos += 1;
            } else {
                pos = align_up(pos, 4);
                if (!need(4)) return false;
                uint32_t v = rd<uint32_t>(b, D + pos); pos += 4;
                if (*k == 'w') { f.raw.resize(4); memcpy(f.raw.data(), &v, 4); }
                else if (*k == 'o') f.refs.push_back(v);
                else if (*k == 'O') {
                    if (!need(4ull * v)) return false;
                    for (uint32_t e = 0; e < v; e++) { f.refs.push_back(rd<uint32_t>(b, D + pos)); pos += 4; }
                } else if (*k == 's') {
                    if (!need(2ull * v)) return false;
                    f.raw.assign(b.begin() + D + pos, b.begin() + D + pos + 2ull * v); pos += 2ull * v;
                } else return false;
            }
            doc.inst[i].fields.push_back(std::move(f));
        }
    }
    return D + pos == b.size();
}

std::vector<uint8_t> cmd_write(const CmdDoc& doc, const std::unordered_map<uint32_t, uint32_t>* current) {
    const size_t R = doc.R, ic = doc.inst.size(), oc = doc.objects.size();
    const size_t inst_off = 48 + 4 * oc, data_off = align_up(inst_off + 8 * ic, 16);
    std::vector<uint8_t> out(doc.head);
    wr<uint32_t>(out, R + 12, (uint32_t)ic);
    wr<uint64_t>(out, R + 32, (uint64_t)data_off);
    wr<uint64_t>(out, R + 40, (uint64_t)data_off);
    for (uint32_t o : doc.objects) put<uint32_t>(out, o);
    for (auto& in : doc.inst) {
        uint32_t crc = in.crc;
        if (current && in.type) { auto it = current->find(in.type); if (it != current->end()) crc = it->second; }
        put<uint32_t>(out, in.type); put<uint32_t>(out, crc);
    }
    out.resize(R + data_off, 0);
    std::vector<uint8_t> d;
    auto pad4 = [&]() { d.resize(align_up(d.size(), 4), 0); };
    for (size_t i = 1; i < ic; i++)
        for (auto& f : doc.inst[i].fields) {
            if (f.kind == 'b') { d.push_back(f.raw[0]); continue; }
            pad4();
            if (f.kind == 'w') d.insert(d.end(), f.raw.begin(), f.raw.end());
            else if (f.kind == 'o') put<uint32_t>(d, f.refs[0]);
            else if (f.kind == 'O') { put<uint32_t>(d, (uint32_t)f.refs.size()); for (uint32_t r : f.refs) put<uint32_t>(d, r); }
            else if (f.kind == 's') { put<uint32_t>(d, (uint32_t)(f.raw.size() / 2)); d.insert(d.end(), f.raw.begin(), f.raw.end()); }
        }
    out.insert(out.end(), d.begin(), d.end());
    return out;
}
}

bool cmd_tints(const std::vector<uint8_t>& data, std::vector<CmdCluster>& out) {
    CmdDoc doc;
    if (!cmd_parse(data, doc) || doc.objects.empty()) return false;
    auto inst = [&](uint32_t i) -> const CmdInst* { return i < doc.inst.size() ? &doc.inst[i] : nullptr; };
    const CmdInst* root = inst(doc.objects[0]);
    if (!root || root->fields.empty()) return false;
    for (uint32_t m : root->fields[0].refs) {                  // MaterialData: Type, Clusters
        const CmdInst* md = inst(m);
        if (!md || md->fields.size() < 2) continue;
        for (uint32_t c : md->fields[1].refs) {                // ClusterData: Name, CustomizeColors...
            const CmdInst* cl = inst(c);
            if (!cl || cl->fields.size() < 2) continue;
            CmdCluster cc;
            const auto& raw = cl->fields[0].raw;
            for (size_t k = 0; k + 1 < raw.size(); k += 2) {
                const uint16_t ch = uint16_t(raw[k] | (raw[k + 1] << 8));
                if (!ch) break;
                cc.name += char(ch);
            }
            for (uint32_t e : cl->fields[1].refs) {            // CustomizeColorData: Enable, Color, Option
                const CmdInst* cd = inst(e);
                if (!cd || cd->fields.size() < 2 || cd->fields[0].raw.empty() || cd->fields[1].raw.size() < 4) continue;
                uint32_t rgba; memcpy(&rgba, cd->fields[1].raw.data(), 4);
                cc.colors.push_back({cd->fields[0].raw[0] != 0, rgba});
            }
            out.push_back(std::move(cc));
        }
    }
    return true;
}

bool costume_material_roundtrip(const std::vector<uint8_t>& data, std::vector<uint8_t>& out) {
    CmdDoc doc;
    if (!cmd_parse(data, doc)) return false;
    out = cmd_write(doc, nullptr);
    return true;
}

bool upgrade_costume_material_layout(std::vector<uint8_t>& data,
                                     const std::unordered_map<uint32_t, uint32_t>& current,
                                     const std::vector<uint8_t>& reference,
                                     size_t* garments_upgraded) {
    CmdDoc doc, ref;
    if (!cmd_parse(data, doc)) return false;
    auto is_old_cloth = [](const CmdInst& in) { return in.type == T_CLOTH && in.fields.size() == 3; };
    size_t n_old = 0;
    for (auto& in : doc.inst) if (is_old_cloth(in)) n_old++;
    if (!n_old || !cmd_parse(reference, ref)) return false;

    // The game's fur block: the first ShellFurColor of the reference and its five values
    const CmdInst* fur = nullptr;
    for (auto& in : ref.inst) if (in.type == T_SHELLFUR && in.fields.size() == 5) { fur = &in; break; }
    if (!fur) return false;
    std::vector<const CmdInst*> fur_values;
    for (auto& f : fur->fields) {
        if (f.refs.size() != 1 || f.refs[0] == 0 || f.refs[0] >= ref.inst.size()) return false;
        const CmdInst& v = ref.inst[f.refs[0]];
        for (auto& vf : v.fields) if (!vf.refs.empty()) return false;   // plain values only
        fur_values.push_back(&v);
    }

    // New indices: the block goes right before its garment, as in the game's files
    const size_t ic = doc.inst.size();
    std::vector<uint32_t> remap(ic);
    size_t shift = 0;
    for (size_t i = 0; i < ic; i++) {
        if (is_old_cloth(doc.inst[i])) shift += FUR_BLOCK;
        remap[i] = (uint32_t)(i + shift);
    }
    auto map_ref = [&](uint32_t r) -> uint32_t { return r < ic ? remap[r] : UINT32_MAX; };

    CmdDoc out;
    out.head = doc.head; out.R = doc.R;
    for (uint32_t o : doc.objects) { uint32_t m = map_ref(o); if (m == UINT32_MAX) return false; out.objects.push_back(m); }
    out.inst.reserve(ic + shift);
    for (size_t i = 0; i < ic; i++) {
        CmdInst in = doc.inst[i];
        for (auto& f : in.fields)
            for (auto& r : f.refs) { uint32_t m = map_ref(r); if (m == UINT32_MAX) return false; r = m; }
        if (is_old_cloth(in)) {
            const uint32_t base = remap[i] - (uint32_t)FUR_BLOCK;
            for (auto* v : fur_values) out.inst.push_back(*v);
            CmdInst sf = *fur;
            for (size_t k = 0; k < 5; k++) sf.fields[k].refs[0] = base + (uint32_t)k;
            out.inst.push_back(sf);
            CmdField ff; ff.kind = 'o'; ff.refs.push_back(base + 5);
            in.fields.push_back(ff);
        }
        out.inst.push_back(std::move(in));
    }
    data = cmd_write(out, &current);
    if (garments_upgraded) *garments_upgraded = n_old;
    return true;
}

// ============================================================================
// Texture mip tables
// ============================================================================
// Each level of a texture declares its row pitch and size. In every one of the game's 7,084 costume
// textures, the pitch covers a full row of blocks (or pixels) and the size is pitch x rows. Vegeta
// (June 2023, 6144x6144 BC7) declares a 24-byte pitch for its 6x6 level, whose block row takes 32:
// the costume never finishes loading. Only the header is wrong (its 64 bytes are 2 rows of 32).

namespace {
// Bytes per 4x4 block (block formats) or per pixel, by DXGI format; 0 when unknown
int tex_unit(int fmt, bool& block) {
    block = true;
    switch (fmt) {
        case 70: case 71: case 72: case 79: case 80: case 81: return 8;             // BC1, BC4
        case 73: case 74: case 75: case 76: case 77: case 78: case 82: case 83: case 84:
        case 94: case 95: case 96: case 97: case 98: case 99: return 16;           // BC2 BC3 BC5 BC6H BC7
    }
    block = false;
    switch (fmt) {
        case 2: return 16;
        case 10: return 8;
        case 24: case 26: case 28: case 29: case 87: case 88: case 91: return 4;
        case 49: case 56: return 2;
        case 61: return 1;
    }
    return 0;
}
}

int texture_mip_check(std::vector<uint8_t>& b, bool repair, int* repaired) {
    if (b.size() < 40 || memcmp(b.data(), "TEX\0", 4) != 0) return 0;
    const uint16_t w = rd<uint16_t>(b, 8), h = rd<uint16_t>(b, 10);
    const int n = b[15] / 16, images = b[14] ? b[14] : 1;
    bool block = false;
    const int unit = tex_unit(rd<int32_t>(b, 16), block);
    if (!unit || n <= 0 || 40 + 16ull * n * images > b.size()) return 0;
    int bad = 0;
    for (int img = 0; img < images; img++)
        for (int i = 0; i < n; i++) {
            const size_t at = 40 + 16ull * (size_t(img) * n + i);
            const uint32_t mw = std::max(1, w >> i), mh = std::max(1, h >> i);
            const uint32_t row = block ? ((mw + 3) / 4) * unit : mw * unit;
            const uint32_t rows = block ? (mh + 3) / 4 : mh;
            const int32_t pitch = rd<int32_t>(b, at + 8), size = rd<int32_t>(b, at + 12);
            if (pitch >= int32_t(row) && size == pitch * int32_t(rows)) continue;
            if (repair && size == int32_t(row * rows)) {
                wr<int32_t>(b, at + 8, int32_t(row));
                if (repaired) (*repaired)++;
                continue;
            }
            bad++;
        }
    if (!bad || !repair || images != 1) return bad;
    // Some tools compute the levels whose side is not a multiple of 4 with fractional blocks but
    // write whole blocks (Feixue for Mai, 6144x6144 BC7: the 6x6 level declared 24 x 1.5 = 36 bytes,
    // 64 written): the declared table then stops short of the end of the file, while the table
    // computed from the format fills it exactly. Only then is the whole table rewritten.
    uint64_t off = rd<uint64_t>(b, 40);
    for (int i = 0; i < n; i++) {
        const uint32_t mw = std::max(1, w >> i), mh = std::max(1, h >> i);
        const uint64_t row = block ? ((mw + 3) / 4) * unit : uint64_t(mw) * unit;
        off += row * (block ? (mh + 3) / 4 : mh);
    }
    if (off != b.size()) return bad;
    off = rd<uint64_t>(b, 40);
    for (int i = 0; i < n; i++) {
        const size_t at = 40 + 16ull * i;
        const uint32_t mw = std::max(1, w >> i), mh = std::max(1, h >> i);
        const uint32_t row = block ? ((mw + 3) / 4) * unit : mw * unit;
        const uint32_t size = row * (block ? (mh + 3) / 4 : mh);
        wr<uint64_t>(b, at, off);
        wr<int32_t>(b, at + 8, int32_t(row));
        wr<int32_t>(b, at + 12, int32_t(size));
        off += size;
    }
    if (repaired) (*repaired) += bad;
    return 0;
}

namespace {
std::string mdf_str(const std::vector<uint8_t>& b, uint64_t o) {
    std::string s;
    for (size_t k = size_t(o); o && k + 1 < b.size(); k += 2) {
        const uint16_t c = uint16_t(b[k] | (b[k + 1] << 8));
        if (!c) break;
        s += char(c);
    }
    return s;
}
struct MdfTex { std::string param, path; size_t entry_at; };
struct MdfParam { std::string name; uint32_t offset = 0, count = 0; };
struct MdfMat { std::string name, master; uint32_t block = 0, params = 0, flags = 0; size_t header = 0;
                uint64_t param_data = 0; std::vector<MdfParam> plist; std::vector<MdfTex> tex; };
bool mdf_read(const std::vector<uint8_t>& b, std::vector<MdfMat>& out) {
    if (b.size() < 16 || memcmp(b.data(), "MDF\0", 4) != 0) return false;
    int16_t count; memcpy(&count, b.data() + 6, 2);
    if (count <= 0 || 16 + 100ull * count > b.size()) return false;
    for (int i = 0; i < count; i++) {
        const size_t h = 16 + 100ull * i;
        uint64_t name_off, tex_off; uint32_t tex_count;
        memcpy(&name_off, b.data() + h, 8); memcpy(&tex_count, b.data() + h + 20, 4); memcpy(&tex_off, b.data() + h + 60, 8);
        MdfMat m; m.name = mdf_str(b, name_off);
        memcpy(&m.block, b.data() + h + 12, 4); memcpy(&m.params, b.data() + h + 16, 4);
        memcpy(&m.flags, b.data() + h + 40, 4); m.header = h;
        uint64_t master_off; memcpy(&master_off, b.data() + h + 84, 8);
        m.master = mdf_str(b, master_off);
        {
            uint64_t ph, pd; memcpy(&ph, b.data() + h + 52, 8); memcpy(&pd, b.data() + h + 76, 8);
            m.param_data = pd;
            for (uint32_t k = 0; k < m.params; k++) {
                const size_t e = size_t(ph) + 24ull * k;
                if (e + 24 > b.size()) return false;
                uint64_t no; MdfParam pr; memcpy(&no, b.data() + e, 8);
                memcpy(&pr.offset, b.data() + e + 16, 4); memcpy(&pr.count, b.data() + e + 20, 4);
                pr.name = mdf_str(b, no);
                m.plist.push_back(std::move(pr));
            }
        }
        for (uint32_t t = 0; t < tex_count; t++) {
            const size_t e = size_t(tex_off) + 32ull * t;
            if (e + 32 > b.size()) return false;
            uint64_t po, ph; memcpy(&po, b.data() + e, 8); memcpy(&ph, b.data() + e + 16, 8);
            m.tex.push_back({mdf_str(b, po), mdf_str(b, ph), e});
        }
        out.push_back(std::move(m));
    }
    return true;
}
std::string mdf_low(std::string s) { for (auto& c : s) c = char(std::tolower((unsigned char)c)); return s; }
}

bool modernize_mdf2(const std::vector<uint8_t>& mod, const std::vector<uint8_t>& game,
                    std::vector<uint8_t>& out, int* rebound) {
    std::vector<MdfMat> mm, gm;
    if (!mdf_read(mod, mm) || !mdf_read(game, gm) || mm.size() != gm.size()) return false;
    for (auto& g : gm) {
        bool found = false;
        for (auto& m : mm) if (m.name == g.name) { found = true; break; }
        if (!found) return false;
    }
    // Only a file made for an older layout of the material: every material has the game's name and master
    // material, and exactly the parameters the game's has minus what it gained since (16 bytes each; Dance
    // Outfit for Cammy, 2023: one parameter less in all its 10 materials). A mod that changes parameters on
    // purpose has another shape (Yasmine Shorts: more bytes, the same count) and is left alone.
    for (auto& g : gm)
        for (auto& m : mm) {
            if (m.name != g.name) continue;
            if (m.master != g.master || g.params <= m.params || g.block <= m.block
                || g.block - m.block != 16u * (g.params - m.params)) return false;
        }
    std::vector<uint8_t> res = game;
    int n = 0;
    // The mod's rendering flags of a material (header +40: the game's leotard has 0x1800_0018, the mod's, sheer,
    // 0x1800_001B) are what makes fabric translucent: they stay the mod's
    for (auto& g : gm)
        for (auto& m : mm) {
            if (m.name != g.name) continue;
            if (m.flags != g.flags) memcpy(res.data() + g.header + 40, &m.flags, 4);
            // The mod's parameter values, by name (alpha test, dissolve, base colour...): what the mod set
            // for the material stays the mod's; a parameter the game gained keeps the game's value
            for (auto& gp : g.plist)
                for (auto& mp : m.plist) {
                    if (mp.name != gp.name || mp.count != gp.count) continue;
                    const size_t src = size_t(m.param_data) + mp.offset, dst = size_t(g.param_data) + gp.offset;
                    if (src + 4ull * gp.count <= mod.size() && dst + 4ull * gp.count <= res.size())
                        memcpy(res.data() + dst, mod.data() + src, 4ull * gp.count);
                    break;
                }
        }
    for (auto& g : gm)
        for (auto& m : mm) {
            if (m.name != g.name) continue;
            for (auto& gt : g.tex)
                for (auto& mt : m.tex) {
                    if (mt.param != gt.param || mt.path.empty() || mdf_low(mt.path) == mdf_low(gt.path)) continue;
                    // the mod's path goes to the end of the file, the binding points at it
                    const uint64_t at = res.size();
                    for (char c : mt.path) { res.push_back((uint8_t)c); res.push_back(0); }
                    res.push_back(0); res.push_back(0);
                    memcpy(res.data() + gt.entry_at + 16, &at, 8);
                    n++;
                    break;
                }
        }
    out = std::move(res);
    if (rebound) *rebound = n;
    return true;
}

bool texture_channel_means(const std::vector<uint8_t>& b, float out[4]) {
    if (b.size() < 40 || memcmp(b.data(), "TEX\0", 4) != 0) return false;
    const uint16_t w = rd<uint16_t>(b, 8), h = rd<uint16_t>(b, 10);
    const int n = b[15] / 16, fmt = rd<int32_t>(b, 16);
    int bs = 0;
    switch (fmt) {
        case 71: case 72: case 80: bs = 8; break;               // BC1, BC4
        case 77: case 78: case 83: case 98: case 99: bs = 16; break;   // BC3, BC5, BC7
        default: return false;
    }
    for (int i = 0; i < n; i++) {
        const uint32_t mw = std::max(1, w >> i), mh = std::max(1, h >> i);
        if (mw > 64 || mh > 64) continue;
        // the first level that small only
        if (40 + 16ull * (i + 1) > b.size() || mw < 4 || mh < 4) return false;
        const uint64_t off = rd<uint64_t>(b, 40 + 16ull * i);
        const uint32_t bw = (mw + 3) / 4, bh = (mh + 3) / 4;
        if (off + uint64_t(bw) * bh * bs > b.size()) return false;
        double sum[4] = {0, 0, 0, 0};
        uint8_t px[4 * 4 * 4];
        for (uint32_t by = 0; by < bh; by++)
            for (uint32_t bx = 0; bx < bw; bx++) {
                const uint8_t* blk = b.data() + off + (uint64_t(by) * bw + bx) * bs;
                memset(px, 0, sizeof px);
                switch (fmt) {
                    case 71: case 72: bcdec_bc1(blk, px, 16); break;
                    case 77: case 78: bcdec_bc3(blk, px, 16); break;
                    case 98: case 99: bcdec_bc7(blk, px, 16); break;
                    case 80: {   // one channel: red
                        uint8_t r[16]; bcdec_bc4(blk, r, 4);
                        for (int k = 0; k < 16; k++) px[4 * k] = r[k];
                        break;
                    }
                    case 83: {   // two channels: red, green
                        uint8_t rg[32]; bcdec_bc5(blk, rg, 8);
                        for (int k = 0; k < 16; k++) { px[4 * k] = rg[2 * k]; px[4 * k + 1] = rg[2 * k + 1]; }
                        break;
                    }
                }
                for (uint32_t y = 0; y < 4 && by * 4 + y < mh; y++)
                    for (uint32_t x = 0; x < 4 && bx * 4 + x < mw; x++)
                        for (int c = 0; c < 4; c++) sum[c] += px[(y * 4 + x) * 4 + c];
            }
        for (int c = 0; c < 4; c++) out[c] = float(sum[c] / (255.0 * mw * mh));
        return true;
    }
    return false;
}
