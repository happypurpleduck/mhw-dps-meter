# Builds and releases

The [CI workflow](../.github/workflows/ci.yml) builds and tests the mod and
native GPUI viewer on Ubuntu 24.04 and Windows Server 2022 (x86-64). Pull
requests get downloadable build artifacts. Browser/WASM and the separate
`web/` viewer are outside this native release pipeline.

## Authoring a release

1. Install Node.js 22, Python 3.11 or newer, and run `npm ci` at the repo root.
2. Run `npm run changeset`, select the affected products and bump types, and
   write a user-facing summary. Commit the generated `.changeset/*.md` file
   with the change. Maintenance changes can omit a changeset.
3. Merge to `master`. Once all builds and tests pass, the bot opens or updates
   `changeset-release/master`. Its release PR consumes the changesets, bumps
   the selected versions, and adds entries to each product's changelog.
4. Review and merge that PR. The resulting `master` build must pass all checks
   before the workflow creates the tags and publishes GitHub releases.

The first included changeset proposes plugin **0.4.1** and viewer **0.1.1**.
Existing version numbers stay unchanged until the release PR is merged.

Version metadata lives in `src/MhwDpsMeter/package.json` and
`gpui-viewer/package.json`. `npm run version-packages` runs Changesets, then
synchronizes the C# project, Cargo manifest, Cargo lockfile and npm lockfile.
`npm run check:versions` rejects drift in CI. Do not manually bump the C# or
Cargo version. `npm run test:release` tests synchronization, packaging and
publication failure handling without contacting GitHub.

Changelogs:

- [Mod](../src/MhwDpsMeter/CHANGELOG.md)
- [GPUI viewer](../gpui-viewer/CHANGELOG.md)

## Release downloads

| Product | Tag | Assets |
| --- | --- | --- |
| Mod | `MhwDpsMeter-vX.Y.Z` | `MhwDpsMeter-X.Y.Z.zip`, `MhwDpsMeter-X.Y.Z-SHA256SUMS.txt` |
| Viewer | `mhw-log-viewer-vX.Y.Z` | `mhw-log-viewer-X.Y.Z-linux-x86_64.tar.gz`, `mhw-log-viewer-X.Y.Z-windows-x86_64.zip`, `mhw-log-viewer-X.Y.Z-SHA256SUMS.txt` |

The mod ZIP contains `nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll`
and all address maps. Extract it into the game root. One DLL supports Windows
and Linux/Proton; SharpPluginLoader and the .NET Desktop Runtime remain separate
requirements described in the main README. CI builds the plugin on both hosts
and packages the Linux-built DLL once. Its build stamp records the commit SHA.

Viewer archives include the executable, sample logs, README and changelog.
Extract the entire archive; the Sample button finds logs beside the executable.
Linux binaries target glibc 2.39 (Ubuntu 24.04 or compatible) and need a working
Vulkan driver plus the runtime libraries documented in the viewer README.
Windows needs the Microsoft Visual C++ v14 x64 runtime. The archives do not
bundle GPU drivers or system libraries, and binaries are not code-signed.

Verify downloads with `sha256sum -c *-SHA256SUMS.txt` on Linux (download both
viewer archives to check the whole viewer manifest), or compare the Windows
archive's `Get-FileHash <archive> -Algorithm SHA256` with its manifest entry.
Distinct product tags keep versions independent; releases are not marked as
the repository-wide “latest” because that label cannot identify both products.

## Repository setup

In **Settings → Actions → General → Workflow permissions**, enable **Allow
GitHub Actions to create and approve pull requests**. The workflow grants its
release job `contents: write`, `pull-requests: write`, and `actions: write`;
build jobs only get read access. No npm token or personal token is required.

GitHub normally suppresses workflows triggered by a bot's `GITHUB_TOKEN`
push. After updating the release PR, this workflow explicitly dispatches CI
on the bot branch so the new versions receive checks. The branch-dispatched
run only tests and builds; publication is restricted to `master`.

If using branch protection, require `Release tooling`, both `Plugin` checks
and both `Viewer` checks. An organization policy that prohibits bot-created
PRs must be changed by an administrator, or you can run
`npm run version-packages` locally and submit the resulting PR yourself.

## Retry and local builds

Publication runs in the same workflow that builds the archives, so it does
not depend on a bot-created tag triggering another workflow. Each release is
created as a draft, receives all archives and checksums, then becomes public.
Published releases are skipped on later runs. An incomplete draft can be
retried from the **original workflow run**; drafts or tags pointing at another
commit cause a failure instead of replacing assets from a different build.
Do not delete or move published version tags.

Use **Actions → CI and release → Run workflow → master** to retry current
versions. Pending changesets produce a version PR instead of publishing.
Versions without a matching changelog entry are skipped (including the
pre-pipeline baseline versions).

Local packaging, from the repository root:

```sh
dotnet restore src/MhwDpsMeter/MhwDpsMeter.csproj --locked-mode
dotnet build src/MhwDpsMeter/MhwDpsMeter.csproj -c Release --no-restore
python3 scripts/release.py package --product plugin

# Use x86_64-pc-windows-msvc and --platform windows on Windows.
CARGO_TARGET_DIR="$PWD/gpui-viewer/target" cargo build \
  --manifest-path gpui-viewer/Cargo.toml --locked --release \
  --target x86_64-unknown-linux-gnu --bin mhw-log-viewer
python3 scripts/release.py package --product viewer --platform linux
```

Archives are written to `dist/artifacts/`. The existing `scripts/package.sh`
continues to support its original local mod packaging workflow.
