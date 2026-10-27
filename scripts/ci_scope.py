#!/usr/bin/env python3
"""Conservatively select expensive PR jobs; unknown paths always get full CI.

Compare the PR head with its merge base, ignoring changes made only on the
base branch after divergence (including when a PR is rebased or from a fork).
"""
import subprocess
import sys


def select_jobs(paths):
    """Return (plugin, viewer, web). Tooling always runs independently."""
    neutral = (
        "README.md",
        "web/README.md",
        "gpui-viewer/README.md",
        "data/README.md",
    )
    relevant = [
        path for path in paths
        if path not in neutral
        and not path.startswith(("docs/", ".changeset/"))
    ]
    if not relevant:
        return False, False, False
    if all(path.startswith("web/") for path in relevant):
        return False, False, True
    if all(path.startswith("gpui-viewer/") for path in relevant):
        return False, True, False
    # Plugin schema changes can affect both viewers; workflow, dependency,
    # tooling and unclassified files are deliberately treated as full scope.
    return True, True, True


def changed_paths(base, head):
    # Checkout has full history; use the merge base so commits only on the
    # target branch cannot broaden the PR's CI scope.
    # Disable rename detection so both sides of a cross-component move are
    # classified. A removed plugin/viewer path must not be hidden by its new name.
    raw = subprocess.check_output(
        ["git", "diff", "--no-renames", "--name-only", "-z", f"{base}...{head}"]
    )
    return [path.decode("utf-8") for path in raw.split(b"\0") if path]


def main():
    if len(sys.argv) != 3:
        raise SystemExit("usage: ci_scope.py <base-sha> <head-sha>")
    paths = changed_paths(*sys.argv[1:])
    # On an unexpected empty diff, fail closed rather than silently skipping
    # all expensive validation (a nonempty PR should have a changed path).
    selected = (True, True, True) if not paths else select_jobs(paths)
    for name, enabled in zip(("plugin", "viewer", "web"), selected):
        print(f"{name}={str(enabled).lower()}")


if __name__ == "__main__":
    main()
