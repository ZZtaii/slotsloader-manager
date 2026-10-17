// Native regression checks for PakWriter exclusions and the existing dedup path.
// Link with the actual common/pak.cpp, miniz and zstd objects; no test double.
// Usage: pak_exclusions.exe <new workspace fixture .pak path>
#include "../common/pak.hpp"
#include <cstring>
#include <stdexcept>

static int passed = 0, total = 0;
static void check(const char* label, bool ok) {
    ++total;
    if (ok) ++passed;
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", label);
}
static std::vector<uint8_t> bytes(const char* text) {
    return std::vector<uint8_t>(text, text + std::strlen(text));
}
static uint64_t hash(const char* name) {
    return pak_path_hash(std::string_view(name));
}

int main(int argc, char** argv) {
    if (argc != 2) {
        std::fprintf(stderr, "usage: pak_exclusions.exe <new fixture .pak path>\n");
        return 2;
    }
    // Never overwrite an earlier fixture or a user archive.
    if (FILE* existing = std::fopen(argv[1], "rb")) {
        std::fclose(existing);
        std::fprintf(stderr, "output already exists: %s\n", argv[1]);
        return 2;
    }

    const uint64_t blocked_stored = hash("natives/stm/product/model/esf/esf002/003/01/original.tex.241101895");
    const uint64_t blocked_raw = hash("natives/stm/streaming/product/model/esf/esf002/003/01/original.tex.241101895");
    const uint64_t stored_a = hash("natives/stm/product/model/esf/esf002/006/01/private_a.tex.241101895");
    const uint64_t stored_b = hash("natives/stm/product/model/esf/esf002/006/01/private_b.tex.241101895");
    const uint64_t compressed_a = hash("natives/stm/streaming/product/model/esf/esf002/006/01/private_a.tex.241101895");
    const uint64_t compressed_b = hash("natives/stm/streaming/product/model/esf/esf002/006/01/private_b.tex.241101895");
    const uint64_t compressed_c = hash("natives/stm/streaming/product/model/esf/esf002/006/01/private_c.tex.241101895");
    const uint64_t custom = hash("natives/stm/product/model/esf/esf002/001/01/manual_unique.tex.241101895");
    const auto shared = bytes("stored alias fixture");
    const auto replacement = bytes("replacement private owner");
    const auto custom_bytes = bytes("unique dependency in an original costume folder");
    const std::vector<uint8_t> compressed = {
        0xcb, 0x4b, 0x2c, 0xc9, 0x2c, 0x4b, 0x55, 0x48, 0xce, 0xcf,
        0x2d, 0x28, 0x4a, 0x2d, 0x2e, 0x4e, 0x4d, 0x51, 0x48, 0xcc,
        0xc9, 0x4c, 0x2c, 0x56, 0x48, 0xcb, 0xac, 0x28, 0x29, 0x2d,
        0x4a, 0x65, 0xc8, 0xa3, 0x50, 0x1e, 0x00
    };
    const char compressed_text[] = "native compressed alias fixture";
    std::vector<uint8_t> decompressed;
    for (int i = 0; i < 3; ++i)
        decompressed.insert(decompressed.end(), compressed_text,
                            compressed_text + sizeof(compressed_text));

    PakWriter writer;
    writer.exclude_hashes({blocked_stored, blocked_raw});
    writer.add_raw(blocked_raw, compressed, 1, int64_t(decompressed.size()));
    writer.add_uncompressed(blocked_stored, shared);
    check("excluded pushes create no entries or hash lookups",
          writer.entry_count() == 0 && !writer.find(blocked_stored) && !writer.find(blocked_raw)
          && writer.get(blocked_stored).empty() && !writer.peek(blocked_stored));
    check("excluded pushes leave dedup counters clean",
          writer.dedup_count() == 0 && writer.dedup_bytes() == 0);
    check("excluded diagnostics count distinct hashes", writer.excluded_write_count() == 2);
    writer.add_raw(blocked_raw, compressed, 1, int64_t(decompressed.size()));
    check("repeated rejected push does not grow state",
          writer.entry_count() == 0 && writer.excluded_write_count() == 2);

    writer.add_uncompressed(stored_a, shared);
    writer.add_uncompressed(blocked_stored, shared);
    writer.add_uncompressed(stored_b, shared);
    check("stored aliases use a permitted owner after rejection",
          writer.entry_count() == 2 && writer.find(stored_a)->alias == -1
          && writer.find(stored_b)->alias == 0 && writer.get(stored_b) == shared);
    check("rejected identical data is absent from dedup accounting",
          writer.dedup_count() == 1 && writer.dedup_bytes() == shared.size());

    writer.add_raw(compressed_a, compressed, 1, int64_t(decompressed.size()));
    writer.add_raw(blocked_raw, compressed, 1, int64_t(decompressed.size()));
    writer.add_raw(compressed_b, compressed, 1, int64_t(decompressed.size()));
    writer.add_raw(compressed_c, compressed, 1, int64_t(decompressed.size()));
    check("compressed aliases decode through the real native decompressor",
          writer.entry_count() == 5 && writer.get(compressed_a) == decompressed
          && writer.get(compressed_b) == decompressed && writer.get(compressed_c) == decompressed);
    check("compressed aliases retain a valid permitted owner",
          writer.find(compressed_b)->alias == 2 && writer.find(compressed_c)->alias == 2
          && !writer.peek(compressed_b));
    check("rejected compressed push does not enter dedup state",
          writer.dedup_count() == 3
          && writer.dedup_bytes() == shared.size() + compressed.size() * 2);

    check("excluded replace refuses new stored and compressed originals",
          !writer.replace(blocked_stored, replacement)
          && !writer.replace(blocked_raw, replacement)
          && writer.entry_count() == 5 && writer.excluded_write_count() == 2
          && !writer.find(blocked_stored) && !writer.find(blocked_raw));
    check("rejected replacements preserve permitted payloads",
          writer.get(stored_a) == shared && writer.get(stored_b) == shared
          && writer.get(compressed_b) == decompressed);

    check("replacing stored owner preserves its old alias",
          writer.replace(stored_a, replacement)
          && writer.get(stored_a) == replacement && writer.get(stored_b) == shared
          && writer.find(stored_b)->alias == -1);
    check("replacing compressed owner preserves all former aliases",
          writer.replace(compressed_a, replacement)
          && writer.get(compressed_a) == replacement
          && writer.get(compressed_b) == decompressed && writer.get(compressed_c) == decompressed
          && writer.find(compressed_b)->alias == -1 && writer.find(compressed_c)->alias == 3);
    const auto alias_replacement = bytes("new data for the former compressed alias");
    check("replacing compressed alias leaves the compressed owner intact",
          writer.replace(compressed_c, alias_replacement)
          && writer.get(compressed_c) == alias_replacement
          && writer.get(compressed_b) == decompressed);
    writer.add_uncompressed(custom, custom_bytes);
    check("safe custom dependency is allowed inside a vanilla costume folder",
          writer.get(custom) == custom_bytes && !writer.is_excluded(custom));

    bool late_threw = false;
    try { writer.exclude_hashes({stored_a}); }
    catch (const std::logic_error&) { late_threw = true; }
    check("late exclusion configuration throws without changing protection",
          late_threw && writer.is_excluded(blocked_stored)
          && !writer.is_excluded(stored_a) && writer.get(stored_a) == replacement
          && writer.excluded_write_count() == 2);

    const bool written = writer.write(argv[1]);
    check("native writer emits the fixture PAK", written);
    PakReader reader;
    const bool opened = written && reader.open(argv[1]);
    check("native reader sees only six permitted keys", opened && reader.entry_count() == 6);
    check("excluded original keys are absent from serialized PAK",
          opened && !reader.find(blocked_stored) && !reader.find(blocked_raw));
    auto read_equals = [&](uint64_t key, const std::vector<uint8_t>& expected) {
        const PakEntry* entry = opened ? reader.find(key) : nullptr;
        return entry && reader.read(*entry) == expected;
    };
    check("stored owner and old alias survive disk roundtrip",
          read_equals(stored_a, replacement) && read_equals(stored_b, shared));
    check("compressed alias survives disk roundtrip with compression preserved",
          read_equals(compressed_b, decompressed)
          && reader.find(compressed_b)->compression() == 1
          && reader.find(compressed_b)->compressed_size == int64_t(compressed.size()));
    check("replaced compressed entries survive disk roundtrip",
          read_equals(compressed_a, replacement) && read_equals(compressed_c, alias_replacement));
    check("custom dependency survives disk roundtrip", read_equals(custom, custom_bytes));
    std::printf("%d/%d passed\n", passed, total);
    return passed == total ? 0 : 1;
}
