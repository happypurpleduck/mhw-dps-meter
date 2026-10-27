#!/usr/bin/env python3
"""Conservatively select expensive PR jobs; unknown paths always get full CI.

Uses exact PR base/head commits rather than a diff against a merge ref, so
classification stays correct for rebases and for fork pull requests.
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
    # The job checks out the full history, including both PR commits.
    raw = subprocess.check_output(
        ["git", "diff", "--name-only", "-z", base, head]
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
