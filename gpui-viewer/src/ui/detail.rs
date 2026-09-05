//! Right pane for one hunt: header + stat tiles, then Overview / Moves / Monsters / Timeline.

use std::sync::Arc;

use gpui_kit::component::{
    ActiveTheme as _, Sizable as _, StyledExt as _,
    button::{Button, ButtonVariants as _},
    checkbox::Checkbox,
    h_flex,
    tab::TabBar,
    table::{Table, TableBody, TableCell, TableHead, TableHeader, TableRow},
    v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::{
    Tab, ViewerApp,
    hunt_list::result_tag,
    plots::{LinesPlot, Marker, Series, legend, slot_color},
};
use crate::analysis::{
    damage_curves, format_date, format_duration, format_int, hit_stats, monster_breakdown, move_breakdown, move_display_name, pct,
    rolling_dps,
};
use crate::model::{FightLog, LogKind};

impl ViewerApp {
    pub(super) fn render_detail(&mut self, log: Arc<FightLog>, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let tab = self.tab;
        let body: AnyElement = match tab {
            Tab::Overview => self.render_overview(&log, cx).into_any_element(),
            Tab::Moves => self.render_moves(&log, cx).into_any_element(),
            Tab::Monsters => render_monsters(&log, cx).into_any_element(),
            Tab::Timeline => self.render_timeline(&log, cx).into_any_element(),
        };
        v_flex()
            .gap_4()
            .child(render_header(&log, cx))
            .child(
                TabBar::new("detail-tabs")
                    .underline()
                    .selected_index(Tab::ALL.iter().position(|t| *t == tab).unwrap_or(0))
                    .on_click(cx.listener(|this, ix: &usize, _, cx| {
                        this.tab = Tab::ALL[*ix];
                        cx.notify();
                    }))
                    .children(Tab::ALL.iter().map(|t| t.label())),
            )
            .child(body)
    }

    fn render_overview(&mut self, log: &FightLog, cx: &mut Context<Self>) -> impl IntoElement {
        let series: Vec<Series> = damage_curves(log)
            .into_iter()
            .map(|(slot, points)| Series {
                label: log.players.iter().find(|p| p.slot == slot).map(|p| p.name.clone()).unwrap_or_else(|| format!("Slot {}", slot + 1)).into(),
                color: slot_color(slot),
                points: points.into_iter().map(|p| (p.t, p.damage as f32)).collect(),
            })
            .collect();
        let markers: Vec<Marker> = log
            .events
            .iter()
            .filter_map(|e| match e.r#type.as_str() {
                "death" => Some(Marker { t: e.t, color: cx.theme().danger }),
                "enrage" => Some(Marker { t: e.t, color: cx.theme().warning }),
                _ => None,
            })
            .collect();
        let window = self.dps_window;
        let dps_series: Vec<Series> = series
            .iter()
            .enumerate()
            .zip(crate::analysis::active_slots(log))
            .map(|((_, s), slot)| Series {
                label: s.label.clone(),
                color: s.color,
                points: rolling_dps(log, slot, window),
            })
            .collect();

        v_flex()
            .gap_4()
            .child(render_players_table(log, cx))
            .when(log.samples.len() > 1, |this| {
                this.child(
                    card(cx)
                        .child(section_title("Cumulative damage", cx))
                        .child(div().text_xs().text_color(cx.theme().muted_foreground).child("Dashed: red = large monster death, orange = enrage."))
                        .child(div().h(px(280.)).w_full().child(LinesPlot::new(series.clone(), markers)))
                        .child(legend(&series, cx)),
                )
                .child(
                    card(cx)
                        .child(
                            h_flex()
                                .justify_between()
                                .items_center()
                                .child(section_title("Rolling DPS", cx))
                                .child(h_flex().gap_1().children([10.0f32, 20.0, 30.0, 60.0].into_iter().map(|w| {
                                    let active = (self.dps_window - w).abs() < 0.5;
                                    let button = Button::new(SharedString::from(format!("win-{}", w as u32)))
                                        .xsmall()
                                        .label(format!("{}s", w as u32))
                                        .on_click(cx.listener(move |this, _, _, cx| {
                                            this.dps_window = w;
                                            cx.notify();
                                        }));
                                    if active { button.primary() } else { button.ghost() }
                                }))),
                        )
                        .child(div().h(px(220.)).w_full().child(LinesPlot::new(dps_series, Vec::new()))),
                )
            })
    }

    fn render_moves(&mut self, log: &FightLog, cx: &mut Context<Self>) -> impl IntoElement {
        if log.hits.is_empty() {
            return v_flex().child(muted("This log has no per-hit data. Logs written by plugin 0.4.0 or later include every hit of the local hunter.", cx));
        }
        let weapon = log.local_player().and_then(|p| p.weapon.clone());
        let mut rows = move_breakdown(&log.hits, |k| move_display_name(weapon.as_deref(), k));
        if self.hide_common {
            rows.retain(|r| !r.is_common);
        }
        let top = rows.iter().map(|r| r.damage).max().unwrap_or(1).max(1) as f32;

        v_flex()
            .gap_3()
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_4()
                    .items_center()
                    .child(muted(
                        format!(
                            "Per-move breakdown of your {} hits ({}). Teammates' hits are not visible to the plugin.",
                            log.hits.len(),
                            weapon.clone().unwrap_or_else(|| "unknown weapon".into())
                        ),
                        cx,
                    ))
                    .child(
                        Checkbox::new("hide-common")
                            .label("Hide Common:: actions (hits that landed after the move ended)")
                            .checked(self.hide_common)
                            .on_click(cx.listener(|this, checked: &bool, _, cx| {
                                this.hide_common = *checked;
                                cx.notify();
                            })),
                    ),
            )
            .child(
                Table::new()
                    .small()
                    .border_1()
                    .border_color(cx.theme().border)
                    .rounded(cx.theme().radius)
                    .child(
                        TableHeader::new().child(
                            TableRow::new()
                                .child(TableHead::new().w(px(260.)).child("Move"))
                                .child(TableHead::new().w(px(220.)).child("Damage"))
                                .child(TableHead::new().text_right().child("Share"))
                                .child(TableHead::new().text_right().child("Hits"))
                                .child(TableHead::new().text_right().child("Crit"))
                                .child(TableHead::new().text_right().child("Avg"))
                                .child(TableHead::new().text_right().child("Max"))
                                .child(TableHead::new().text_right().child("Tenderized")),
                        ),
                    )
                    .child(TableBody::new().children(rows.iter().enumerate().map(|(ix, r)| {
                        let fill = if r.is_common { cx.theme().muted_foreground } else { cx.theme().chart_1 };
                        TableRow::new()
                            .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                            .child(
                                TableCell::new().w(px(260.)).child(
                                    div()
                                        .flex()
                                        .flex_col()
                                        .child(div().child(r.name.clone()))
                                        .child(div().text_xs().text_color(cx.theme().muted_foreground).child(r.key.clone())),
                                ),
                            )
                            .child(
                                TableCell::new().w(px(220.)).child(
                                    h_flex()
                                        .gap_2()
                                        .items_center()
                                        .child(
                                            div().h(px(10.)).w(px(130.)).rounded_sm().bg(cx.theme().border).child(
                                                div().h_full().rounded_sm().bg(fill).w(px(130.0 * r.damage as f32 / top)),
                                            ),
                                        )
                                        .child(div().text_xs().child(format_int(r.damage))),
                                ),
                            )
                            .child(TableCell::new().text_right().child(pct(r.share, 1)))
                            .child(TableCell::new().text_right().child(r.hits.to_string()))
                            .child(TableCell::new().text_right().child(pct(r.crit_rate(), 0)))
                            .child(TableCell::new().text_right().child(format!("{:.1}", r.avg())))
                            .child(TableCell::new().text_right().child(r.max.to_string()))
                            .child(TableCell::new().text_right().child(r.tenderized.to_string()))
                    }))),
            )
    }

    fn render_timeline(&mut self, log: &FightLog, cx: &mut Context<Self>) -> impl IntoElement {
        let monster_name = |id: &str| log.monsters.iter().find(|m| m.id == id).map(|m| m.name.clone()).unwrap_or_else(|| id.to_string());
        let player_name = |slot: usize| log.players.iter().find(|p| p.slot == slot).map(|p| p.name.clone()).unwrap_or_else(|| format!("slot {}", slot + 1));
        let show_flinches = self.show_flinches;
        let events: Vec<_> = log.events.iter().filter(|e| show_flinches || e.r#type != "flinch").collect();
        let flinches = log.events.iter().filter(|e| e.r#type == "flinch").count();

        v_flex()
            .gap_3()
            .child(
                Checkbox::new("show-flinch")
                    .label(format!("Show flinches ({flinches})"))
                    .checked(show_flinches)
                    .on_click(cx.listener(|this, checked: &bool, _, cx| {
                        this.show_flinches = *checked;
                        cx.notify();
                    })),
            )
            .child(
                Table::new()
                    .small()
                    .border_1()
                    .border_color(cx.theme().border)
                    .rounded(cx.theme().radius)
                    .child(
                        TableHeader::new().child(
                            TableRow::new()
                                .child(TableHead::new().w(px(80.)).text_right().child("Time"))
                                .child(TableHead::new().w(px(110.)).child("Event"))
                                .child(TableHead::new().w(px(220.)).child("Who / what"))
                                .child(TableHead::new().child("Detail")),
                        ),
                    )
                    .child(TableBody::new().children(events.iter().enumerate().map(|(ix, e)| {
                        let who = match (&e.monster, e.slot) {
                            (Some(m), _) => monster_name(m),
                            (None, Some(slot)) => player_name(slot),
                            _ => String::new(),
                        };
                        TableRow::new()
                            .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                            .child(TableCell::new().w(px(80.)).text_right().child(format_duration(e.t)))
                            .child(TableCell::new().w(px(110.)).child(event_tag(&e.r#type)))
                            .child(TableCell::new().w(px(220.)).child(who))
                            .child(TableCell::new().child(e.detail.clone().unwrap_or_default()))
                    }))),
            )
    }
}

fn render_header(log: &FightLog, cx: &App) -> impl IntoElement {
    let me = log.local_player();
    let stats = hit_stats(&log.hits);
    let total = log.total_damage();
    let duration = log.duration_seconds.max(1.0);
    v_flex()
        .gap_3()
        .child(
            h_flex()
                .flex_wrap()
                .items_baseline()
                .gap_3()
                .child(div().text_2xl().font_semibold().child(log.quest_name.clone()))
                .child(result_tag(&log.result))
                .when(log.kind == LogKind::Trial, |this| this.child(gpui_kit::component::tag::Tag::info().outline().xsmall().child("time trial")))
                .child(div().text_sm().text_color(cx.theme().muted_foreground).child(format!(
                    "{} · {} · schema {}",
                    format_date(&log.started_at),
                    log.stage.clone().unwrap_or_else(|| format!("stage {}", log.stage_id)),
                    log.schema_version
                ))),
        )
        .child(
            h_flex()
                .flex_wrap()
                .gap_2()
                .child(stat_tile("Total damage", format_int(total), format!("{} hunter{}", log.players.len(), if log.players.len() == 1 { "" } else { "s" }), None, cx))
                .child(stat_tile("Party DPS", format!("{:.1}", total as f32 / duration), format!("over {}", format_duration(log.duration_seconds)), None, cx))
                .when_some(me, |this, p| {
                    this.child(stat_tile(
                        "Your damage",
                        format_int(p.damage),
                        format!("{:.1}% of party · {}", p.percent, p.weapon.clone().unwrap_or_else(|| "weapon ?".into())),
                        Some(cx.theme().primary),
                        cx,
                    ))
                    .child(stat_tile(
                        "Your hits",
                        stats.hits.to_string(),
                        if stats.hits > 0 {
                            format!("crit {} · avg {:.0} · max {}", pct(stats.crit_rate(), 0), stats.avg(), stats.max)
                        } else {
                            "no per-hit data (schema 1)".into()
                        },
                        None,
                        cx,
                    ))
                    .when(stats.hits > 0, |this| {
                        this.child(stat_tile(
                            "Active DPS",
                            format!("{:.1}", stats.active_dps()),
                            format!("first→last hit {}", format_duration(stats.active_seconds)),
                            None,
                            cx,
                        ))
                    })
                }),
        )
}

fn render_players_table(log: &FightLog, cx: &App) -> impl IntoElement {
    Table::new()
        .small()
        .border_1()
        .border_color(cx.theme().border)
        .rounded(cx.theme().radius)
        .child(
            TableHeader::new().child(
                TableRow::new()
                    .child(TableHead::new().w(px(220.)).child("Hunter"))
                    .child(TableHead::new().w(px(130.)).child("Weapon"))
                    .child(TableHead::new().text_right().child("Damage"))
                    .child(TableHead::new().text_right().child("DPS"))
                    .child(TableHead::new().w(px(220.)).child("Share")),
            ),
        )
        .child(TableBody::new().children(log.players.iter().enumerate().map(|(ix, p)| {
            TableRow::new()
                .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                .child(
                    TableCell::new().w(px(220.)).child(
                        h_flex()
                            .gap_2()
                            .items_center()
                            .child(div().size_2p5().rounded_full().bg(slot_color(p.slot)))
                            .child(div().font_medium().child(p.name.clone()))
                            .when(p.is_local, |this| this.child(gpui_kit::component::tag::Tag::primary().xsmall().child("you"))),
                    ),
                )
                .child(TableCell::new().w(px(130.)).child(p.weapon.clone().unwrap_or_else(|| "—".into())))
                .child(TableCell::new().text_right().child(format_int(p.damage)))
                .child(TableCell::new().text_right().child(format!("{:.1}", p.dps)))
                .child(
                    TableCell::new().w(px(220.)).child(
                        h_flex()
                            .gap_2()
                            .items_center()
                            .child(
                                div().h(px(10.)).w(px(120.)).rounded_sm().bg(cx.theme().border).child(
                                    div().h_full().rounded_sm().bg(slot_color(p.slot)).w(px(120.0 * (p.percent / 100.0).clamp(0.0, 1.0))),
                                ),
                            )
                            .child(div().text_xs().child(format!("{:.1}%", p.percent))),
                    ),
                )
        })))
}

fn render_monsters(log: &FightLog, cx: &App) -> impl IntoElement {
    let rows = monster_breakdown(log);
    if rows.is_empty() {
        return v_flex().child(muted("No large monsters were recorded for this log.", cx));
    }
    v_flex().child(
        Table::new()
            .small()
            .border_1()
            .border_color(cx.theme().border)
            .rounded(cx.theme().radius)
            .child(
                TableHeader::new().child(
                    TableRow::new()
                        .child(TableHead::new().w(px(200.)).child("Monster"))
                        .child(TableHead::new().text_right().child("Max HP"))
                        .child(TableHead::new().w(px(240.)).child("HP lost"))
                        .child(TableHead::new().text_right().child("Your damage"))
                        .child(TableHead::new().text_right().child("Your hits"))
                        .child(TableHead::new().text_right().child("Seen at"))
                        .child(TableHead::new().text_right().child("Died at")),
                ),
            )
            .child(TableBody::new().children(rows.iter().enumerate().map(|(ix, r)| {
                let frac = if r.monster.max_health > 0.0 { (r.hp_lost / r.monster.max_health).clamp(0.0, 1.0) } else { 0.0 };
                TableRow::new()
                    .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                    .child(TableCell::new().w(px(200.)).child(format!("{} {}", r.monster.name, r.monster.id)))
                    .child(TableCell::new().text_right().child(format_int(r.monster.max_health as i64)))
                    .child(
                        TableCell::new().w(px(240.)).child(
                            h_flex()
                                .gap_2()
                                .items_center()
                                .child(
                                    div().h(px(10.)).w(px(120.)).rounded_sm().bg(cx.theme().border).child(
                                        div().h_full().rounded_sm().bg(cx.theme().danger).w(px(120.0 * frac)),
                                    ),
                                )
                                .child(div().text_xs().child(format!("{} ({})", format_int(r.hp_lost as i64), pct(frac, 0)))),
                        ),
                    )
                    .child(TableCell::new().text_right().child(format_int(r.your_damage)))
                    .child(TableCell::new().text_right().child(r.your_hits.to_string()))
                    .child(TableCell::new().text_right().child(format_duration(r.monster.first_seen_t)))
                    .child(TableCell::new().text_right().child(r.monster.died_t.map(format_duration).unwrap_or_else(|| "—".into())))
            }))),
    )
}

pub(super) fn event_tag(kind: &str) -> gpui_kit::component::tag::Tag {
    use gpui_kit::component::tag::Tag;
    match kind {
        "death" => Tag::danger(),
        "enrage" => Tag::warning(),
        "join" | "leave" => Tag::success(),
        "weapon" => Tag::primary(),
        _ => Tag::secondary(),
    }
    .outline()
    .xsmall()
    .child(kind.to_string())
}

pub(super) fn card(cx: &App) -> Div {
    v_flex().gap_2().p_3().rounded(cx.theme().radius_lg).bg(cx.theme().secondary)
}

pub(super) fn section_title(text: &'static str, _cx: &App) -> impl IntoElement {
    div().font_semibold().child(text)
}

pub(super) fn muted(text: impl Into<SharedString>, cx: &App) -> impl IntoElement {
    div().text_sm().text_color(cx.theme().muted_foreground).child(text.into())
}

pub(super) fn stat_tile(title: &'static str, value: String, desc: String, accent: Option<Hsla>, cx: &App) -> impl IntoElement {
    v_flex()
        .min_w(px(150.))
        .p_3()
        .rounded(cx.theme().radius_lg)
        .bg(cx.theme().secondary)
        .child(div().text_xs().text_color(cx.theme().muted_foreground).child(title))
        .child(div().text_2xl().font_semibold().when_some(accent, |this, c| this.text_color(c)).child(value))
        .child(div().text_xs().text_color(cx.theme().muted_foreground).child(desc))
}
