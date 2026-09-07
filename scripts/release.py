#!/usr/bin/env python3
"""Version synchronization, portable archives, and retryable GitHub releases."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PRODUCTS = {
    "plugin": ("src/MhwDpsMeter", "MhwDpsMeter"),
    "viewer": ("gpui-viewer", "mhw-log-viewer"),
}


def version(product):
    value = json.loads((ROOT / PRODUCTS[product][0] / "package.json").read_text())["version"]
    if not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise ValueError(f"Only stable semantic versions are supported: {value}")
    return value


def versions(write=False):
    replacements = [
        ("src/MhwDpsMeter/MhwDpsMeter.csproj", r"(<Version>)[^<]+(</Version>)", version("plugin")),
        ("gpui-viewer/Cargo.toml", r'(\[package\][\s\S]*?\nversion = ")[^"]+(")', version("viewer")),
        ("gpui-viewer/Cargo.lock", r'(\[\[package\]\]\nname = "mhw-log-viewer"\nversion = ")[^"]+(")', version("viewer")),
    ]
    for name, pattern, value in replacements:
        path = ROOT / name
        before = path.read_text(encoding="utf-8")
        after, count = re.subn(pattern, lambda m: m[1] + value + m[2], before)
        if count != 1:
            raise ValueError(f"Expected exactly one version in {name}, found {count}")
        if before != after:
            if not write:
                raise ValueError(f"Version mismatch in {name}; run npm run version-packages")
            path.write_text(after, encoding="utf-8")
    lock_path = ROOT / "package-lock.json"
    lock = json.loads(lock_path.read_text())
    changed = False
    for product, (directory, _) in PRODUCTS.items():
        if lock["packages"][directory]["version"] != version(product):
            if not write:
                raise ValueError(f"Version mismatch in package-lock.json: {directory}")
            lock["packages"][directory]["version"] = version(product)
            changed = True
    if changed:
        lock_path.write_text(json.dumps(lock, indent=2) + "\n")


def notes(product):
    path = ROOT / PRODUCTS[product][0] / "CHANGELOG.md"
    match = re.search(rf"^## {re.escape(version(product))}\s*\n(.*?)(?=^## |\Z)", path.read_text(), re.M | re.S)
    return match[1].strip() if match else None


def archive_name(product, platform=None):
    stem = f"{PRODUCTS[product][1]}-{version(product)}"
    if product == "plugin":
        return stem + ".zip"
    if platform not in ("linux", "windows"):
        raise ValueError("Viewer packaging requires --platform linux or windows")
    return f"{stem}-{platform}-x86_64" + (".tar.gz" if platform == "linux" else ".zip")


def package(product, platform=None):
    versions()
    files = []
    if product == "plugin":
        base = ROOT / "dist/Release"
        plugin = base / "nativePC/plugins/CSharp/MhwDpsMeter"
        maps = sorted((plugin / "Addresses").glob("*.map"))
        expected = sorted((ROOT / "src/MhwDpsMeter/Addresses").glob("*.map"))
        if not expected or [p.name for p in maps] != [p.name for p in expected]:
            raise ValueError("Plugin output is missing address maps or contains stale maps")
        files = [(p, p.relative_to(base).as_posix()) for p in [plugin / "MhwDpsMeter.dll", *maps]]
    else:
        target = "x86_64-unknown-linux-gnu" if platform == "linux" else "x86_64-pc-windows-msvc"
        binary = "mhw-log-viewer" + (".exe" if platform == "windows" else "")
        files = [(ROOT / "gpui-viewer/target" / target / "release" / binary, binary)]
        samples = ROOT / "gpui-viewer/sample-logs"
        files += [(p, f"sample-logs/{p.name}") for p in sorted(samples.glob("*.json"))]
        if not (samples / "index.json").is_file():
            raise ValueError("Sample log index is missing")
    files += [(ROOT / PRODUCTS[product][0] / "CHANGELOG.md", "CHANGELOG.md")]
    files += [(ROOT / ("README.md" if product == "plugin" else "gpui-viewer/README.md"), "README.md")]
    for path, _ in files:
        if not path.is_file():
            raise ValueError(f"Missing package input: {path}")
    output = ROOT / "dist/artifacts" / archive_name(product, platform)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.name.endswith(".zip"):
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            for path, name in files:
                archive.write(path, name)
    else:
        with tarfile.open(output, "w:gz") as archive:
            for path, name in files:
                info = archive.gettarinfo(str(path), arcname=name)
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                info.mode = 0o755 if name == "mhw-log-viewer" else 0o644
                with path.open("rb") as data:
                    archive.addfile(info, data)
    print(output)


def gh(*args):
    return subprocess.check_output(["gh", *args], text=True, cwd=ROOT).strip()


def publish():
    versions()
    if os.environ.get("GITHUB_REF") != "refs/heads/master":
        raise ValueError("Releases must run on master")
    if list((ROOT / ".changeset").glob("*.md")) != [ROOT / ".changeset/README.md"]:
        raise ValueError("Consume all pending changesets before publishing")
    sha = os.environ["GITHUB_SHA"]
    repo = os.environ["GITHUB_REPOSITORY"]
    pages = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100"))
    releases = {r["tag_name"]: r for page in pages for r in page}
    for product, (directory, name) in PRODUCTS.items():
        body = notes(product)
        if not body:
            print(f"Skipping {name}: no changelog entry for this version")
            continue
        tag = f"{name}-v{version(product)}"
        names = [archive_name("plugin")] if product == "plugin" else [archive_name("viewer", p) for p in ("linux", "windows")]
        checksum_name = f"{name}-{version(product)}-SHA256SUMS.txt"
        existing = releases.get(tag)
        if existing and not existing["draft"]:
            if not set(names + [checksum_name]).issubset({a["name"] for a in existing["assets"]}):
                raise ValueError(f"Published release {tag} has missing assets; inspect it manually")
            print(f"Skipping published release {tag}")
            continue
        if existing and existing["target_commitish"] != sha:
            raise ValueError(f"Draft {tag} belongs to another commit; rerun its original workflow")
        tag_commit = subprocess.run(["git", "rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}"], cwd=ROOT, text=True, capture_output=True)
        if tag_commit.returncode == 0 and tag_commit.stdout.strip() != sha:
            raise ValueError(f"Tag {tag} points to another commit")
        assets = [ROOT / "dist/artifacts" / n for n in names]
        sums = "".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n" for p in assets)
        checksum = ROOT / "dist/artifacts" / checksum_name
        checksum.write_text(sums)
        assets.append(checksum)
        if product == "plugin":
            body += "\n\nExtract the ZIP into the game root. The same DLL supports Windows and Linux/Proton; install the appropriate SharpPluginLoader separately."
        else:
            body += "\n\nExtract the archive and run mhw-log-viewer (Windows: mhw-log-viewer.exe). Linux: Ubuntu 24.04 or compatible, with the GPUI runtime libraries listed in README.md. Windows requires the Visual C++ v14 x64 runtime."
        with tempfile.TemporaryDirectory() as temp:
            body_file = Path(temp) / "notes.md"
            body_file.write_text(body + "\n")
            if not existing:
                gh("release", "create", tag, "--target", sha, "--draft", "--title", f"{name} {version(product)}", "--notes-file", str(body_file))
            gh("release", "upload", tag, *map(str, assets), "--clobber")
            gh("release", "edit", tag, "--draft=false", "--latest=false", "--notes-file", str(body_file))
        print(f"Published {tag}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["sync", "check", "package", "publish"])
    parser.add_argument("--product", choices=PRODUCTS)
    parser.add_argument("--platform", choices=["linux", "windows"])
    args = parser.parse_args()
    if args.command in ("sync", "check"):
        versions(args.command == "sync")
    elif args.command == "package":
        if not args.product:
            parser.error("package requires --product")
        package(args.product, args.platform)
    else:
        publish()
