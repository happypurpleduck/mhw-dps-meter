#![cfg_attr(target_family = "wasm", allow(unused))]

#[cfg(not(target_family = "wasm"))]
fn main() {
    use mhw_log_viewer::{Source, launch, loader};

    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    // `mhw-log-viewer <dir|url>`, else $MHW_LOGS / a known Steam install, else empty.
    let source = match std::env::args().nth(1) {
        Some(arg) if arg.starts_with("http://") || arg.starts_with("https://") => Some(Source::Url(arg)),
        Some(arg) => Some(Source::Dir(arg.into())),
        None => loader::default_log_dirs().into_iter().find(|d| d.join("index.json").is_file()).map(Source::Dir),
    };

    gpui_kit::application()
        .with_assets(gpui_kit::assets::Assets)
        .run(move |cx| launch(cx, source));
}

#[cfg(target_family = "wasm")]
fn main() {}
