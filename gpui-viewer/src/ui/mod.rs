//! GPUI views. `ViewerApp` is the window's root view: a hunt list on the left and
//! the selected hunt (or the trials page) on the right.

mod detail;
mod hunt_list;
mod plots;
mod trials;

use std::sync::Arc;

use gpui_kit::component::{
    ActiveTheme, Icon, IconName, Sizable as _, StyledExt as _,
    button::{Button, ButtonVariants as _},
    h_flex,
    input::{Input, InputEvent, InputState},
    resizable::{h_resizable, resizable_panel},
    scroll::ScrollableElement as _,
    table::{DataTable, TableEvent, TableState},
    theme::{Theme, ThemeMode},
    v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::{
    loader::Loaded,
    model::{FightLog, LogKind},
};
use hunt_list::HuntListDelegate;
use trials::TrialsDelegate;

/// Where the viewer reads logs from.
#[derive(Clone, Debug)]
pub enum Source {
    #[cfg(not(target_family = "wasm"))]
    Dir(std::path::PathBuf),
    Url(String),
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Tab {
    Overview,
    Moves,
    Monsters,
    Timeline,
}

impl Tab {
    pub const ALL: [Tab; 4] = [Tab::Overview, Tab::Moves, Tab::Monsters, Tab::Timeline];
    pub fn label(self) -> &'static str {
        match self {
            Tab::Overview => "Overview",
            Tab::Moves => "Moves",
            Tab::Monsters => "Monsters",
            Tab::Timeline => "Timeline",
        }
    }
}

#[derive(Clone, PartialEq, Eq, Debug)]
pub enum Page {
    Empty,
    Hunt(String),
    Trials,
}

pub struct ViewerApp {
    loaded: Option<Loaded>,
    status: SharedString,
    loading: bool,
    page: Page,
    tab: Tab,
    dark: bool,
    hunt_table: Entity<TableState<HuntListDelegate>>,
    search: Entity<InputState>,
    trials_table: Entity<TableState<TrialsDelegate>>,
    /// Per-hunt UI state.
    pub(crate) hide_common: bool,
    pub(crate) dps_window: f32,
    pub(crate) show_flinches: bool,
    /// Which hunter's moves the Moves tab shows (None = local).
    pub(crate) moves_slot: Option<usize>,
    /// Trial files ticked for comparison (at most two).
    pub(crate) compare: Vec<String>,
    _tasks: Vec<Task<()>>,
    source_task: Option<Task<()>>,
}

impl ViewerApp {
    pub fn new(initial: Option<Source>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let hunt_table = cx.new(|cx| TableState::new(HuntListDelegate::default(), window, cx).sortable(true));
        let trials_table = cx.new(|cx| TableState::new(TrialsDelegate::default(), window, cx).sortable(true));
        let search = cx.new(|cx| InputState::new(window, cx).placeholder("Filter hunts: quest, monster, hunter…"));

        cx.subscribe(&hunt_table, |this, table, event: &TableEvent, cx| {
            if let TableEvent::SelectRow(ix) = event {
                if let Some(file) = table.read(cx).delegate().file_at(*ix) {
                    this.open_hunt(file, cx);
                }
            }
        })
        .detach();
        cx.subscribe(&trials_table, |this, table, event: &TableEvent, cx| match event {
            TableEvent::SelectRow(ix) => {
                if let Some(file) = table.read(cx).delegate().file_at(*ix) {
                    this.toggle_compare(file, cx);
                }
            }
            TableEvent::DoubleClickedRow(ix) => {
                if let Some(file) = table.read(cx).delegate().file_at(*ix) {
                    this.open_hunt(file, cx);
                }
            }
            _ => {}
        })
        .detach();
        cx.subscribe(&search, |this, input, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) {
                let query = input.read(cx).value().to_string();
                this.hunt_table.update(cx, |table, cx| {
                    table.delegate_mut().set_query(query);
                    table.refresh(cx);
                });
            }
        })
        .detach();

        let mut this = Self {
            loaded: None,
            status: "No logs loaded".into(),
            loading: false,
            page: Page::Empty,
            tab: Tab::Overview,
            dark: true,
            hunt_table,
            search,
            trials_table,
            hide_common: false,
            dps_window: 20.0,
            show_flinches: false,
            moves_slot: None,
            compare: Vec::new(),
            _tasks: Vec::new(),
            source_task: None,
        };
        if let Some(source) = initial {
            this.load(source, cx);
        }
        this
    }

    pub fn selected_log(&self) -> Option<Arc<FightLog>> {
        match &self.page {
            Page::Hunt(file) => self.loaded.as_ref()?.log(file),
            _ => None,
        }
    }

    fn open_hunt(&mut self, file: String, cx: &mut Context<Self>) {
        if self.page != Page::Hunt(file.clone()) {
            self.page = Page::Hunt(file);
            self.moves_slot = None;
            cx.notify();
        }
    }

    fn toggle_compare(&mut self, file: String, cx: &mut Context<Self>) {
        if let Some(pos) = self.compare.iter().position(|f| *f == file) {
            self.compare.remove(pos);
        } else {
            if self.compare.len() >= 2 {
                self.compare.remove(0);
            }
            self.compare.push(file);
        }
        let compare = self.compare.clone();
        self.trials_table.update(cx, |table, cx| {
            table.delegate_mut().compare = compare;
            table.refresh(cx);
        });
        cx.notify();
    }

    pub(crate) fn set_kind_filter(&mut self, kind: Option<LogKind>, cx: &mut Context<Self>) {
        self.hunt_table.update(cx, |table, cx| {
            table.delegate_mut().kind = kind;
            table.refresh(cx);
        });
        cx.notify();
    }

    fn apply_loaded(&mut self, loaded: Loaded, preserve: bool, cx: &mut Context<Self>) {
        self.status = format!("{} · {} hunts", loaded.label, loaded.entries.len()).into();
        let first = loaded.entries.first().map(|e| e.file.clone());
        self.hunt_table.update(cx, |table, cx| {
            table.delegate_mut().set_entries(loaded.entries.clone());
            table.refresh(cx);
        });
        self.trials_table.update(cx, |table, cx| {
            table.delegate_mut().set_entries(&loaded.entries);
            table.refresh(cx);
        });
        if preserve {
            self.compare.retain(|file| loaded.logs.contains_key(file));
        } else {
            self.compare.clear();
        }
        let compare = self.compare.clone();
        self.trials_table.update(cx, |table, cx| {
            table.delegate_mut().compare = compare;
            table.refresh(cx);
        });
        if !preserve || matches!(&self.page, Page::Empty)
            || matches!(&self.page, Page::Hunt(file) if !loaded.logs.contains_key(file)) {
            self.page = first.map(Page::Hunt).unwrap_or(Page::Empty);
            self.moves_slot = None;
        }
        self.loaded = Some(loaded);
        self.loading = false;
        cx.notify();
    }

    fn fail(&mut self, message: String, cx: &mut Context<Self>) {
        self.status = format!("Error: {message}").into();
        self.loading = false;
        cx.notify();
    }

    pub fn load(&mut self, source: Source, cx: &mut Context<Self>) {
        self.source_task = None;
        self.loading = true;
        self.status = "Loading…".into();
        cx.notify();
        match source {
            #[cfg(not(target_family = "wasm"))]
            Source::Dir(path) => {
                self.source_task = Some(cx.spawn(async move |this, cx| {
                    let mut watch = crate::loader::DirectoryWatch::default();
                    let mut first = true;
                    loop {
                        let path = path.clone();
                        let (next_watch, result) = cx.background_spawn(async move {
                            let result = watch.poll(&path);
                            (watch, result)
                        }).await;
                        watch = next_watch;
                        if this.update(cx, |this, cx| match result {
                            Ok(Some(loaded)) => {
                                this.apply_loaded(loaded, !first, cx);
                                this.status = format!("{} · watching", this.status).into();
                                first = false;
                            }
                            Ok(None) => {}
                            Err(err) => this.fail(format!("{err:#} (retrying)"), cx),
                        }).is_err() { break; }
                        cx.background_executor().timer(std::time::Duration::from_secs(2)).await;
                    }
                }));
            }
            Source::Url(url) => {
                let client = cx.http_client();
                let task = cx.spawn(async move |this, cx| {
                    let result = crate::loader::load_url(client, &url).await;
                    this.update(cx, |this, cx| match result {
                        Ok(loaded) => this.apply_loaded(loaded, false, cx),
                        Err(err) => this.fail(format!("{err:#}"), cx),
                    })
                    .ok();
                });
                self.source_task = Some(task);
            }
        }
    }

    #[cfg(not(target_family = "wasm"))]
    fn pick_folder(&mut self, cx: &mut Context<Self>) {
        let receiver = cx.prompt_for_paths(PathPromptOptions {
            files: false,
            directories: true,
            multiple: false,
            prompt: Some("Open logs folder".into()),
        });
        let task = cx.spawn(async move |this, cx| {
            if let Ok(Ok(Some(paths))) = receiver.await {
                if let Some(path) = paths.into_iter().next() {
                    this.update(cx, |this, cx| this.load(Source::Dir(path), cx)).ok();
                }
            }
        });
        self._tasks.push(task);
    }

    fn load_sample(&mut self, cx: &mut Context<Self>) {
        #[cfg(not(target_family = "wasm"))]
        {
            match crate::loader::sample_dir() {
                Some(dir) => self.load(Source::Dir(dir), cx),
                None => self.fail("sample-logs folder not found next to the executable".into(), cx),
            }
        }
        #[cfg(target_family = "wasm")]
        {
            self.load(Source::Url("./sample-logs/".into()), cx);
        }
    }

    fn toggle_theme(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.dark = !self.dark;
        let mode = if self.dark { ThemeMode::Dark } else { ThemeMode::Light };
        Theme::change(mode, Some(window), cx);
        cx.refresh_windows();
    }

    fn render_toolbar(&mut self, cx: &mut Context<Self>) -> impl IntoElement {
        h_flex()
            .w_full()
            .px_3()
            .py_2()
            .gap_2()
            .items_center()
            .border_b_1()
            .border_color(cx.theme().border)
            .bg(cx.theme().sidebar)
            .child(div().font_semibold().text_base().child("⚔ MHW Fight Logs"))
            .child(div().text_sm().text_color(cx.theme().muted_foreground).flex_1().truncate().child(self.status.clone()))
            .when(self.loading, |this| this.child(gpui_kit::component::spinner::Spinner::new().small()))
            .child(
                Button::new("trials")
                    .small()
                    .ghost()
                    .label("Time trials")
                    .when(self.page == Page::Trials, |b| b.primary())
                    .on_click(cx.listener(|this, _, _, cx| {
                        this.page = Page::Trials;
                        cx.notify();
                    })),
            )
            .map(|this| {
                #[cfg(not(target_family = "wasm"))]
                {
                    this.child(
                        Button::new("open")
                            .small()
                            .primary()
                            .label("Open logs folder…")
                            .on_click(cx.listener(|this, _, _, cx| this.pick_folder(cx))),
                    )
                }
                #[cfg(target_family = "wasm")]
                {
                    this
                }
            })
            .child(Button::new("sample").small().label("Sample").on_click(cx.listener(|this, _, _, cx| this.load_sample(cx))))
            .child(
                Button::new("theme")
                    .small()
                    .ghost()
                    .icon(Icon::new(if self.dark { IconName::Sun } else { IconName::Moon }))
                    .on_click(cx.listener(|this, _, window, cx| this.toggle_theme(window, cx))),
            )
    }

    fn render_sidebar(&mut self, cx: &mut Context<Self>) -> impl IntoElement {
        let kind = self.hunt_table.read(cx).delegate().kind;
        let counts = self.hunt_table.read(cx).delegate().counts();
        v_flex()
            .size_full()
            .border_r_1()
            .border_color(cx.theme().border)
            .child(
                v_flex()
                    .p_2()
                    .gap_2()
                    .child(Input::new(&self.search).small().cleanable(true))
                    .child(
                        h_flex()
                            .gap_1()
                            .child(filter_button("all", format!("All {}", counts.0), kind.is_none(), cx.listener(|this, _, _, cx| this.set_kind_filter(None, cx))))
                            .child(filter_button("quests", format!("Quests {}", counts.1), kind == Some(LogKind::Quest), cx.listener(|this, _, _, cx| this.set_kind_filter(Some(LogKind::Quest), cx))))
                            .child(filter_button("trials-f", format!("Trials {}", counts.2), kind == Some(LogKind::Trial), cx.listener(|this, _, _, cx| this.set_kind_filter(Some(LogKind::Trial), cx)))),
                    ),
            )
            .child(div().flex_1().min_h_0().child(DataTable::new(&self.hunt_table).stripe(true)))
    }

    fn render_empty(&self, cx: &Context<Self>) -> impl IntoElement {
        v_flex()
            .size_full()
            .items_center()
            .justify_center()
            .gap_3()
            .text_color(cx.theme().muted_foreground)
            .child(div().text_xl().font_semibold().text_color(cx.theme().foreground).child("Load your fight logs"))
            .child(div().max_w(px(520.)).text_center().child(
                "The MHW DPS Meter plugin writes one JSON file per hunt plus index.json into nativePC/plugins/CSharp/MhwDpsMeter/logs/. Open that folder, pass it as the first argument, set MHW_LOGS, or try the sample data.",
            ))
    }
}

fn filter_button(
    id: &'static str,
    label: String,
    active: bool,
    on_click: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
) -> Button {
    let button = Button::new(id).xsmall().label(label).on_click(on_click);
    if active { button.primary() } else { button.ghost() }
}

impl Render for ViewerApp {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let content: AnyElement = match self.page.clone() {
            Page::Empty if self.loaded.is_none() => self.render_empty(cx).into_any_element(),
            Page::Empty => div().p_4().child("Pick a hunt on the left.").into_any_element(),
            Page::Trials => self.render_trials(window, cx).into_any_element(),
            Page::Hunt(_) => match self.selected_log() {
                Some(log) => self.render_detail(log, window, cx).into_any_element(),
                None => div().p_4().child("That log is no longer loaded.").into_any_element(),
            },
        };

        v_flex()
            .size_full()
            .bg(cx.theme().background)
            .text_color(cx.theme().foreground)
            .child(self.render_toolbar(cx))
            .child(if self.loaded.is_some() {
                // Draggable separator between the hunt list and the detail pane.
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .child(
                        h_resizable("split")
                            .child(
                                resizable_panel()
                                    .size(px(460.))
                                    .size_range(px(300.)..px(960.))
                                    .child(self.render_sidebar(cx)),
                            )
                            .child(
                                div()
                                    .size_full()
                                    .min_w_0()
                                    .child(div().size_full().overflow_y_scrollbar().child(div().p_4().w_full().child(content)))
                                    .into_any_element(),
                            ),
                    )
                    .into_any_element()
            } else {
                div().flex_1().min_h_0().size_full().overflow_y_scrollbar().child(div().p_4().child(content)).into_any_element()
            })
    }
}
