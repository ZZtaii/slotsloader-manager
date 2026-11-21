#!/usr/bin/env python3
"""Sync the Slots Loader source and dependencies.json to a release of the SF6 Mod Suite.

Run from the repository root. It never commits or pushes; the workflow does that.

  python3 .github/scripts/sync_suite.py [--release vX.Y.Z] [--allow-older] [--scheduled] [--out <folder>]

Without --release it takes the upstream repository's latest published release (not drafts or
pre-releases). It then:
  1. reads the release's SF6_SlotsLoader_v*.zip and SF6_NativeCore_*.zip (name, size, GitHub SHA-256),
  2. flags a file whose name is already approved but whose SHA-256 changed (re-uploaded upstream):
     the pull request then says so in its title and first lines, and nothing is approved until merged,
  3. fetches the release's commit and replaces every path in .github/upstream-paths.txt with that
     commit's copy (index and work tree), then checks each path's tree hash against the commit.
     Since 21 Nov 2026 the suite's code is in Wael's private repository (CODE_SOURCE, same tags as
     the releases); the releases stay on dependencies.json's "source". Reading the code needs
     SUITE_SOURCE_TOKEN, a token of an account with read access there (see UPSTREAM.md),
  4. rewrites dependencies.json and the "Synced to" line of UPSTREAM.md,
  5. writes title.txt, body.md and branch.txt into --out (default: .sync-out) for the pull request,
  6. (Manager 1.0.21) builds the Manager's slim copy of the Slots Loader zip: Wael's zip, downloaded and checked
     against its SHA-256, without the files of .github/loader-slim-skip.txt (every other entry byte for byte the
     same content, same names and times). It is published as a pre-release `slotsloader-v<version>` of this
     repository (never Latest) by the workflow, and named in dependencies.json as slots_loader.slim. Managers
     1.0.21 and later download it; older ones keep using the full zip of the same entry. An existing asset of that
     release is reused when its entries match (a rebuild may compress differently), never replaced.

Prints "changed=true|false" (and writes changed/branch to $GITHUB_OUTPUT when set). Exits non-zero
on anything that needs a person: a missing digest, a path gone upstream, a tree hash mismatch, an
older release or package without --allow-older (with --scheduled an older latest release is only a
notice and exits 0).
"""
import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.getcwd()
DEPS = os.path.join(ROOT, "dependencies.json")
PATHS = os.path.join(ROOT, ".github", "upstream-paths.txt")
SLIM_SKIP = os.path.join(ROOT, ".github", "loader-slim-skip.txt")
REPO = os.environ.get("GITHUB_REPOSITORY") or "ZZtaii/slotsloader-manager"
UPSTREAM_MD = os.path.join(ROOT, "UPSTREAM.md")
# Where the suite's code is (private since 21 Nov 2026). Releases and their zips stay on deps["source"].
CODE_SOURCE = os.environ.get("SUITE_SOURCE_REPO") or "Wael3rd/SF6_ModSuite_Private"
STARTER = ("Slotsloader from this fork and the Mod Suite are now one, with combined fixes and "
           "improvements. Latest releases are now synced.")
PACKAGES = {
    "slots_loader": ("SF6 Slots Loader", re.compile(r"SF6_SlotsLoader_v(\d+(?:\.\d+)+)\.zip")),
    "native_core": ("SF6 Native Core", re.compile(r"SF6_NativeCore_(\d+(?:\.\d+)+)\.zip")),
}
TAG = re.compile(r"v(\d+(?:\.\d+)+)")
SYNCED = re.compile(r"^Synced to: `([0-9a-f]{40})` \(release `([^`]+)`\)\.?$", re.M)


def run(*args, env=None):
    r = subprocess.run(args, cwd=ROOT, text=True, capture_output=True, env=env)
    if r.returncode != 0:
        sys.exit("command failed: %s\n%s" % (" ".join(args), (r.stderr or "").strip()))
    return (r.stdout or "").strip()


def code_env():
    """The environment for git and gh calls on CODE_SOURCE: SUITE_SOURCE_TOKEN for both. For git it replaces the
    checkout's own github.com credentials (the workflow's token, which cannot read another owner's private
    repository) through GIT_CONFIG_* variables, so the token never shows in a command line."""
    token = os.environ.get("SUITE_SOURCE_TOKEN", "").strip()
    if not token:
        sys.exit("SUITE_SOURCE_TOKEN is not set: the suite's code is in the private repository %s. Add the "
                 "repository secret SUITE_SOURCE_TOKEN (see UPSTREAM.md, Syncing)" % CODE_SOURCE)
    basic = base64.b64encode(("x-access-token:" + token).encode()).decode()
    key = "http.https://github.com/.extraheader"
    env = dict(os.environ, GH_TOKEN=token, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="never",
               GIT_CONFIG_COUNT="3",
               GIT_CONFIG_KEY_0=key, GIT_CONFIG_VALUE_0="",   # an empty value drops the headers set so far
               GIT_CONFIG_KEY_1=key, GIT_CONFIG_VALUE_1="AUTHORIZATION: basic " + basic,
               GIT_CONFIG_KEY_2="credential.helper", GIT_CONFIG_VALUE_2="")
    env.pop("GITHUB_TOKEN", None)
    return env


def vkey(v, width=4):
    parts = [int(x) for x in v.split(".")]
    return tuple(parts + [0] * (width - len(parts)))


def tag_key(tag):
    m = TAG.fullmatch(tag or "")
    return vkey(m.group(1)) if m else None


def output(**kv):
    for k, v in kv.items():
        print("%s=%s" % (k, v))
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            for k, v in kv.items():
                f.write("%s=%s\n" % (k, v))


def read_text(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def write_text(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def load_release(source, tag):
    path = "repos/%s/releases/%s" % (source, "tags/" + tag if tag else "latest")
    rel = json.loads(run("gh", "api", path))
    if rel.get("draft"):
        sys.exit("release %s is a draft" % rel.get("tag_name"))
    return rel


def pick_assets(rel):
    found = {}
    for asset in rel.get("assets", []):
        for key, (_, rx) in PACKAGES.items():
            m = rx.fullmatch(asset.get("name", ""))
            if not m:
                continue
            if key in found:
                sys.exit("release %s has more than one %s zip" % (rel["tag_name"], key))
            if asset.get("state") != "uploaded":
                sys.exit("%s is not fully uploaded yet (state %s): run again later" % (asset["name"], asset.get("state")))
            digest = asset.get("digest") or ""
            if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
                sys.exit("%s has no SHA-256 digest on GitHub" % asset["name"])
            found[key] = {"version": m.group(1), "file": asset["name"], "size": int(asset["size"]),
                          "sha256": digest[7:]}
    return found


def read_paths():
    out = []
    for line in read_text(PATHS).splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name = line.rstrip("/")
        bits = name.replace("\\", "/").split("/")
        if (not name or os.path.isabs(name) or ":" in name or any(b in ("", ".", "..") for b in bits)
                or bits[0].lower().startswith(".git")):
            sys.exit("upstream-paths.txt: refusing the path %r" % line)
        out.append(line)
    return out


def sync_source(source, tag, paths):
    env = code_env()
    run("git", "fetch", "--quiet", "--no-tags", "--depth=1", "https://github.com/%s.git" % source,
        "+refs/tags/%s:refs/sync/upstream" % tag, env=env)
    commit = run("git", "rev-parse", "refs/sync/upstream^{commit}")
    released = run("gh", "api", "repos/%s/commits/%s" % (source, tag), "--jq", ".sha", env=env)
    if released != commit:
        sys.exit("the tag %s moved while syncing (%s fetched, %s now): run again" % (tag, commit[:7], released[:7]))
    for p in paths:
        name = p.rstrip("/")
        if subprocess.run(["git", "cat-file", "-e", "%s:%s" % (commit, name)], cwd=ROOT).returncode != 0:
            sys.exit("%s is not in %s %s any more: update .github/upstream-paths.txt by hand" % (name, source, tag))
        run("git", "rm", "-r", "-q", "-f", "--ignore-unmatch", "--", name)
        full = os.path.join(ROOT, name)
        if os.path.islink(full) or os.path.isfile(full):
            os.remove(full)
        elif os.path.isdir(full):
            shutil.rmtree(full)
        run("git", "checkout", commit, "--", name)
    for p in paths:
        name = p.rstrip("/")
        want = run("git", "rev-parse", "%s:%s" % (commit, name))
        have = run("git", "write-tree", "--prefix=%s/" % name) if p.endswith("/") else run("git", "rev-parse", ":" + name)
        if want != have:
            sys.exit("%s differs from %s after the copy (%s vs %s)" % (name, commit[:7], have[:7], want[:7]))
    return commit


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def slim_skip():
    out = []
    for line in read_text(SLIM_SKIP).splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            out.append(line.replace("\\", "/").lstrip("/"))
    if not out:
        sys.exit("loader-slim-skip.txt lists nothing")
    return out


def skipped(name, skip):
    n = name.replace("\\", "/").lstrip("/")
    return any(n.lower().startswith(s.lower()) if s.endswith("/") else n.lower() == s.lower() for s in skip)


def kept_entries(path, skip):
    """(name, CRC, size) of the entries the slim zip keeps, in order: what a slim zip must hold."""
    with zipfile.ZipFile(path) as z:
        return [(i.filename, i.CRC, i.file_size) for i in z.infolist() if not skipped(i.filename, skip)]


def build_slim(full, dest, skip):
    with zipfile.ZipFile(full) as src, zipfile.ZipFile(dest, "w") as out:
        for info in src.infolist():
            if skipped(info.filename, skip):
                continue
            copy = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            copy.external_attr = info.external_attr
            copy.create_system = info.create_system
            if info.is_dir():
                out.writestr(copy, b"")
            else:
                copy.compress_type = zipfile.ZIP_DEFLATED
                out.writestr(copy, src.read(info), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        if src.testzip() is not None:
            sys.exit("%s fails its own CRC check" % full)


def slim_package(source, entry, old_entry, out_dir):
    """The slim block for the slots_loader entry, the zip to upload (or None when the release already has it)."""
    skip = slim_skip()
    tag = "slotsloader-v" + entry["version"]
    name = entry["file"][:-4] + "-manager.zip"
    old_slim = (old_entry or {}).get("slim")
    if (old_slim and old_slim.get("from_sha256", "").lower() == entry["sha256"].lower()
            and old_slim.get("skip") == skip and old_slim.get("release") == tag):
        return old_slim, None, False
    work = tempfile.mkdtemp(prefix="slim-")
    run("gh", "release", "download", entry["release"], "--repo", source, "--pattern", entry["file"], "--dir", work)
    full = os.path.join(work, entry["file"])
    if os.path.getsize(full) != entry["size"] or sha256_of(full) != entry["sha256"].lower():
        sys.exit("%s downloaded from %s %s does not match its size and SHA-256" % (entry["file"], source, entry["release"]))
    want = kept_entries(full, skip)
    dropped = [i for i in zipfile.ZipFile(full).namelist() if skipped(i, skip)]
    os.makedirs(out_dir, exist_ok=True)
    slim = os.path.join(out_dir, name)
    upload = True
    # The release may already hold the asset (a rerun, or the same loader in a later suite release): reuse it when
    # its entries are the ones wanted, refuse to go on when they differ (never replace an approved file).
    assets = subprocess.run(["gh", "release", "view", tag, "--repo", REPO, "--json", "assets", "--jq", ".assets[].name"],
                            cwd=ROOT, text=True, capture_output=True)
    if assets.returncode == 0 and name in assets.stdout.split():
        run("gh", "release", "download", tag, "--repo", REPO, "--pattern", name, "--dir", out_dir, "--clobber")
        if kept_entries(slim, skip) != want:
            sys.exit("%s on %s %s holds other files than the ones wanted now: check it by hand" % (name, REPO, tag))
        upload = False
    else:
        build_slim(full, slim, skip)
        if kept_entries(slim, []) != want:
            sys.exit("the slim zip does not hold the kept entries")
    block = {"repository": REPO, "release": tag, "file": name, "size": os.path.getsize(slim), "sha256": sha256_of(slim),
             "from_sha256": entry["sha256"].lower(), "skip": skip}
    shutil.rmtree(work, ignore_errors=True)
    print("slim zip %s: %d bytes (full %d), %d entries left out: %s" % (name, block["size"], entry["size"], len(dropped), ", ".join(dropped)))
    return block, slim, upload


def changed_folders(paths):
    names = [p.rstrip("/") for p in paths]
    stat = run("git", "diff", "--cached", "--name-only", "--", *names)
    tops = {}
    for line in stat.splitlines():
        top = line.split("/")[0] + ("/" if "/" in line else "")
        tops[top] = tops.get(top, 0) + 1
    return tops


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--release", help="upstream release tag, e.g. v1.38.0 (default: the latest)")
    ap.add_argument("--allow-older", action="store_true", help="allow an older release or package (going back)")
    ap.add_argument("--scheduled", action="store_true", help="an older latest release is a notice, not an error")
    ap.add_argument("--out", default=os.path.join(ROOT, ".sync-out"))
    a = ap.parse_args()

    deps = json.loads(read_text(DEPS))
    source = deps["source"]
    upstream_md = read_text(UPSTREAM_MD)
    m = SYNCED.search(upstream_md)
    if not m:
        sys.exit("UPSTREAM.md has no 'Synced to: `<commit>` (release `<tag>`).' line")
    synced_commit, synced_tag = m.group(1), m.group(2)

    rel = load_release(source, a.release)
    tag = rel["tag_name"]
    if tag_key(tag) is None and not a.allow_older:
        sys.exit("the release tag %r is not vX.Y.Z (pass --allow-older to take it anyway)" % tag)
    if not a.allow_older and tag_key(synced_tag) and tag_key(tag) < tag_key(synced_tag):
        if a.scheduled:
            output(changed="false")
            print("::notice::the latest release %s is older than the synced %s; nothing done" % (tag, synced_tag))
            return
        sys.exit("%s is older than the synced %s (pass --allow-older to go back)" % (tag, synced_tag))
    assets = pick_assets(rel)
    if "slots_loader" not in assets and "slots_loader" not in deps["packages"]:
        sys.exit("release %s has no SF6_SlotsLoader zip and none is approved yet" % tag)

    lines, reuploaded, packages_changed = [], [], False
    new_packages = dict(deps["packages"])
    for key, (label, _) in PACKAGES.items():
        short = label.replace("SF6 ", "")
        old = deps["packages"].get(key)
        info = assets.get(key)
        if not info:
            if old:
                lines.append("- %s unchanged (%s): not in this release" % (short, old["version"]))
            continue
        if old and not a.allow_older and vkey(info["version"]) < vkey(old["version"]):
            sys.exit("%s %s in %s is older than the approved %s (pass --allow-older to go back)" %
                     (short, info["version"], tag, old["version"]))
        entry = {"name": label, "version": info["version"], "release": tag,
                 "file": info["file"], "size": info["size"], "sha256": info["sha256"]}
        new_packages[key] = entry
        if old and old["file"] == info["file"] and old["sha256"].lower() == info["sha256"]:
            if old.get("release") != tag:
                lines.append("- %s unchanged (%s), now taken from `%s`" % (short, old["version"], tag))
                packages_changed = True
            else:
                lines.append("- %s unchanged (%s)" % (short, old["version"]))
            continue
        packages_changed = True
        if old and old["file"] == info["file"]:
            reuploaded.append(short)
            lines.append("- `dependencies.json`: %s %s **re-uploaded upstream under the same name**: SHA-256 %s → %s" %
                         (short, info["version"], old["sha256"][:12], info["sha256"][:12]))
        else:
            lines.append("- `dependencies.json`: %s %s → %s, new SHA-256" % (short, old["version"] if old else "none", info["version"]))

    # 6. The Manager's slim zip (Manager 1.0.21): rebuilt when the loader zip or the skip list changed.
    slim_path, slim_upload = None, False
    loader_entry = new_packages.get("slots_loader")
    if loader_entry:
        old_loader = deps["packages"].get("slots_loader") or {}
        block, slim_path, slim_upload = slim_package(source, loader_entry, old_loader, a.out)
        loader_entry["slim"] = block
        if block != old_loader.get("slim"):
            packages_changed = True
            lines.append("- `dependencies.json`: the Manager's slim zip `%s`, %.2f MB instead of %.2f MB (%d files left out), "
                         "on the pre-release `%s` of this repository" % (block["file"], block["size"] / 1048576.0,
                         loader_entry["size"] / 1048576.0, len(block["skip"]), block["release"]))

    paths = read_paths()
    commit = sync_source(CODE_SOURCE, tag, paths)   # the release's tag in the private code repository
    folders = changed_folders(paths)
    source_changed = bool(folders)
    if not source_changed and not packages_changed:
        output(changed="false")
        print("Already synced to %s (%s); approved packages match." % (tag, commit[:7]))
        return

    deps["packages"] = new_packages
    write_text(DEPS, json.dumps(deps, indent=2) + "\n")
    line = "Synced to: `%s` (release `%s`)." % (commit, tag)
    write_text(UPSTREAM_MD, SYNCED.sub(lambda _m: line, upstream_md, count=1))
    run("git", "add", "--", "dependencies.json", "UPSTREAM.md")

    loader = new_packages.get("slots_loader", {})
    moved = [l for l in lines if "→" in l]
    if reuploaded:
        title = "Re-uploaded: %s (SF6 Mod Suite %s)" % (", ".join(reuploaded), tag)
    elif any("Slots Loader" in l for l in moved):
        title = "Slots Loader %s (SF6 Mod Suite %s)" % (loader["version"], tag)
    elif moved:
        title = "Native Core %s (SF6 Mod Suite %s)" % (new_packages["native_core"]["version"], tag)
    elif any("slim zip" in l for l in lines):
        title = "Slim Slots Loader zip for the Manager (%s)" % loader.get("version", "")
    else:
        title = "Source sync (SF6 Mod Suite %s)" % tag

    body = [STARTER, ""]
    if reuploaded:
        body += ["**Check before merging:** %s was re-uploaded upstream under a name already approved. Managers refuse "
                 "the new file until this is merged; compare it with the approved one first." % " and ".join(reuploaded), ""]
    where = ", ".join("`%s` (%d)" % (k, v) for k, v in sorted(folders.items())) or "no file changes"
    body.append("- Source synced to `%s` (release `%s`): %s" % (commit[:7], tag, where))
    body += lines
    body.append("- [Release notes](%s)" % rel.get("html_url", "https://github.com/%s/releases/tag/%s" % (source, tag)))
    if reuploaded:
        body += ["", "Merge after checking: Managers accept the re-uploaded file the next time they check."]
    else:
        body += ["", "Merge after testing: Managers offer the new version the next time they check." if moved
                 else "Merging keeps the code in step; nothing new is offered to Managers."]

    branch = "suite-sync/%s-%s-%s" % (tag, commit[:7], loader.get("sha256", "0" * 7)[:7])
    if (loader.get("slim") or {}).get("sha256"):
        branch += "-" + loader["slim"]["sha256"][:7]   # Manager 1.0.21: a new slim zip alone is a new sync
    os.makedirs(a.out, exist_ok=True)
    write_text(os.path.join(a.out, "title.txt"), title + "\n")
    write_text(os.path.join(a.out, "body.md"), "\n".join(body) + "\n")
    write_text(os.path.join(a.out, "branch.txt"), branch + "\n")
    slim = loader.get("slim") or {}
    output(changed="true", branch=branch, slim_upload="true" if slim_upload else "false",
           slim_path=slim_path or "", slim_tag=slim.get("release", ""), slim_version=loader.get("version", ""),
           slim_from=loader.get("file", ""), slim_source_release=loader.get("release", ""))
    print(title)


if __name__ == "__main__":
    main()
