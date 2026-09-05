//! Left pane: sortable, filterable DataTable of every hunt and trial in the index.

use gpui_kit::component::{
    ActiveTheme as _, Sizable as _,
    table::{Column, ColumnSort, TableDelegate, TableState},
    tag::Tag,
};
use gpui_kit::*;

use crate::{
    analysis::{format_date, format_duration, format_int},
    model::{IndexEntry, LogKind},
};

#[derive(Default)]
pub struct HuntListDelegate {
    entries: Vec<IndexEntry>,
    /// Indices into `entries` after filtering and sorting.
    rows: Vec<usize>,
    query: String,
    pub kind: Option<LogKind>,
    sort: Option<(usize, ColumnSort)>,
    columns: Vec<Column>,
}

impl HuntListDelegate {
    fn columns() -> Vec<Column> {
        vec![
            Column::new("date", "Date").width(130.).sortable().descending(),
            Column::new("hunt", "Hunt").width(180.).sortable(),
            Column::new("result", "Result").width(78.).sortable(),
            Column::new("time", "Time").width(58.).sortable().text_right(),
            Column::new("damage", "Dmg").width(70.).sortable().text_right(),
        ]
    }

    pub fn set_entries(&mut self, entries: Vec<IndexEntry>) {
        self.entries = entries;
        if self.columns.is_empty() {
            self.columns = Self::columns();
            self.sort = Some((0, ColumnSort::Descending));
        }
        self.rebuild();
    }

    pub fn set_query(&mut self, query: String) {
        self.query = query.to_lowercase();
        self.rebuild();
    }

    pub fn file_at(&self, row: usize) -> Option<String> {
        self.rows.get(row).map(|ix| self.entries[*ix].file.clone())
    }

    /// (all, quests, trials)
    pub fn counts(&self) -> (usize, usize, usize) {
        let quests = self.entries.iter().filter(|e| e.kind == LogKind::Quest).count();
        (self.entries.len(), quests, self.entries.len() - quests)
    }

    fn rebuild(&mut self) {
        let query = self.query.trim().to_string();
        let kind = self.kind;
        let entries = &self.entries;
        let mut rows: Vec<usize> = (0..entries.len())
            .filter(|ix| {
                let e = &entries[*ix];
                if kind.is_some_and(|k| e.kind != k) {
                    return false;
                }
                if query.is_empty() {
                    return true;
                }
                let hay = format!(
                    "{} {} {} {} {}",
                    e.quest_name,
                    e.result,
                    e.weapon.as_deref().unwrap_or(""),
                    e.players.join(" "),
                    e.monsters.join(" ")
                )
                .to_lowercase();
                hay.contains(&query)
            })
            .collect();

        if let Some((col_ix, sort)) = self.sort {
            let key = self.columns.get(col_ix).map(|c| c.key.to_string()).unwrap_or_default();
            rows.sort_by(|a, b| {
                let (a, b) = (&entries[*a], &entries[*b]);
                let ord = match key.as_str() {
                    "hunt" => a.quest_name.cmp(&b.quest_name),
                    "result" => a.result.cmp(&b.result),
                    "time" => a.duration_seconds.total_cmp(&b.duration_seconds),
                    "damage" => a.total_damage.cmp(&b.total_damage),
                    _ => a.started_at.cmp(&b.started_at),
                };
                if sort == ColumnSort::Descending { ord.reverse() } else { ord }
            });
        }
        self.rows = rows;
    }
}

impl TableDelegate for HuntListDelegate {
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
        let Some(entry) = self.rows.get(row_ix).map(|ix| &self.entries[*ix]) else {
            return div().into_any_element();
        };
        let key = self.columns[col_ix].key.as_ref();
        match key {
            "date" => div().text_xs().child(format_date(&entry.started_at)).into_any_element(),
            "hunt" => div()
                .flex()
                .flex_col()
                .min_w_0()
                .child(div().truncate().child(entry.quest_name.clone()))
                .child(
                    div()
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .truncate()
                        .child(if entry.kind == LogKind::Trial {
                            entry.weapon.clone().unwrap_or_else(|| "trial".into())
                        } else {
                            entry.monsters.iter().chain(entry.players.iter()).cloned().collect::<Vec<_>>().join(" · ")
                        }),
                )
                .into_any_element(),
            "result" => result_tag(&entry.result).into_any_element(),
            "time" => div().w_full().text_right().child(format_duration(entry.duration_seconds)).into_any_element(),
            "damage" => div().w_full().text_right().child(format_int(entry.total_damage)).into_any_element(),
            _ => div().into_any_element(),
        }
    }

    fn cell_text(&self, row_ix: usize, col_ix: usize, _: &App) -> String {
        let Some(entry) = self.rows.get(row_ix).map(|ix| &self.entries[*ix]) else {
            return String::new();
        };
        match self.columns[col_ix].key.as_ref() {
            "date" => entry.started_at.clone(),
            "hunt" => entry.quest_name.clone(),
            "result" => entry.result.clone(),
            "time" => format!("{}", entry.duration_seconds),
            "damage" => entry.total_damage.to_string(),
            _ => String::new(),
        }
    }
}

pub fn result_tag(result: &str) -> Tag {
    match result {
        "complete" => Tag::success(),
        "fail" => Tag::danger(),
        "trial" => Tag::info(),
        "abandon" | "return" | "leave" => Tag::warning(),
        _ => Tag::secondary(),
    }
    .outline()
    .xsmall()
    .child(result.to_string())
}
