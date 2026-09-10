# MHW Fight Logs — GPUI viewer

Desktop and browser viewer for the fight logs written by the [MHW DPS Meter](../README.md) plugin, built on [GPUI](https://www.gpui.rs/) (Zed's UI framework) through [gpui-kit](https://github.com/longbridge/gpui-kit) 0.6, which bundles GPUI, its platform layer, the `gpui_web` browser backend and the styled component library (DataTable, Tabs, Buttons, Tags, plot primitives).

The same crate produces both targets:

- **Native**: `cargo run --release -- <logs folder>`; a real window on Linux (Wayland/X11), macOS or Windows.
- **Browser**: `wasm32-unknown-unknown` via wasm-bindgen, rendered on WebGPU (WebGL2 fallback) inside a canvas. Served by the tiny Vite shell in `www/`.

## Release downloads

Download the Linux x86-64 `.tar.gz` or Windows x86-64 `.zip` from this
repository's Releases page and extract the entire archive. Run
`mhw-log-viewer` (Windows: `mhw-log-viewer.exe`); sample logs are included.
See the [release guide](../docs/releases.md) and [changelog](CHANGELOG.md).

Linux archives are built on Ubuntu 24.04 (glibc 2.39). On Ubuntu 24.04,
install the runtime libraries with:

```sh
sudo apt-get install libfontconfig1 libwayland-client0 libxkbcommon-x11-0 \
  libx11-xcb1 libvulkan1 libssl3t64 libzstd1 libwebkit2gtk-4.1-0
```

A working Vulkan-capable graphics driver is also required. Windows builds
require the Microsoft Visual C++ v14 Redistributable (x64).

## Native

```bash
cd gpui-viewer
cargo run --release -- "/path/to/Monster Hunter World/nativePC/plugins/CSharp/MhwDpsMeter/logs"
# or:  MHW_LOGS=/path/to/logs cargo run --release
# or:  cargo run --release            # looks in the usual Steam locations, else shows the empty state
cargo test                            # parser + analysis tests against sample-logs/
```

The toolbar has **Open logs folder…** (native file dialog), **Sample** (the bundled `sample-logs/`), **Time trials**, and a light/dark toggle. A folder URL (`http://…/logs/`) also works as the argument.

Linux needs the usual GPUI system libraries: Vulkan, xkbcommon, wayland-client, X11, fontconfig.

## Browser

```bash
rustup toolchain install nightly -t wasm32-unknown-unknown -c rust-src
cargo install wasm-bindgen-cli --version "$(grep -A1 '^name = "wasm-bindgen"$' Cargo.lock | grep version | cut -d'"' -f2)"
scripts/build-wasm.sh            # nightly wasm build + bindgen into www/src/wasm
cd www && pnpm install && pnpm dev   # http://localhost:3000
```

`?logs=<url>` loads any served folder containing an `index.json`; without it the sample logs load. The dev server sends the COOP/COEP headers WebGPU wants. The nightly toolchain is required by `gpui_web`'s `parking_lot` nightly feature; the app itself uses the single-threaded web runtime, so no shared-memory flags are needed (see `.cargo/config.toml`).

The browser build bundles Inter and IBM Plex Sans (`fonts/`, SIL OFL) because the canvas has no system fonts, and serves gpui-kit's icon set from `www/public/assets/icons`.

## What it shows

- **Hunt list** (left): every quest and time trial from `index.json`, text filter, All/Quests/Trials toggle, sortable columns; click a row to open it.
- **Overview**: party table with damage, DPS, carts and share; cumulative damage per hunter as a multi-series plot with red (monster death), orange (enrage) and yellow (hunter cart) markers; rolling DPS with a selectable window.
- **Moves**: per-move breakdown per hunter (damage bar, share, hits, crit rate, avg, max, tenderized); your rows are exact, teammates' rows (plugin 0.4.0+) are estimated from award-table increments and labelled "(est.)". Toggle to hide `Common::` actions (hits registered after the move ended).
- **Parts**: damage by monster part with a per-part top hunter; part tags exist only on your exact hits (see `docs/part-damage.md`).
- **Layout**: the hunt list and detail pane are separated by a draggable resizable panel.
- **Monsters**: HP lost per large monster and how much of it was yours.
- **Timeline**: enrage, unenrage, death, cart, weapon swaps, joins and leaves; flinches on request.
- **Time trials**: personal bests per weapon and window (same rule as the plugin's F9 panel), every run, and a two-run comparison (click two rows) with overlaid damage curves and top moves.

Exact per-hit data exists only for the local hunter: the game runs its deal-damage function only for hits simulated on your client. Teammates get their award total, the 2-second damage curve, and estimated per-move rows once the plugin matched their hunter entity to their slot.

## Layout

```
src/model.rs      serde mirror of FightLog.cs (schema 1 and 2)
src/analysis.rs   pure computations + unit tests (moves, curves, PBs, formatting)
src/loader.rs     folder / URL loading (index.json + every log)
src/ui/           ViewerApp root, hunt list DataTable, detail tabs, trials page, LinesPlot
src/lib.rs        launch() shared by both targets; wasm entry `run()`
src/main.rs       native entry
www/              Vite shell for the wasm build; scripts/build-wasm.sh produces www/src/wasm
sample-logs/      anonymised real logs used by tests, the Sample button and the web dev server
```

`LinesPlot` is a custom `Plot` (gpui-component's `Plot` trait + `ScaleLinear`, `Line`, `Grid`, `PlotAxis`) because the stock `LineChart` draws a single series and a hunt has up to four hunters.
