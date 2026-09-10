//! Right pane for one hunt: header + stat tiles, then Overview / Moves / Monsters / Timeline.

use crate::names::{weapon_display_name, weapon_icon_svg};
use crate::names::stage_display_name;
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
    damage_curves, format_date, format_duration, format_int, has_part_data, hit_stats, local_exact_hits, monster_breakdown,
    move_breakdown, move_display_name, part_breakdown, pct, rolling_dps, interval_dps,
};
use crate::model::{FightLog, LogKind};

impl ViewerApp {
    pub(super) fn render_detail(&mut self, log: Arc<FightLog>, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let tab = self.tab;
        let body: AnyElement = match tab {
            Tab::Overview => self.render_overview(&log, cx).into_any_element(),
            Tab::Moves => self.render_moves(&log, cx).into_any_element(),
            Tab::Parts => self.render_parts(&log, cx).into_any_element(),
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
                // Yellow — distinct from monster-death red and enrage orange.
                "cart" => Some(Marker { t: e.t, color: hsla(48. / 360., 0.95, 0.48, 1.) }),
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

        let direct_series = series.iter().zip(crate::analysis::active_slots(log))
            .map(|(s, slot)| Series { label: s.label.clone(), color: s.color, points: interval_dps(log, slot) })
            .collect();

        v_flex()
            .gap_4()
            .child(render_players_table(log, cx))
            .when(log.samples.len() > 1, |this| {
                this.child(
                    card(cx)
                        .child(section_title("Cumulative damage", cx))
                        .child(div().text_xs().text_color(cx.theme().muted_foreground).child("Dashed: red = large monster death, orange = enrage, yellow = hunter cart."))
                        .child(div().h(px(280.)).w_full().child(LinesPlot::new(series.clone(), markers)))
                        .child(legend(&series, cx)),
                )
                .child(
                    card(cx)
                        .child(section_title("DPS", cx))
                        .child(div().text_xs().text_color(cx.theme().muted_foreground)
                            .child("Damage gained / elapsed time between consecutive samples. No rolling window; party samples are usually 2s apart."))
                        .child(div().h(px(220.)).w_full().child(LinesPlot::new(direct_series, Vec::new())))
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
        let slots = log.slots_with_hits();
        let slot = self.moves_slot.filter(|s| slots.contains(s)).or_else(|| slots.first().copied()).unwrap_or(0);
        let player = log.players.iter().find(|p| p.slot == slot);
        let weapon = player.and_then(|p| p.weapon.clone());
        let hits = log.hits_for_slot(slot);
        let estimated = !hits.is_empty() && hits.iter().all(|h| h.estimated);
        let mut rows = move_breakdown(hits.iter().copied(), |k| move_display_name(weapon.as_deref(), k));
        if self.hide_common {
            rows.retain(|r| !r.is_common);
        }
        let top = rows.iter().map(|r| r.damage).max().unwrap_or(1).max(1) as f32;
        let bar_color = slot_color(slot);
        let who = match player {
            Some(p) if p.is_local => "your".to_string(),
            Some(p) => format!("{}'s", p.name),
            None => format!("slot {}'s", slot + 1),
        };
        let summary = if estimated {
            format!(
                "Estimated per-move damage for {} ({}): {} award-table increments credited to the move being performed. Crit and tenderize are unknown for teammates.",
                player.map(|p| p.name.clone()).unwrap_or_else(|| "teammate".into()),
                weapon_display_name(weapon.as_deref()),
                hits.len()
            )
        } else {
            format!("Per-move breakdown of {who} {} hits ({}).", hits.len(), weapon_display_name(weapon.as_deref()))
        };

        v_flex()
            .gap_3()
            .when(slots.len() > 1, |this| {
                this.child(
                    TabBar::new("moves-hunters")
                        .pill()
                        .selected_index(slots.iter().position(|s| *s == slot).unwrap_or(0))
                        .on_click(cx.listener({
                            let slots = slots.clone();
                            move |this, ix: &usize, _, cx| {
                                this.moves_slot = slots.get(*ix).copied();
                                cx.notify();
                            }
                        }))
                        .children(slots.iter().map(|s| {
                            let name = log.players.iter().find(|p| p.slot == *s).map(|p| p.name.clone()).unwrap_or_else(|| format!("Slot {}", s + 1));
                            let est = log.hits_for_slot(*s).iter().all(|h| h.estimated);
                            SharedString::from(if est { format!("{name} (est.)") } else { name })
                        })),
                )
            })
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_4()
                    .items_center()
                    .child(muted(summary, cx))
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
                                .child(TableHead::new().w(px(64.)).text_right().child("Share"))
                                .child(TableHead::new().w(px(48.)).text_right().child("Hits"))
                                .child(TableHead::new().w(px(48.)).text_right().child("Crit"))
                                .child(TableHead::new().w(px(48.)).text_right().child("Avg"))
                                .child(TableHead::new().w(px(48.)).text_right().child("Max"))
                                .child(TableHead::new().w(px(72.)).text_right().child("Tenderized")),
                        ),
                    )
                    .child(TableBody::new().children(rows.iter().enumerate().map(|(ix, r)| {
                        let fill = if r.is_common { cx.theme().muted_foreground } else { bar_color };
                        let frac = (r.damage as f32 / top).clamp(0.0, 1.0);
                        TableRow::new()
                            .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                            .child(
                                TableCell::new().w(px(260.)).child(
                                    div()
                                        .flex()
                                        .flex_col()
                                        .w_full()
                                        .min_w_0()
                                        .overflow_hidden()
                                        .child(div().truncate().child(r.name.clone()))
                                        .child(
                                            div()
                                                .text_xs()
                                                .text_color(cx.theme().muted_foreground)
                                                .truncate()
                                                .child(r.key.clone()),
                                        ),
                                ),
                            )
                            .child(
                                TableCell::new().w(px(220.)).child(meter_bar(
                                    frac,
                                    fill,
                                    cx.theme().border,
                                    format_int(r.damage),
                                )),
                            )
                            .child(TableCell::new().w(px(64.)).text_right().child(pct(r.share, 1)))
                            .child(TableCell::new().w(px(48.)).text_right().child(r.hits.to_string()))
                            .child(TableCell::new().w(px(48.)).text_right().child(pct(r.crit_rate(), 0)))
                            .child(TableCell::new().w(px(48.)).text_right().child(format!("{:.1}", r.avg())))
                            .child(TableCell::new().w(px(48.)).text_right().child(r.max.to_string()))
                            .child(TableCell::new().w(px(72.)).text_right().child(r.tenderized.to_string()))
                    }))),
            )
    }

    fn render_parts(&mut self, log: &FightLog, cx: &mut Context<Self>) -> impl IntoElement {
        if !log.hits.iter().any(|h| h.monster.is_some()) {
            return v_flex().child(muted("This log has no monster-targeted hits to group by part.", cx));
        }
        let monsters = &log.monsters;
        let monster_id = self
            .parts_monster
            .as_deref()
            .filter(|id| monsters.iter().any(|m| m.id == *id))
            .or_else(|| monsters.first().map(|m| m.id.as_str()));
        let rows = part_breakdown(log, monster_id);
        let top = rows.iter().map(|r| r.damage).max().unwrap_or(1).max(1) as f32;
        let summary = if has_part_data(log) {
            "Damage by monster part. Part tags come from your exact hits; teammate award deltas have no part."
                .to_string()
        } else {
            "No part tags in this log yet. Untagged and teammate rows appear under Unknown part — party award totals are not split by part.".to_string()
        };

        v_flex()
            .gap_3()
            .when(monsters.len() > 1, |this| {
                this.child(
                    TabBar::new("parts-monsters")
                        .pill()
                        .selected_index(
                            monsters
                                .iter()
                                .position(|m| Some(m.id.as_str()) == monster_id)
                                .unwrap_or(0),
                        )
                        .on_click(cx.listener({
                            let ids: Vec<String> = monsters.iter().map(|m| m.id.clone()).collect();
                            move |this, ix: &usize, _, cx| {
                                this.parts_monster = ids.get(*ix).cloned();
                                cx.notify();
                            }
                        }))
                        .children(monsters.iter().map(|m| SharedString::from(m.name.clone()))),
                )
            })
            .child(muted(summary, cx))
            .child(
                Table::new()
                    .small()
                    .border_1()
                    .border_color(cx.theme().border)
                    .rounded(cx.theme().radius)
                    .child(
                        TableHeader::new().child(
                            TableRow::new()
                                .child(TableHead::new().w(px(180.)).child("Part"))
                                .child(TableHead::new().w(px(220.)).child("Damage"))
                                .child(TableHead::new().w(px(64.)).text_right().child("Share"))
                                .child(TableHead::new().w(px(48.)).text_right().child("Hits"))
                                .child(TableHead::new().w(px(200.)).child("Most damage")),
                        ),
                    )
                    .child(TableBody::new().children(rows.iter().enumerate().map(|(ix, r)| {
                        let frac = (r.damage as f32 / top).clamp(0.0, 1.0);
                        let top_hunter = r.hunters.first().map(|h| {
                            format!("{} {}", h.name, format_int(h.damage))
                        }).unwrap_or_else(|| "—".into());
                        let top_color = r.hunters.first().map(|h| slot_color(h.slot)).unwrap_or(cx.theme().muted_foreground);
                        TableRow::new()
                            .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                            .child(
                                TableCell::new().w(px(180.)).child(
                                    div()
                                        .flex()
                                        .flex_col()
                                        .child(div().child(r.name.clone()))
                                        .when_some(r.part, |d, part| {
                                            d.child(
                                                div()
                                                    .text_xs()
                                                    .text_color(cx.theme().muted_foreground)
                                                    .child(format!("id {part}")),
                                            )
                                        }),
                                ),
                            )
                            .child(
                                TableCell::new().w(px(220.)).child(meter_bar(
                                    frac,
                                    Hsla::from(rgb(0xf26bb8)),
                                    cx.theme().border,
                                    format_int(r.damage),
                                )),
                            )
                            .child(TableCell::new().w(px(64.)).text_right().child(pct(r.share, 1)))
                            .child(TableCell::new().w(px(48.)).text_right().child(r.hits.to_string()))
                            .child(
                                TableCell::new().w(px(200.)).child(
                                    h_flex()
                                        .gap_2()
                                        .items_center()
                                        .child(div().size(px(8.)).rounded_full().bg(top_color))
                                        .child(div().truncate().child(top_hunter)),
                                ),
                            )
                    }))),
            )
            .when_some(rows.first().cloned(), |this, part| {
                this.child(
                    card(cx)
                        .child(div().font_medium().child(part.name.clone()))
                        .child(muted("Hunters ranked by damage to this part", cx))
                        .children(part.hunters.into_iter().map(|h| {
                            h_flex()
                                .gap_2()
                                .items_center()
                                .child(div().size(px(8.)).rounded_full().bg(slot_color(h.slot)))
                                .child(div().flex_1().truncate().child(h.name.clone()))
                                .when(h.estimated, |row| {
                                    row.child(
                                        gpui_kit::component::tag::Tag::warning()
                                            .outline()
                                            .xsmall()
                                            .child("est."),
                                    )
                                })
                                .child(div().child(format_int(h.damage)))
                                .child(
                                    div()
                                        .w(px(48.))
                                        .text_right()
                                        .text_color(cx.theme().muted_foreground)
                                        .child(pct(h.share, 1)),
                                )
                        })),
                )
            })
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
                            .child(TableCell::new().child(match (e.r#type.as_str(), e.detail.as_deref()) {
                                ("weapon", Some(detail)) => weapon_display_name(Some(detail)),
                                (_, Some(detail)) => detail.to_string(),
                                _ => String::new(),
                            }))
                    }))),
            )
    }
}

fn render_header(log: &FightLog, cx: &App) -> impl IntoElement {
    let me = log.local_player();
    let stats = hit_stats(local_exact_hits(log));
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
                    stage_display_name(log.stage_id, log.stage.as_deref()),
                    log.schema_version
                )))
                .when_some(log.rewards.as_ref(), |this, r| {
                    this.child(div().text_sm().text_color(cx.theme().muted_foreground).child(format!(
                        "rewards {}z · {} HRP · {}★",
                        format_int(r.zenny),
                        format_int(r.hunter_rank_points),
                        r.stars
                    )))
                }),
        )
        .child(
            // Tiles share one fixed height and stretch to fill the row.
            h_flex()
                .w_full()
                .flex_wrap()
                .gap_2()
                .child(stat_tile("Total damage", format_int(total), format!("{} hunter{}", log.players.len(), if log.players.len() == 1 { "" } else { "s" }), None, cx))
                .child(stat_tile("Party DPS", format!("{:.1}", total as f32 / duration), format!("over {}", format_duration(log.duration_seconds)), None, cx))
                .when_some(me, |this, p| {
                    this.child(stat_tile(
                        "Your damage",
                        format_int(p.damage),
                        format!("{:.1}% of party · {}", p.percent, weapon_display_name(p.weapon.as_deref())),
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
                    .child(TableHead::new().w(px(280.)).child("Hunter"))
                    .child(TableHead::new().w(px(88.)).text_right().child("Damage"))
                    .child(TableHead::new().w(px(56.)).text_right().child("DPS"))
                    .child(TableHead::new().w(px(48.)).text_right().child("Carts"))
                    .child(TableHead::new().w(px(220.)).child("Share")),
            ),
        )
        .child(TableBody::new().children(log.players.iter().enumerate().map(|(ix, p)| {
            TableRow::new()
                .when(ix % 2 == 1, |row| row.bg(cx.theme().table_even))
                .child(
                    TableCell::new().w(px(280.)).child(
                        h_flex()
                            .gap_2()
                            .items_center()
                            .overflow_hidden()
                            .child(div().size_2p5().flex_shrink_0().rounded_full().bg(slot_color(p.slot)))
                            .child(weapon_icon(p.weapon.as_deref(), px(20.), cx.theme().foreground))
                            .child(div().font_medium().truncate().child(p.name.clone()))
                            .when(p.is_local, |this| this.child(gpui_kit::component::tag::Tag::primary().xsmall().child("you"))),
                    ),
                )
                .child(TableCell::new().w(px(88.)).text_right().child(format_int(p.damage)))
                .child(TableCell::new().w(px(56.)).text_right().child(format!("{:.1}", p.dps)))
                .child(TableCell::new().w(px(48.)).text_right().child(if p.carts > 0 { p.carts.to_string() } else { "—".into() }))
                .child(TableCell::new().w(px(220.)).child(meter_bar(
                    (p.percent / 100.0).clamp(0.0, 1.0),
                    slot_color(p.slot),
                    cx.theme().border,
                    format!("{:.1}%", p.percent),
                )))
        })))
}

/// Horizontal share/damage meter that clips to its cell instead of painting over neighbors.
fn meter_bar(frac: f32, fill: Hsla, track: Hsla, label: impl Into<SharedString>) -> impl IntoElement {
    h_flex()
        .w_full()
        .min_w_0()
        .gap_2()
        .items_center()
        .overflow_hidden()
        .child(
            div()
                .h(px(10.))
                .flex_1()
                .min_w_0()
                .rounded_sm()
                .bg(track)
                .overflow_hidden()
                .child(div().h_full().rounded_sm().bg(fill).w(relative(frac.clamp(0.0, 1.0)))),
        )
        .child(div().text_xs().flex_shrink_0().whitespace_nowrap().child(label.into()))
}

/// Class glyph sized for inline use next to a hunter or weapon label.
///
/// GPUI only paints `svg().data(...)` when a text color is set (monochrome alpha mask).
pub(super) fn weapon_icon(key: Option<&str>, size: Pixels, color: Hsla) -> impl IntoElement {
    match weapon_icon_svg(key) {
        Some(data) => svg().data(data).size(size).flex_shrink_0().text_color(color).into_any_element(),
        None => div().size(size).flex_shrink_0().into_any_element(),
    }
}

pub(super) fn weapon_label(key: Option<&str>, color: Hsla) -> impl IntoElement {
    h_flex()
        .gap(px(6.))
        .items_center()
        .overflow_hidden()
        .child(weapon_icon(key, px(18.), color))
        .child(div().truncate().child(weapon_display_name(key)))
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
                    .child(TableCell::new().w(px(240.)).child(meter_bar(
                        frac,
                        cx.theme().danger,
                        cx.theme().border,
                        format!("{} ({})", format_int(r.hp_lost as i64), pct(frac, 0)),
                    )))
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
        "cart" => Tag::warning(),
        "enrage" => Tag::warning(),
        "join" | "leave" => Tag::success(),
        "weapon" | "slotmatch" => Tag::primary(),
        _ => Tag::secondary(),
    }
    .outline()
    .xsmall()
    .child(kind.to_string())
}

pub(super) fn card(cx: &App) -> Div {
    v_flex().w_full().gap_2().p_3().rounded(cx.theme().radius_lg).bg(cx.theme().secondary)
}

pub(super) fn section_title(text: &'static str, _cx: &App) -> impl IntoElement {
    div().font_semibold().child(text)
}

pub(super) fn muted(text: impl Into<SharedString>, cx: &App) -> impl IntoElement {
    div().text_sm().text_color(cx.theme().muted_foreground).child(text.into())
}

pub(super) fn stat_tile(title: &'static str, value: String, desc: String, accent: Option<Hsla>, cx: &App) -> impl IntoElement {
    v_flex()
        .flex_1()
        .min_w(px(170.))
        .h(px(96.))
        .justify_between()
        .p_3()
        .rounded(cx.theme().radius_lg)
        .bg(cx.theme().secondary)
        .child(div().text_xs().text_color(cx.theme().muted_foreground).child(title))
        .child(div().text_2xl().font_semibold().when_some(accent, |this, c| this.text_color(c)).child(value))
        .child(div().text_xs().text_color(cx.theme().muted_foreground).truncate().child(desc))
}
