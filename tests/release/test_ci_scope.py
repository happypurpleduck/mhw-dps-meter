"""Keep PR CI scoping conservative and deterministic."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ci_scope", ROOT / "scripts/ci_scope.py")
scope = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)


class ScopeTests(unittest.TestCase):
    def test_documentation_and_changesets_need_only_tooling(self):
        self.assertEqual(
            scope.select_jobs(["docs/monster-gauges.md", ".changeset/example.md", "README.md"]),
            (False, False, False),
        )

    def test_web_only_changes_need_web(self):
        self.assertEqual(
            scope.select_jobs(["web/src/data/parse.ts", "web/tests/analysis.test.ts",
                               ".changeset/change.md"]),
            (False, False, True),
        )

    def test_gpui_only_changes_need_native_viewer(self):
        self.assertEqual(
            scope.select_jobs(["gpui-viewer/src/model.rs", "gpui-viewer/Cargo.lock"]),
            (False, True, False),
        )

    def test_plugin_and_shared_files_need_every_check(self):
        for files in (
            ["src/MhwDpsMeter/FightLog.cs"],
            ["data/monster-parts.json"],
            [".github/workflows/ci.yml"],
            ["scripts/release.py"],
            ["global.json"],
            ["web/src/data/parse.ts", "gpui-viewer/src/model.rs"],
        ):
            with self.subTest(files=files):
                self.assertEqual(scope.select_jobs(files), (True, True, True))

    def test_unknown_paths_fail_closed(self):
        self.assertEqual(scope.select_jobs(["new-top-level-file"]), (True, True, True))

    def test_base_branch_changes_do_not_expand_pr_scope(self):
        """Use divergent real Git branches to test the CLI's diff and selection."""
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)

            def git(*args):
                return subprocess.check_output(
                    ["git", "-C", str(repo), *args], text=True
                ).strip()

            git("init", "-q", "-b", "main")
            git("config", "user.name", "CI scope test")
            git("config", "user.email", "ci-scope@example.invalid")
            (repo / "web").mkdir()
            (repo / "src").mkdir()
            (repo / "web" / "view.ts").write_text("original web\n")
            (repo / "src" / "plugin.cs").write_text("original plugin\n")
            git("add", ".")
            git("commit", "-qm", "initial")

            git("switch", "-qc", "web-only")
            (repo / "web" / "view.ts").write_text("changed web\n")
            git("commit", "-qam", "web change")
            head = git("rev-parse", "HEAD")

            git("switch", "-q", "main")
            (repo / "src" / "plugin.cs").write_text("changed plugin\n")
            git("commit", "-qam", "plugin change")
            base = git("rev-parse", "HEAD")

            result = subprocess.run(
                [sys.executable, str(ROOT / "scripts/ci_scope.py"), base, head],
                cwd=repo,
                capture_output=True,
                text=True,
                check=True,
            )
            self.assertEqual(
                result.stdout.splitlines(),
                ["plugin=false", "viewer=false", "web=true"],
            )
