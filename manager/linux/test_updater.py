"""Linux update-channel and rollback tests with generated packages; no live network."""
import contextlib
import hashlib
import json
from pathlib import Path
import stat
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

import launcher
import updater


class LinuxUpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / "installed"
        (self.root / "app/Data").mkdir(parents=True)
        (self.root / "app/Data/recovery.json").write_bytes(b"precious recovery")
        (self.root / "app/gdiplus.dll").write_bytes(b"retained runtime")
        self.version = "1.0.13-linux-beta3"
        self.wrapper = "SlotsLoader-Manager-" + self.version
        self.archive = self.base / "package.zip"
        self.files = {"app/" + launcher.EXE: b"new gui", "app/" + launcher.EXE + ".config": b"config",
                      "linux/launcher.py": b"launcher", "linux/host.py": b"host", "linux/updater.py": b"updater",
                      "linux/bottle.yml": b"recipe", "app/Fonts/Inter-Regular.ttf": b"font",
                      "app/Fonts/LiberationMono-Regular.ttf": b"mono"}
        self.package()
        self.dialog = types.SimpleNamespace(headless=False, busy=lambda _: contextlib.nullcontext(),
                                            confirm=lambda _: True, info=lambda *args, **kwargs: None)
        self.release = {"tag_name": "manager-linux-v1.0.13-beta3", "prerelease": True, "draft": False,
                        "assets": [{"name": self.wrapper + ".zip", "size": self.offer["size"],
                                    "digest": "sha256:" + self.offer["sha256"], "browser_download_url": self.offer["url"]}]}
        (self.root / "package.json").write_text(json.dumps({"version": "1.0.12-linux-beta2"}))

    def package(self, extra=None, version=None):
        manifest = {"format": 1, "version": version or self.version,
                    "files": {name: hashlib.sha256(value).hexdigest() for name, value in self.files.items()}}
        with zipfile.ZipFile(self.archive, "w") as z:
            for name, value in self.files.items():
                z.writestr(self.wrapper + "/" + name, value)
            z.writestr(self.wrapper + "/package.json", json.dumps(manifest))
            if extra:
                z.writestr(*extra)
        self.offer = {"version": self.version, "name": self.wrapper + ".zip", "size": self.archive.stat().st_size,
                      "sha256": hashlib.sha256(self.archive.read_bytes()).hexdigest(),
                      "url": "https://github.com/" + updater.REPO + "/releases/download/manager-linux-v1.0.13-beta3/" + self.wrapper + ".zip"}

    def fetch(self, url, limit, destination=None):
        if url == updater.API_URL:
            return json.dumps([self.release]).encode()
        self.assertEqual(url, self.offer["url"])
        destination.write_bytes(self.archive.read_bytes())
        return self.archive.stat().st_size

    def preserved(self):
        self.assertEqual((self.root / "app/Data/recovery.json").read_bytes(), b"precious recovery")
        self.assertEqual((self.root / "app/gdiplus.dll").read_bytes(), b"retained runtime")

    def test_only_linux_channel_exact_asset_and_newer_version(self):
        self.assertEqual(updater.find_offer([self.release], "1.0.12-linux-beta2"), self.offer)
        self.assertIsNone(updater.find_offer([self.release], self.version))
        self.assertIsNone(updater.find_offer([self.release], "1.0.14-linux-beta1"))
        for tag in ("manager-v99.0.0", "loader-v99.0.0", "manager-linux-v1.0.13-beta3-evil"):
            self.assertIsNone(updater.find_offer([dict(self.release, tag_name=tag)], "1.0.12-linux-beta2"))
        self.assertIsNone(updater.find_offer([dict(self.release, draft=True)], "1.0.12-linux-beta2"))

    def test_unverified_ambiguous_external_and_oversized_assets_refused(self):
        asset = self.release["assets"][0]
        for change in ({"digest": ""}, {"digest": "sha256:bad"}, {"size": True}, {"size": updater.MAX_DOWNLOAD + 1},
                       {"browser_download_url": "https://example.invalid/payload.zip"}, {"name": "windows.zip"}):
            self.assertIsNone(updater.find_offer([dict(self.release, assets=[dict(asset, **change)])], "1.0.12-linux-beta2"))
        self.assertIsNone(updater.find_offer([dict(self.release, assets=[asset, asset])], "1.0.12-linux-beta2"))

    def test_checksum_size_and_manifest_version_fail_before_install(self):
        for change in ({"sha256": "0" * 64}, {"size": self.offer["size"] + 1}):
            with self.assertRaises(updater.UpdateError):
                updater.unpack_verified(self.archive, self.base / "extract", dict(self.offer, **change), launcher)
        self.package(version="99.0.0-linux-beta3")
        with self.assertRaisesRegex(updater.UpdateError, "version"):
            updater.unpack_verified(self.archive, self.base / "mismatch", self.offer, launcher)
        self.preserved()

    def test_traversal_links_duplicate_case_and_private_data_refused(self):
        link = zipfile.ZipInfo(self.wrapper + "/link")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        for number, name in enumerate((self.wrapper + "/../escape", "/absolute", self.wrapper + "/app/Data/key.txt",
                                       self.wrapper + "/LINUX/host.py", link)):
            self.package(extra=(name, b"bad"))
            with self.assertRaises((updater.UpdateError, launcher.SetupError)):
                updater.unpack_verified(self.archive, self.base / ("bad" + str(number)), self.offer, launcher)
        self.assertFalse((self.base / "escape").exists())
        self.preserved()

    def test_manifest_checksum_rejects_changed_font(self):
        self.package(extra=(self.wrapper + "/unlisted.txt", b"ignored"))
        # Rewrite one listed member without changing the trusted manifest.
        with zipfile.ZipFile(self.archive) as z:
            entries = [(item.filename, z.read(item)) for item in z.infolist()]
        with zipfile.ZipFile(self.archive, "w") as z:
            for name, data in entries:
                z.writestr(name, b"tampered" if name.endswith("Inter-Regular.ttf") else data)
        self.offer.update(size=self.archive.stat().st_size, sha256=hashlib.sha256(self.archive.read_bytes()).hexdigest())
        with self.assertRaisesRegex(launcher.SetupError, "checksum"):
            updater.unpack_verified(self.archive, self.base / "tampered", self.offer, launcher)

    def test_confirmed_whole_package_update_preserves_data_and_runtime(self):
        with patch.object(updater, "fetch", side_effect=self.fetch), patch.object(launcher, "install_shortcut"):
            self.assertTrue(updater.check_and_install(self.root, self.dialog, launcher, manual=True))
        self.assertEqual((self.root / "app" / launcher.EXE).read_bytes(), b"new gui")
        self.assertEqual(launcher.verify_package(self.root)["version"], self.version)
        self.preserved()

    def test_failed_install_rolls_back_program_files(self):
        old = self.root / "app" / launcher.EXE
        old.write_bytes(b"old gui")
        copy = launcher.shutil.copyfile
        def fail(source, target):
            if str(target).endswith("host.py.incoming"):
                raise OSError("fixture interrupted update")
            return copy(source, target)
        with patch.object(updater, "fetch", side_effect=self.fetch), patch.object(launcher.shutil, "copyfile", side_effect=fail):
            self.assertFalse(updater.check_and_install(self.root, self.dialog, launcher, manual=True))
        self.assertEqual(old.read_bytes(), b"old gui")
        self.assertFalse((self.root / "update-journal.json").exists())
        self.preserved()

    def test_daily_preference_decline_headless_and_offline_never_install(self):
        with patch.object(updater, "fetch", side_effect=self.fetch) as request:
            self.dialog.headless = True
            self.assertFalse(updater.check_and_install(self.root, self.dialog, launcher))
            self.dialog.headless = False
            settings = self.root / "app/Data/update-settings.json"
            settings.write_text('{"AutoCheck":false}')
            self.assertFalse(updater.check_and_install(self.root, self.dialog, launcher))
            request.assert_not_called()
            self.dialog.confirm = lambda _: False
            self.assertFalse(updater.check_and_install(self.root, self.dialog, launcher, manual=True))
            settings.write_text('{"AutoCheck":true}')
            count = request.call_count
            self.assertFalse(updater.check_and_install(self.root, self.dialog, launcher))
            self.assertEqual(request.call_count, count)
        with patch.object(updater, "fetch", side_effect=OSError("offline")):
            self.assertFalse(updater.check_and_install(self.root, self.dialog, launcher, manual=True))
        self.assertFalse((self.root / "app" / launcher.EXE).exists())
        self.preserved()

    def test_url_and_redirect_policy(self):
        self.assertTrue(updater.trusted_url(updater.API_URL))
        for url in ("http://github.com/" + updater.REPO, "https://evil.invalid/file", "https://github.com/other/repo/releases/download/v1/x.zip"):
            self.assertFalse(updater.trusted_url(url))
        with self.assertRaises(updater.UpdateError):
            updater.ReleaseRedirect().redirect_request(None, None, 302, "", {}, "http://github.com/payload")


if __name__ == "__main__":
    unittest.main()
