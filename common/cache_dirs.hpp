#pragma once
// The loaders' own cache folders (the costume loader's costume_mods\.cache, the stage loader's stage_mods, title_mods and
// ui_mods \.cache): removing what a loader made there, and nothing else (stagetest --selftest). Windows only.
//   remove_tree  a folder and everything in it. A link met inside (junction, symbolic link, mount point) is removed itself,
//                never followed: nothing outside the folder is touched. Read-only files and folders are made writable first.
//                The first thing that stays is reported, with the Win32 error.
//   prune        the entries of a cache folder that no pass asked for: only the folders the caller names as its own entries
//                (is_entry), never a file, never a link. A cache folder that is itself a link to another folder is left alone.

#include <windows.h>

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace cache_dirs {

// The \\?\ form of a path, for the wide file calls: any length, made absolute, "/" "." ".." and doubled separators resolved
// (a \\?\ path is taken as it is written)
inline std::wstring long_form(const std::wstring& p) {
    if (p.compare(0, 4, L"\\\\?\\") == 0 || p.compare(0, 4, L"\\\\.\\") == 0) return p;
    std::vector<wchar_t> buf(p.size() + MAX_PATH + 1);
    DWORD n = GetFullPathNameW(p.c_str(), (DWORD)buf.size(), buf.data(), nullptr);
    if (n >= buf.size()) {
        buf.resize(size_t(n) + 1);
        n = GetFullPathNameW(p.c_str(), (DWORD)buf.size(), buf.data(), nullptr);
    }
    if (n == 0 || n >= buf.size()) return p;
    const std::wstring full(buf.data(), n);
    if (full.size() >= 3 && full[1] == L':' && full[2] == L'\\') return L"\\\\?\\" + full;
    if (full.compare(0, 2, L"\\\\") == 0) return L"\\\\?\\UNC\\" + full.substr(2);
    return full;
}

// The name of a folder the stage loader unpacks an archive into: its key, 16 lower-case hex digits (stage_loader.cpp's hex16)
inline bool is_hex16(const std::wstring& n) {
    if (n.size() != 16) return false;
    for (wchar_t c : n)
        if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return false;
    return true;
}

// What stayed, and why
struct Failure {
    std::wstring path;          // \\?\ form
    unsigned long error = 0;    // GetLastError()
};

namespace detail {

inline void note(Failure* f, const std::wstring& path, DWORD error) {
    if (f && f->path.empty()) { f->path = path; f->error = error; }
}

// dir: \\?\ form, attrs: its own attributes (a link's, not its target's)
inline bool remove_in(const std::wstring& dir, DWORD attrs, uint64_t& bytes, Failure* fail) {
    if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) {                  // a link: the link itself, never what it points to
        if (RemoveDirectoryW(dir.c_str())) return true;
        note(fail, dir, GetLastError());
        return false;
    }
    if (attrs & FILE_ATTRIBUTE_READONLY) SetFileAttributesW(dir.c_str(), attrs & ~DWORD(FILE_ATTRIBUTE_READONLY));
    bool ok = true;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) {
        note(fail, dir, GetLastError());
        return false;
    }
    do {
        const wchar_t* n = fd.cFileName;
        if (n[0] == L'.' && (n[1] == 0 || (n[1] == L'.' && n[2] == 0))) continue;
        const std::wstring full = dir + L"\\" + n;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            ok = remove_in(full, fd.dwFileAttributes, bytes, fail) && ok;
            continue;
        }
        const bool link = (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;   // a file link: the link
        if (!link && (fd.dwFileAttributes & FILE_ATTRIBUTE_READONLY))
            SetFileAttributesW(full.c_str(), fd.dwFileAttributes & ~DWORD(FILE_ATTRIBUTE_READONLY));
        if (DeleteFileW(full.c_str())) {
            if (!link) bytes += (uint64_t(fd.nFileSizeHigh) << 32) | fd.nFileSizeLow;
        } else {
            note(fail, full, GetLastError());
            ok = false;
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    if (!ok) return false;                                       // something inside stayed: the folder cannot go
    if (RemoveDirectoryW(dir.c_str())) return true;
    note(fail, dir, GetLastError());
    return false;
}

}  // namespace detail

// dir and everything in it. true when it is gone (or was never there); bytes: what the removed files held, added
inline bool remove_tree(const std::wstring& dir, uint64_t* bytes = nullptr, Failure* fail = nullptr) {
    const std::wstring d = long_form(dir);
    const DWORD a = GetFileAttributesW(d.c_str());
    if (a == INVALID_FILE_ATTRIBUTES) {
        const DWORD e = GetLastError();
        if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) return true;
        detail::note(fail, d, e);
        return false;
    }
    if (!(a & FILE_ATTRIBUTE_DIRECTORY)) {                       // never a file
        detail::note(fail, d, ERROR_DIRECTORY);
        return false;
    }
    uint64_t b = 0;
    const bool ok = detail::remove_in(d, a, b, fail);
    if (bytes) *bytes += b;
    return ok;
}

struct Pruned {
    bool there = false;                // the cache folder exists
    bool link = false;                 // it is a link to another folder: nothing was removed
    unsigned long list_error = 0;      // it could not be listed (GetLastError()): nothing was removed
    size_t kept = 0;                   // entries asked for this pass
    std::vector<std::wstring> removed; // entries removed (their names)
    uint64_t bytes = 0;                // what their files held
    std::vector<std::pair<std::wstring, Failure>> failed;   // entries that stayed (their names), what stayed and why
};

// root: the cache folder. is_entry(name): a folder name the loader makes there (any other folder, every file and every link
// is left alone). keep(name): asked for this pass. Each entry neither kept nor asked for is removed once (no retry here).
template <class IsEntry, class Keep>
Pruned prune(const std::wstring& root, IsEntry is_entry, Keep keep) {
    Pruned r;
    const std::wstring d = long_form(root);
    const DWORD a = GetFileAttributesW(d.c_str());
    if (a == INVALID_FILE_ATTRIBUTES || !(a & FILE_ATTRIBUTE_DIRECTORY)) return r;
    r.there = true;
    if (a & FILE_ATTRIBUTE_REPARSE_POINT) {
        r.link = true;
        return r;
    }
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((d + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) {
        r.list_error = GetLastError();
        return r;
    }
    std::vector<std::wstring> stale;
    do {
        const std::wstring n = fd.cFileName;
        if (n == L"." || n == L"..") continue;
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) || !is_entry(n)) continue;
        if (keep(n)) ++r.kept;
        else stale.push_back(n);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    for (auto& n : stale) {
        Failure f;
        if (remove_tree(d + L"\\" + n, &r.bytes, &f)) r.removed.push_back(n);
        else r.failed.push_back({ n, f });
    }
    return r;
}

}  // namespace cache_dirs
