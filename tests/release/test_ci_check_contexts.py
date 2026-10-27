"""Preserve platform check contexts while skipping unrelated PR builds.

GitHub expands a matrix only after its job-level if passes, so a conditional
matrix job can silently omit names configured as required status checks.
"""
from pathlib import Path
import re
import unittest


WORKFLOW = (Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml")


def job_section(source, name):
    match = re.search(
        rf"(?ms)^  {name}:\n(.*?)(?=^  [a-z_-]+:\n|\\Z)", source
    )
    if match is None:
        raise AssertionError(f"CI job {name} missing")
    return match.group(1)


class RequiredCheckContextTests(unittest.TestCase):
    def test_component_jobs_expand_with_stable_names_and_guard_all_steps(self):
        workflow = WORKFLOW.read_text()
        jobs = {
            "plugin": "Plugin (${{ matrix.os }})",
            "viewer": "Viewer (${{ matrix.platform }})",
            "web": "SolidJS web viewer",
        }
        for job, check_name in jobs.items():
            with self.subTest(job=job):
                section = job_section(workflow, job)
                self.assertIn(f"    name: {check_name}\n", section)
                self.assertIn("    needs: scope\n", section)
                self.assertNotRegex(section, r"(?m)^    if:")
                self.assertIn(
                    "      RUN_COMPONENT: ${{ github.event_name != 'pull_request' || "
                    + f"needs.scope.outputs.{job} == 'true' }}}}\n",
                    section,
                )
                steps = re.split(r"(?m)^      - ", section)[1:]
                self.assertGreater(len(steps), 1)
                self.assertTrue(steps[0].startswith("name: Not affected by PR\n"))
                self.assertIn("        if: env.RUN_COMPONENT != 'true'\n", steps[0])
                for step in steps[1:]:
                    self.assertRegex(
                        step,
                        r"(?m)^        if: env.RUN_COMPONENT == 'true'"
                        r"(?: && \\(runner\\.os == '(?:Windows|Linux)'\\))?$",
                    )

    def test_native_check_names_stay_platform_specific(self):
        workflow = WORKFLOW.read_text()
        plugin = job_section(workflow, "plugin")
        viewer = job_section(workflow, "viewer")
        self.assertIn("        os: [ubuntu-24.04, windows-2022]", plugin)
        self.assertIn("            platform: linux", viewer)
        self.assertIn("            platform: windows", viewer)


if __name__ == "__main__":
    unittest.main()
