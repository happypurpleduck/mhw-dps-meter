# CI: fast feedback and release checks

The workflow in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml)
always runs release tooling and classifies PR changes before starting expensive jobs.

- Documentation and changeset-only PRs: release tooling only.
- `web/`-only PRs: SolidJS tests, typecheck and Vite build.
- `gpui-viewer/`-only PRs: Linux and Windows GPUI tests, builds and archives.
- Plugin, shared data, CI, dependency or otherwise unclassified changes: all
  platforms and both viewers. The default is deliberately conservative.
- Every `master` push and manual dispatch: complete validation and conditional
  release creation/publishing. Scheduled runs: complete validation, **never**
  a release. PRs only upload short-lived artifacts.

Rust uses `Swatinem/rust-cache` for dependencies. Pull requests save their own
caches so repeat pushes to a PR don't rebuild all GPUI dependencies; successful
master builds seed caches available to new PRs. GitHub can evict caches after
seven days without access. The twice-weekly scheduled run keeps master caches
active during quiet periods, but cache misses and dependency changes may still
cause a ~10–15 minute cold native build.

For comparison, a warm master run previously finished the native viewers in
about 1–2 minutes on October 6, 2026; cold Windows GPUI compilation on
October 27 took over 10 minutes. These are observations, not CI time SLAs.
Investigate persistent regressions in GitHub Actions job **step timings**
(especially cache hit/miss, Linux apt install, Rust test compilation).

The GPUI browser/WebAssembly build requires nightly Rust and wasm-bindgen,
and is not a release artifact in this workflow; validate it separately before
claiming browser compatibility. These checks do not replace testing the
C# plugin inside Monster Hunter World on Windows and Proton.
