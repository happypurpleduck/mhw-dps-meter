//! MHW DPS Meter fight-log viewer on GPUI. The same crate builds the native desktop
//! app (`src/main.rs`) and the browser build (`run` below, via wasm-bindgen).

pub mod analysis;
pub mod loader;
pub mod model;
pub mod ui;

use gpui_kit::component::Root;
use gpui_kit::*;

pub use ui::Source;

/// Initializes gpui-kit, applies the dark theme and opens the main window.
pub fn launch(cx: &mut App, source: Option<Source>) {
    gpui_kit::init(cx);
    install_fonts(cx);
    gpui_kit::component::theme::Theme::change(gpui_kit::component::theme::ThemeMode::Dark, None, cx);

    let options = WindowOptions {
        titlebar: Some(TitlebarOptions {
            title: Some("MHW Fight Logs".into()),
            ..Default::default()
        }),
        window_bounds: Some(WindowBounds::Windowed(Bounds::centered(None, size(px(1380.), px(880.)), cx))),
        ..Default::default()
    };
    cx.open_window(options, move |window, cx| {
        let view = cx.new(|cx| ui::ViewerApp::new(source, window, cx));
        cx.new(|cx| Root::new(view, window, cx))
    })
    .expect("failed to open window");
    cx.activate(true);
}

/// The browser has no system fonts; bundle Inter for the UI and IBM Plex Sans because
/// gpui_web maps the `.SystemUIFont` alias to it (see gpui-kit's story-web).
fn install_fonts(cx: &mut App) {
    #[cfg(target_family = "wasm")]
    {
        use std::borrow::Cow;
        cx.text_system()
            .add_fonts(vec![
                Cow::Borrowed(include_bytes!("../fonts/Inter-Regular.ttf").as_slice()),
                Cow::Borrowed(include_bytes!("../fonts/IBMPlexSans-Regular.ttf").as_slice()),
            ])
            .expect("bundled fonts");
        let theme = cx.global_mut::<gpui_kit::component::theme::Theme>();
        theme.font_family = "Inter".into();
    }
    #[cfg(not(target_family = "wasm"))]
    {
        let _ = cx;
    }
}

#[cfg(target_family = "wasm")]
mod web {
    use std::cell::RefCell;

    use wasm_bindgen::prelude::*;

    use super::*;

    thread_local! {
        static APPLICATION: RefCell<Option<ApplicationHandle>> = const { RefCell::new(None) };
    }

    fn absolute_url(url: &str) -> String {
        if url.starts_with("http://") || url.starts_with("https://") {
            return url.to_string();
        }
        let base = web_sys::window().and_then(|w| w.location().href().ok()).unwrap_or_default();
        web_sys::Url::new_with_base(url, &base).map(|u| u.href()).unwrap_or_else(|_| url.to_string())
    }

    /// Entry point called from `www/src/main.js`. `logs_url` is a folder URL holding an
    /// index.json; `None` starts on the bundled sample logs.
    #[wasm_bindgen]
    pub fn run(logs_url: Option<String>) -> Result<(), JsValue> {
        console_error_panic_hook::set_once();
        console_log::init_with_level(log::Level::Info).ok();
        gpui_kit::platform::web_init();

        let app = gpui_kit::platform::single_threaded_web().with_assets(gpui_kit::assets::Assets::new(absolute_url("./")));
        // GPUI's fetch client wants absolute URLs; resolve relative ones against the page.
        let source = Source::Url(absolute_url(&logs_url.unwrap_or_else(|| "./sample-logs/".into())));
        let handle = app.run_embedded(move |cx| launch(cx, Some(source)));
        APPLICATION.with(|application| *application.borrow_mut() = Some(handle));
        Ok(())
    }
}
