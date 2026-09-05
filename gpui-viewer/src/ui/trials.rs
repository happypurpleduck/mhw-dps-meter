//! Time-trial page: personal bests, every run (click two to compare), and the comparison.

use gpui_kit::component::{
    ActiveTheme as _, Sizable as _, StyledExt as _,
    h_flex,
    table::{Column, ColumnSort, Table, TableBody, TableCell, TableDelegate, TableHead, TableHeader, TableRow, TableState, DataTable},
    v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::{
    ViewerApp,
    detail::{card, muted, section_title},
    plots::{LinesPlot, Series, legend},
};
use crate::analysis::{cumulative_from_hits, format_date, format_int, hit_stats, move_breakdown, move_display_name, pct, trial_bests};
use crate::model::{IndexEntry, LogKind};

#[derive(Default)]
pub struct TrialsDelegate {
    trials: Vec<IndexEntry>,
    rows: Vec<usize>,
    sort: Option<(usize, ColumnSort)>,
    columns: Vec<Column>,
    pub compare: Vec<String>,
}

impl TrialsDelegate {
    pub fn set_entries(&mut self, entries: &[IndexEntry]) {
        self.trials = entries.iter().filter(|e| e.kind == LogKind::Trial).cloned().collect();
        if self.columns.is_empty() {
            self.columns = vec![
                Column::new("pick", "Compare").width(70.).text_center(),
                Column::new("date", "Date").width(130.).sortable().descending(),
                Column::new("weapon", "Weapon").width(120.).sortable(),
                Column::new("window", "Window").width(70.).sortable().text_right(),
                Column::new("damage", "Damage").width(90.).sortable().text_right(),
                Column::new("dps", "DPS").width(70.).sortable().text_right(),
            ];
            self.sort = Some((1, ColumnSort::Descending));
        }
        self.compare.clear();
        self.rebuild();
    }

    pub fn file_at(&self, row: usize) -> Option<String> {
        self.rows.get(row).map(|ix| self.trials[*ix].file.clone())
    }

    fn rebuild(&mut self) {
        let mut rows: Vec<usize> = (0..self.trials.len()).collect();
        if let Some((col_ix, sort)) = self.sort {
            let key = self.columns.get(col_ix).map(|c| c.key.to_string()).unwrap_or_default();
            let trials = &self.trials;
            rows.sort_by(|a, b| {
                let (a, b) = (&trials[*a], &trials[*b]);
                let ord = match key.as_str() {
                    "weapon" => a.weapon.cmp(&b.weapon),
                    "window" => a.duration_seconds.total_cmp(&b.duration_seconds),
                    "damage" => a.total_damage.cmp(&b.total_damage),
                    "dps" => (a.total_damage as f32 / a.duration_seconds.max(1.0)).total_cmp(&(b.total_damage as f32 / b.duration_seconds.max(1.0))),
                    _ => a.started_at.cmp(&b.started_at),
                };
                if sort == ColumnSort::Descending { ord.reverse() } else { ord }
            });
        }
        self.rows = rows;
    }
}

impl TableDelegate for TrialsDelegate {
    fn columns_count(&self, _: &App) -> usize {
        self.columns.len()
    }
    fn rows_count(&self, _: &App) -> usize {
        self.rows.len()
    }
    fn column(&self, col_ix: usize, _: &App) -> Column {
        self.columns[col_ix].clone()
    }
    fn perform_sort(&mut self, col_ix: usize, sort: ColumnSort, _: &mut Window, _: &mut Context<TableState<Self>>) {
        self.sort = match sort {
            ColumnSort::Default => None,
            other => Some((col_ix, other)),
        };
        self.rebuild();
    }
    fn render_td(&mut self, row_ix: usize, col_ix: usize, _: &mut Window, cx: &mut Context<TableState<Self>>) -> impl IntoElement {
        let Some(e) = self.rows.get(row_ix).map(|ix| &self.trials[*ix]) else {
            return div().into_any_element();
        };
        match self.columns[col_ix].key.as_ref() {
            "pick" => div()
                .w_full()
                .text_center()
                .text_color(cx.theme().primary)
                .child(if self.compare.contains(&e.file) { "✓" } else { "" })
                .into_any_element(),
            "date" => div().text_xs().child(format_date(&e.started_at)).into_any_element(),
            "weapon" => div().child(e.weapon.clone().unwrap_or_else(|| "?".into())).into_any_element(),
            "window" => div().w_full().text_right().child(format!("{}s", e.duration_seconds.round())).into_any_element(),
            "damage" => div().w_full().text_right().child(format_int(e.total_damage)).into_any_element(),
            "dps" => div().w_full().text_right().child(format!("{:.1}", e.total_damage as f32 / e.duration_seconds.max(1.0))).into_any_element(),
            _ => div().into_any_element(),
        }
    }
}

const COMPARE_COLORS: [u32; 2] = [0x52b8ff, 0xff9e2e];

impl ViewerApp {
    pub(super) fn render_trials(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let Some(loaded) = self.loaded.clone() else {
            return v_flex().child(muted("Load a logs folder first.", cx));
        };
        let bests = trial_bests(&loaded.entries);
        if bests.is_empty() {
            return v_flex()
                .gap_2()
                .child(div().text_2xl().font_semibold().child("Time trials"))
                .child(muted("No trials in this folder yet. Arm one with F8 in the training area.", cx));
        }

        let compared: Vec<_> = self.compare.iter().filter_map(|f| loaded.log(f)).collect();

        v_flex()
            .gap_5()
            .child(
                v_flex()
                    .gap_1()
                    .child(div().text_2xl().font_semibold().child("Time trials"))
                    .child(muted("Training-area runs recorded by the plugin (F8). Personal bests are per weapon and window, as the F9 panel shows them.", cx)),
            )
            .child(
                v_flex().gap_2().child(section_title("Personal bests", cx)).child(
                    Table::new()
                        .small()
                        .border_1()
                        .border_color(cx.theme().border)
                        .rounded(cx.theme().radius)
                        .child(
                            TableHeader::new().child(
                                TableRow::new()
                                    .child(TableHead::new().w(px(140.)).child("Weapon"))
                                    .child(TableHead::new().text_right().child("Window"))
                                    .child(TableHead::new().text_right().child("Best damage"))
                                    .child(TableHead::new().text_right().child("DPS"))
                                    .child(TableHead::new().text_right().child("Attempts"))
                                    .child(TableHead::new().w(px(150.)).child("Set on")),
                            ),
                        )
                        .child(TableBody::new().children(bests.iter().map(|b| {
                            TableRow::new()
                                .child(TableCell::new().w(px(140.)).child(b.weapon.clone()))
                                .child(TableCell::new().text_right().child(format!("{}s", b.duration_seconds)))
                                .child(TableCell::new().text_right().child(div().font_semibold().text_color(cx.theme().primary).child(format_int(b.best.total_damage))))
                                .child(TableCell::new().text_right().child(format!("{:.1}", b.best.total_damage as f32 / b.duration_seconds.max(1) as f32)))
                                .child(TableCell::new().text_right().child(b.attempts.to_string()))
                                .child(TableCell::new().w(px(150.)).child(format_date(&b.best.started_at)))
                        }))),
                ),
            )
            .child(
                v_flex()
                    .gap_2()
                    .child(h_flex().gap_2().items_baseline().child(section_title("All runs", cx)).child(muted("click two rows to compare, double-click to open", cx)))
                    .child(div().h(px(260.)).w_full().child(DataTable::new(&self.trials_table).stripe(true))),
            )
            .when(compared.len() == 2, |this| {
                let series: Vec<Series> = compared
                    .iter()
                    .enumerate()
                    .map(|(i, log)| Series {
                        label: format!("{} · {}", format_date(&log.started_at), format_int(log.players.first().map(|p| p.damage).unwrap_or(0))).into(),
                        color: rgb(COMPARE_COLORS[i]).into(),
                        points: cumulative_from_hits(&log.hits, 1.0).into_iter().map(|p| (p.t, p.damage as f32)).collect(),
                    })
                    .collect();
                this.child(
                    card(cx)
                        .child(section_title("Comparison", cx))
                        .child(h_flex().flex_wrap().gap_4().children(compared.iter().enumerate().map(|(i, log)| {
                            let stats = hit_stats(&log.hits);
                            h_flex()
                                .gap_2()
                                .items_center()
                                .text_sm()
                                .child(div().size_2p5().rounded_full().bg(Hsla::from(rgb(COMPARE_COLORS[i]))))
                                .child(format_date(&log.started_at))
                                .child(div().text_color(cx.theme().muted_foreground).child(log.players.first().and_then(|p| p.weapon.clone()).unwrap_or_else(|| "?".into())))
                                .child(div().font_semibold().child(format_int(log.players.first().map(|p| p.damage).unwrap_or(0))))
                                .child(div().text_color(cx.theme().muted_foreground).child(format!("{} hits · crit {}", stats.hits, pct(stats.crit_rate(), 0))))
                        })))
                        .child(div().h(px(280.)).w_full().child(LinesPlot::new(series.clone(), Vec::new())))
                        .child(legend(&series, cx))
                        .child(h_flex().gap_4().items_start().children(compared.iter().enumerate().map(|(i, log)| {
                            let weapon = log.players.first().and_then(|p| p.weapon.clone());
                            let rows = move_breakdown(&log.hits, |k| move_display_name(weapon.as_deref(), k));
                            v_flex()
                                .flex_1()
                                .gap_1()
                                .child(div().text_sm().font_medium().text_color(Hsla::from(rgb(COMPARE_COLORS[i]))).child(format!("Run {}: top moves", i + 1)))
                                .child(
                                    Table::new().xsmall().child(
                                        TableHeader::new().child(
                                            TableRow::new()
                                                .child(TableHead::new().child("Move"))
                                                .child(TableHead::new().text_right().child("Damage"))
                                                .child(TableHead::new().text_right().child("Share"))
                                                .child(TableHead::new().text_right().child("Hits")),
                                        ),
                                    )
                                    .child(TableBody::new().children(rows.iter().take(8).map(|r| {
                                        TableRow::new()
                                            .child(TableCell::new().child(r.name.clone()))
                                            .child(TableCell::new().text_right().child(format_int(r.damage)))
                                            .child(TableCell::new().text_right().child(pct(r.share, 0)))
                                            .child(TableCell::new().text_right().child(r.hits.to_string()))
                                    }))),
                                )
                        }))),
                )
            })
    }
}
