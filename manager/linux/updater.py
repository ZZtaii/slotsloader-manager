"""Verified, whole-package Linux updates while the Manager session is closed."""
from __future__ import annotations

import contextlib
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import tempfile
import time
import urllib.parse
import urllib.request
import zipfile

REPO = "ZZtaii/slotsloader-manager"
API_URL = "https://api.github.com/repos/" + REPO + "/releases?per_page=100"
MAX_DOWNLOAD = 128 * 1024 * 1024
MAX_EXPANDED = 512 * 1024 * 1024
TAG = re.compile(r"manager-linux-v(\d+\.\d+\.\d+)-beta([1-9]\d*)\Z")
VERSION = re.compile(r"(\d+)\.(\d+)\.(\d+)-linux-beta([1-9]\d*)\Z")


class UpdateError(Exception):
    pass


def version_key(value):
    match = VERSION.fullmatch(str(value))
    if not match:
        raise UpdateError("The installed Linux package version could not be read.")
    return tuple(int(part) for part in match.groups())


def trusted_url(url):
    parsed = urllib.parse.urlsplit(url)
    return (parsed.scheme == "https" and not parsed.username and not parsed.password
            and parsed.port in (None, 443) and not parsed.fragment
            and (url == API_URL or (parsed.netloc == "github.com"
                 and parsed.path.startswith("/" + REPO + "/releases/download/"))))


class ReleaseRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, url):
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != "https" or parsed.username or parsed.password
                or parsed.port not in (None, 443)
                or parsed.hostname not in ("github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com")):
            raise UpdateError("GitHub redirected the update to an unexpected server.")
        return super().redirect_request(request, fp, code, msg, headers, url)


def fetch(url, limit, destination=None):
    if not trusted_url(url):
        raise UpdateError("The update URL is not an official project release.")
    request = urllib.request.Request(url, headers={"User-Agent": "SlotsLoader-Manager-Linux-Updater",
                                                  "Accept": "application/vnd.github+json" if url == API_URL else "application/octet-stream"})
    opener = urllib.request.build_opener(ReleaseRedirect())
    started = time.monotonic()
    with opener.open(request, timeout=4 if destination is None else 30) as response:
        size = response.headers.get("Content-Length")
        if size and int(size) > limit:
            raise UpdateError("The update download is larger than expected.")
        chunks, total = [], 0
        with (open(destination, "xb") if destination else contextlib.nullcontext()) as output:
            while True:
                chunk = response.read(min(65536, limit + 1 - total))
                if not chunk:
                    break
                total += len(chunk)
                if total > limit or time.monotonic() - started > (180 if destination else 10):
                    raise UpdateError("The update download exceeded its size or time limit.")
                if output:
                    output.write(chunk)
                else:
                    chunks.append(chunk)
    return total if destination else b"".join(chunks)


def find_offer(releases, current):
    if not isinstance(releases, list):
        raise UpdateError("GitHub returned an invalid releases list.")
    offers = []
    for release in releases:
        if not isinstance(release, dict) or release.get("draft"):
            continue
        match = TAG.fullmatch(str(release.get("tag_name", "")))
        if not match:
            continue  # Windows and loader releases can never replace Linux integration.
        version = match[1] + "-linux-beta" + match[2]
        if version_key(version) <= version_key(current):
            continue
        name = "SlotsLoader-Manager-" + version + ".zip"
        assets = release.get("assets", [])
        if not isinstance(assets, list):
            continue
        matches = [a for a in assets if isinstance(a, dict) and a.get("name") == name]
        if len(matches) != 1:
            continue
        asset = matches[0]
        size, digest = asset.get("size"), asset.get("digest", "")
        url = "https://github.com/" + REPO + "/releases/download/" + release["tag_name"] + "/" + name
        # GitHub's asset digest binds this exact binary to its official HTTPS metadata.
        if (type(size) is not int or not 0 < size <= MAX_DOWNLOAD
                or not isinstance(digest, str) or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest)
                or asset.get("browser_download_url") != url):
            continue
        offers.append({"version": version, "name": name, "size": size, "sha256": digest[7:].lower(), "url": url})
    return max(offers, key=lambda item: version_key(item["version"])) if offers else None


def unpack_verified(archive, destination, offer, api):
    if archive.stat().st_size != offer["size"] or hashlib.sha256(archive.read_bytes()).hexdigest() != offer["sha256"]:
        raise UpdateError("The update failed its size or SHA-256 check. Your current installation is unchanged.")
    wrapper = "SlotsLoader-Manager-" + offer["version"]
    seen, expanded = set(), 0
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        if not entries or len(entries) > 1024:
            raise UpdateError("The Linux update archive has an invalid file count.")
        for entry in entries:
            name = entry.filename.rstrip("/")
            parts = PurePosixPath(name).parts
            mode = entry.external_attr >> 16
            if (not parts or parts[0] != wrapper or "\\" in name
                    or name.casefold() in seen or entry.flag_bits & 1
                    or (stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR))):
                raise UpdateError("The Linux update archive contains an unsafe entry.")
            seen.add(name.casefold())
            api.safe_member(name)
            expanded += entry.file_size
            if expanded > MAX_EXPANDED:
                raise UpdateError("The expanded update is larger than expected.")
            target = api.checked_target(destination, name)
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with package.open(entry) as source, open(target, "xb") as output:
                    copied = 0
                    while True:
                        chunk = source.read(65536)
                        if not chunk:
                            break
                        copied += len(chunk)
                        if copied > entry.file_size:
                            raise UpdateError("An update file exceeded its declared size.")
                        output.write(chunk)
                os.chmod(target, 0o755 if name.endswith((".desktop", ".sh")) else 0o644)
    source = destination / wrapper
    manifest = api.verify_package(source)
    if manifest.get("version") != offer["version"]:
        raise UpdateError("The update's package version does not match its release.")
    required = {"linux/updater.py", "app/Fonts/Inter-Regular.ttf", "app/Fonts/LiberationMono-Regular.ttf"}
    if not required.issubset(manifest["files"]):
        raise UpdateError("The Linux update is missing its update helper or fonts.")
    return source


def check_and_install(root, dialog, api, manual=False):
    """Caller holds the session lock and has confirmed no Manager is running."""
    if dialog.headless:
        return False
    cache_path = api.checked_target(root, "linux-update-state.json")
    try:
        cache = api.read_json(cache_path, {})
        if not isinstance(cache, dict):
            cache = {}
    except Exception:
        cache = {}
    def remember_check():
        try:
            api.atomic_json(cache_path, {"checked_at": time.time()})
        except OSError:
            pass  # An optional check timestamp must not prevent launching the app.
    if not manual:
        settings_path = root / "app" / "Data" / "update-settings.json"
        # Read only update preferences, never the Assistant settings or credentials.
        try:
            settings = api.read_json(settings_path, {})
            if not isinstance(settings, dict):
                settings = {}
        except Exception:
            settings = {}
        checked = cache.get("checked_at", 0)
        age = time.time() - checked if type(checked) in (int, float) else 86400
        if settings.get("AutoCheck") is False or 0 <= age < 86400:
            return False
    manifest = api.read_json(root / "package.json", {})
    try:
        with dialog.busy("Checking for Linux app updates") if manual else contextlib.nullcontext():
            releases = json.loads(fetch(API_URL, 4 * 1024 * 1024))
        offer = find_offer(releases, manifest.get("version"))
        remember_check()
    except Exception:
        # Background failures stay quiet and retry on the next day; launch still works offline.
        remember_check()
        if manual:
            dialog.info("Couldn't check for Linux updates. Check your connection and try again. Your installed app is unchanged.", error=True)
        return False
    if not offer:
        if manual:
            dialog.info("Your Linux app package is up to date.")
        return False
    if not dialog.confirm("SlotsLoader Manager " + offer["version"] + " is available.\n\nDownload and install this Linux update now? Your settings, mods and compatibility runtime will be kept."):
        return False
    try:
        with dialog.busy("Downloading and installing the Linux update"), tempfile.TemporaryDirectory(prefix="slotsloader-update-") as temporary:
            stage = Path(temporary)
            archive = stage / "update.zip"
            fetch(offer["url"], offer["size"], archive)
            source = unpack_verified(archive, stage, offer, api)
            api.install_package(source, root)
            api.install_shortcut(root)
        return True
    except Exception as error:
        dialog.info("The Linux update could not finish: " + str(error)[:400] + "\nReopen Manager to retry. Your app Data has been kept.", error=True)
        return False
