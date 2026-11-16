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
     commit's copy (index and work tree), then checks each path's tree hash against the commit,
  4. rewrites dependencies.json and the "Synced to" line of UPSTREAM.md,
  5. writes title.txt, body.md and branch.txt into --out (default: .sync-out) for the pull request.

Prints "changed=true|false" (and writes changed/branch to $GITHUB_OUTPUT when set). Exits non-zero
on anything that needs a person: a missing digest, a path gone upstream, a tree hash mismatch, an
older release or package without --allow-older (with --scheduled an older latest release is only a
notice and exits 0).
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys

ROOT = os.getcwd()
DEPS = os.path.join(ROOT, "dependencies.json")
PATHS = os.path.join(ROOT, ".github", "upstream-paths.txt")
UPSTREAM_MD = os.path.join(ROOT, "UPSTREAM.md")
STARTER = ("Slotsloader from this fork and the Mod Suite are now one, with combined fixes and "
           "improvements. Latest releases are now synced.")
PACKAGES = {
    "slots_loader": ("SF6 Slots Loader", re.compile(r"SF6_SlotsLoader_v(\d+(?:\.\d+)+)\.zip")),
    "native_core": ("SF6 Native Core", re.compile(r"SF6_NativeCore_(\d+(?:\.\d+)+)\.zip")),
}
TAG = re.compile(r"v(\d+(?:\.\d+)+)")
SYNCED = re.compile(r"^Synced to: `([0-9a-f]{40})` \(release `([^`]+)`\)\.?$", re.M)


def run(*args):
    r = subprocess.run(args, cwd=ROOT, text=True, capture_output=True)
    if r.returncode != 0:
        sys.exit("command failed: %s\n%s" % (" ".join(args), (r.stderr or "").strip()))
    return (r.stdout or "").strip()


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
    run("git", "fetch", "--quiet", "--no-tags", "--depth=1", "https://github.com/%s.git" % source,
        "+refs/tags/%s:refs/sync/upstream" % tag)
    commit = run("git", "rev-parse", "refs/sync/upstream^{commit}")
    released = run("gh", "api", "repos/%s/commits/%s" % (source, tag), "--jq", ".sha")
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

    paths = read_paths()
    commit = sync_source(source, tag, paths)
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
    os.makedirs(a.out, exist_ok=True)
    write_text(os.path.join(a.out, "title.txt"), title + "\n")
    write_text(os.path.join(a.out, "body.md"), "\n".join(body) + "\n")
    write_text(os.path.join(a.out, "branch.txt"), branch + "\n")
    output(changed="true", branch=branch)
    print(title)


if __name__ == "__main__":
    main()
