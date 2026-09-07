"""Exercise release safety and actual archive contents without GitHub writes."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SOURCE = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release", SOURCE / "scripts/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.patch = patch.object(release, "ROOT", self.root)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        for name in ["package-lock.json", "README.md", "gpui-viewer/Cargo.toml",
                     "gpui-viewer/Cargo.lock", "gpui-viewer/package.json",
                     "gpui-viewer/README.md", "gpui-viewer/CHANGELOG.md",
                     "src/MhwDpsMeter/package.json", "src/MhwDpsMeter/MhwDpsMeter.csproj",
                     "src/MhwDpsMeter/CHANGELOG.md", ".changeset/README.md"]:
            dst = self.root / name
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(SOURCE / name, dst)

    def write(self, name, content):
        dst = self.root / name
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_text(content)
        return dst

    def test_sync_and_drift(self):
        release.versions()
        package = self.root / "gpui-viewer/package.json"
        data = json.loads(package.read_text())
        data["version"] = "1.2.3"
        package.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "Version mismatch"):
            release.versions()
        release.versions(write=True)
        release.versions()
        self.assertIn('version = "1.2.3"', (self.root / "gpui-viewer/Cargo.toml").read_text())
        self.assertEqual(json.loads((self.root / "package-lock.json").read_text())["packages"]["gpui-viewer"]["version"], "1.2.3")

    def test_missing_version_field_fails(self):
        self.write("gpui-viewer/Cargo.toml", "[package]\n")
        with self.assertRaisesRegex(ValueError, "Expected exactly one"):
            release.versions(write=True)

    def test_plugin_archive_layout_and_missing_map(self):
        self.write("src/MhwDpsMeter/Addresses/test.map", "map")
        base = "dist/Release/nativePC/plugins/CSharp/MhwDpsMeter/"
        self.write(base + "MhwDpsMeter.dll", "dll")
        with self.assertRaisesRegex(ValueError, "address maps"):
            release.package("plugin")
        self.write(base + "Addresses/test.map", "map")
        self.write(base + "settings.json", "must not ship")
        release.package("plugin")
        with zipfile.ZipFile(self.root / "dist/artifacts" / release.archive_name("plugin")) as archive:
            self.assertIn("nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll", archive.namelist())
            self.assertIn("nativePC/plugins/CSharp/MhwDpsMeter/Addresses/test.map", archive.namelist())
            self.assertFalse(any("settings.json" in n for n in archive.namelist()))

    def test_viewer_archives_include_samples_and_executable_mode(self):
        self.write("gpui-viewer/sample-logs/index.json", "[]")
        for platform, target, binary in [
            ("linux", "x86_64-unknown-linux-gnu", "mhw-log-viewer"),
            ("windows", "x86_64-pc-windows-msvc", "mhw-log-viewer.exe"),
        ]:
            with self.subTest(platform=platform):
                with self.assertRaisesRegex(ValueError, "Missing package input"):
                    release.package("viewer", platform)
                self.write(f"gpui-viewer/target/{target}/release/{binary}", "binary")
                release.package("viewer", platform)
                path = self.root / "dist/artifacts" / release.archive_name("viewer", platform)
                if platform == "linux":
                    with tarfile.open(path) as archive:
                        self.assertEqual(archive.getmember(binary).mode, 0o755)
                        self.assertIn("sample-logs/index.json", archive.getnames())
                else:
                    with zipfile.ZipFile(path) as archive:
                        self.assertIn(binary, archive.namelist())
                        self.assertIn("sample-logs/index.json", archive.namelist())

    def test_changelog_extracts_only_current_version(self):
        value = release.version("plugin")
        self.write("src/MhwDpsMeter/CHANGELOG.md", f"# Mod\n\n## {value}\n\n### Patch Changes\n\n- New fix\n\n## 0.0.1\n\n- Old fix\n")
        self.assertEqual(release.notes("plugin"), "### Patch Changes\n\n- New fix")

    def prepare_publish(self):
        self.env = patch.dict(os.environ, GITHUB_REF="refs/heads/master", GITHUB_SHA="abc", GITHUB_REPOSITORY="test/repo")
        self.env.start()
        self.addCleanup(self.env.stop)
        self.write("src/MhwDpsMeter/CHANGELOG.md", f"## {release.version('plugin')}\n\n- Fix\n")
        # Keep only the plugin eligible even when testing a versioned checkout.
        self.write("gpui-viewer/CHANGELOG.md", "# Viewer\n")
        self.write("dist/artifacts/" + release.archive_name("plugin"), "archive")
        self.tag = f"MhwDpsMeter-v{release.version('plugin')}"

    def test_publish_draft_upload_then_publish_and_skip_existing(self):
        self.prepare_publish()
        with patch.object(release, "gh", return_value="[[]]") as gh, patch.object(release.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)):
            release.publish()
            self.assertEqual([c.args[:2] for c in gh.call_args_list], [("api", "--paginate"), ("release", "create"), ("release", "upload"), ("release", "edit")])
            self.assertIn("--draft", gh.call_args_list[1].args)
            self.assertIn("--draft=false", gh.call_args_list[3].args)
        checksum = next((self.root / "dist/artifacts").glob("*SHA256SUMS.txt"))
        self.assertIn(release.archive_name("plugin"), checksum.read_text())
        existing = {"tag_name": self.tag, "draft": False, "assets": [{"name": release.archive_name("plugin")}, {"name": checksum.name}]}
        with patch.object(release, "gh", return_value=json.dumps([[existing]])) as gh:
            release.publish()
            self.assertEqual(gh.call_count, 1)

    def test_failed_upload_leaves_draft(self):
        self.prepare_publish()
        with patch.object(release, "gh", side_effect=["[[]]", "", RuntimeError("upload failed")]) as gh, patch.object(release.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)):
            with self.assertRaisesRegex(RuntimeError, "upload failed"):
                release.publish()
            self.assertFalse(any(c.args[:2] == ("release", "edit") for c in gh.call_args_list))

    def test_draft_retry_requires_original_commit(self):
        self.prepare_publish()
        existing = {"tag_name": self.tag, "draft": True, "target_commitish": "other"}
        with patch.object(release, "gh", return_value=json.dumps([[existing]])):
            with self.assertRaisesRegex(ValueError, "another commit"):
                release.publish()
        existing["target_commitish"] = "abc"
        with patch.object(release, "gh", side_effect=[json.dumps([[existing]]), "", ""]) as gh, patch.object(release.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)):
            release.publish()
            self.assertEqual([c.args[:2] for c in gh.call_args_list], [("api", "--paginate"), ("release", "upload"), ("release", "edit")])

    def test_publish_rejects_pending_changesets_and_other_branches(self):
        self.prepare_publish()
        self.write(".changeset/pending.md", "---\n---\n")
        with self.assertRaisesRegex(ValueError, "pending changesets"):
            release.publish()
        with patch.dict(os.environ, GITHUB_REF="refs/heads/feature"):
            with self.assertRaisesRegex(ValueError, "must run on master"):
                release.publish()


if __name__ == "__main__":
    unittest.main()
