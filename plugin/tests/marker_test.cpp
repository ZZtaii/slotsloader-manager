// Unit test of plugin/src/marker.hpp: the private opt-out marker of the owned-outfit check.
// The marker's name is not in this repository: give it in the environment variable
// SLOTS_OPTOUT_NAME (the test fails without it). Folders are made under %TEMP% and removed.
// Build and run: build_ownership_test.bat.
#include "../src/marker.hpp"

#include <cstdio>
#include <cstdlib>
#include <cwctype>
#include <string>
#include <vector>

using slots::marker::fnv1a64;
using slots::marker::folded_name;
using slots::marker::kOptOutNameHash;
using slots::marker::opt_out_present;

static int fails = 0;
static void check(bool ok, const char* what) {
    std::printf("  %-78s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++fails;
}

static std::wstring g_root;

// A new folder holding these files (and one sub-folder holding `nested`, if given)
static std::wstring folder(const wchar_t* tag, const std::vector<std::wstring>& files,
                           const std::wstring& nested = L"") {
    std::wstring d = g_root + L"\\" + tag;
    CreateDirectoryW(d.c_str(), nullptr);
    for (auto& f : files) {
        HANDLE h = CreateFileW((d + L"\\" + f).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
    if (!nested.empty()) {
        CreateDirectoryW((d + L"\\sub").c_str(), nullptr);
        HANDLE h = CreateFileW((d + L"\\sub\\" + nested).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
    return d;
}

static void remove_tree(const std::wstring& dir) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            std::wstring n = fd.cFileName;
            if (n == L"." || n == L"..") continue;
            std::wstring p = dir + L"\\" + n;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) remove_tree(p);
            else DeleteFileW(p.c_str());
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

int main() {
    wchar_t env[512];
    DWORD n = GetEnvironmentVariableW(L"SLOTS_OPTOUT_NAME", env, 512);
    if (n == 0 || n >= 512) {
        std::printf("SLOTS_OPTOUT_NAME is not set: the marker cases cannot run\n1 FAILED\n");
        return 1;
    }
    std::wstring name(env, n), upper = name;
    for (auto& c : upper) c = (wchar_t)std::towupper(c);

    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    g_root = std::wstring(tmp) + L"slots_marker_test_" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(g_root.c_str(), nullptr);

    check(fnv1a64("") == 0xcbf29ce484222325ull, "FNV-1a 64 of the empty string");
    check(fnv1a64("a") == 0xaf63dc4c8601ec8cull, "FNV-1a 64 of \"a\"");
    check(folded_name(L"AbC.TXT") == "abc.txt", "names are compared lowercased");
    check(fnv1a64(folded_name(name.c_str())) == kOptOutNameHash, "the given name has the marker's hash");

    check(opt_out_present(folder(L"exact", {name})), "marker present: filter off");
    check(opt_out_present(folder(L"upper", {upper})), "marker in capitals: filter off (case-insensitive)");
    check(opt_out_present(folder(L"among", {L"state.json", name, L"native_log.txt"})),
          "marker among other files: filter off");
    check(!opt_out_present(folder(L"old", {L"show_unowned_slots.txt"})),
          "the 1.9.7 switch file is ignored: filter on");
    check(!opt_out_present(folder(L"unrelated", {L"state.json", L"ownership_probe.txt"})),
          "unrelated files: filter on");
    check(!opt_out_present(folder(L"empty", {})), "empty folder: filter on");
    check(!opt_out_present(folder(L"nested", {}, name)), "marker only in a sub-folder: filter on");
    check(!opt_out_present(folder(L"suffix", {name + L".bak"})), "a longer name: filter on");
    check(!opt_out_present(g_root + L"\\missing"), "missing folder: filter on");

    remove_tree(g_root);
    std::printf(fails ? "%d FAILED\n" : "all ok\n", fails);
    return fails ? 1 : 0;
}
