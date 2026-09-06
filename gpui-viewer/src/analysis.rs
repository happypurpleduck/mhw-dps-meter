//! Pure computations over a [`FightLog`]; no UI types so they unit-test on native.

use std::collections::BTreeMap;

use crate::model::{FightLog, Hit, IndexEntry, LogKind, Monster};

pub const PARTY_SLOTS: usize = 4;

#[derive(Debug, Clone, PartialEq)]
pub struct CurvePoint {
    pub t: f32,
    pub damage: i64,
}

/// Cumulative damage per slot from the 2 s samples; empty for slots with no data.
pub fn damage_curves(log: &FightLog) -> Vec<(usize, Vec<CurvePoint>)> {
    let mut out = Vec::new();
    for slot in active_slots(log) {
        let points = log
            .samples
            .iter()
            .map(|s| CurvePoint { t: s.t, damage: s.damage.get(slot).copied().unwrap_or(0) })
            .collect();
        out.push((slot, points));
    }
    out
}

pub fn active_slots(log: &FightLog) -> Vec<usize> {
    let mut slots: Vec<usize> = log.players.iter().map(|p| p.slot).collect();
    for s in &log.samples {
        for (i, d) in s.damage.iter().enumerate() {
            if *d > 0 {
                slots.push(i);
            }
        }
    }
    slots.retain(|s| *s < PARTY_SLOTS);
    slots.sort();
    slots.dedup();
    slots
}

/// Rolling DPS over `window` seconds for one slot, one point per sample.
pub fn rolling_dps(log: &FightLog, slot: usize, window: f32) -> Vec<(f32, f32)> {
    let samples = &log.samples;
    let mut out = Vec::with_capacity(samples.len());
    for i in 0..samples.len() {
        let cur = &samples[i];
        let mut j = i;
        while j > 0 && cur.t - samples[j - 1].t < window {
            j -= 1;
        }
        let prev = &samples[j];
        let dt = cur.t - prev.t;
        let dps = if dt > 0.0 {
            (cur.damage.get(slot).copied().unwrap_or(0) - prev.damage.get(slot).copied().unwrap_or(0)) as f32 / dt
        } else {
            0.0
        };
        out.push((cur.t, dps.max(0.0)));
    }
    out
}

#[derive(Debug, Clone, PartialEq)]
pub struct MoveRow {
    /// Internal action name or `action <set>/<id>`.
    pub key: String,
    pub name: String,
    pub damage: i64,
    pub hits: usize,
    pub crits: usize,
    pub tenderized: usize,
    pub max: i64,
    pub share: f32,
    pub is_common: bool,
}

impl MoveRow {
    pub fn crit_rate(&self) -> f32 {
        if self.hits == 0 { 0.0 } else { self.crits as f32 / self.hits as f32 }
    }
    pub fn avg(&self) -> f32 {
        if self.hits == 0 { 0.0 } else { self.damage as f32 / self.hits as f32 }
    }
}

pub fn is_common_action(action: Option<&str>) -> bool {
    action.is_some_and(|a| a.starts_with("Common::"))
}

/// Per-move breakdown of one hunter's hits, biggest damage first.
pub fn move_breakdown<'a>(hits: impl IntoIterator<Item = &'a Hit> + Clone, display_name: impl Fn(&str) -> String) -> Vec<MoveRow> {
    let mut groups: BTreeMap<String, MoveRow> = BTreeMap::new();
    let total: i64 = hits.clone().into_iter().map(|h| h.damage).sum();
    for hit in hits {
        let key = hit
            .action
            .clone()
            .unwrap_or_else(|| format!("action {}/{}", hit.action_set, hit.action_id));
        let row = groups.entry(key.clone()).or_insert_with(|| MoveRow {
            name: display_name(&key),
            key,
            damage: 0,
            hits: 0,
            crits: 0,
            tenderized: 0,
            max: 0,
            share: 0.0,
            is_common: is_common_action(hit.action.as_deref()),
        });
        row.damage += hit.damage;
        row.hits += 1;
        row.crits += hit.crit as usize;
        row.tenderized += hit.tenderized as usize;
        row.max = row.max.max(hit.damage);
    }
    let mut rows: Vec<MoveRow> = groups.into_values().collect();
    for row in &mut rows {
        row.share = if total > 0 { row.damage as f32 / total as f32 } else { 0.0 };
    }
    rows.sort_by(|a, b| b.damage.cmp(&a.damage));
    rows
}

#[derive(Debug, Clone)]
pub struct MonsterRow {
    pub monster: Monster,
    pub hp_lost: f32,
    pub your_damage: i64,
    pub your_hits: usize,
}

pub fn monster_breakdown(log: &FightLog) -> Vec<MonsterRow> {
    let local = log.local_player().map(|p| p.slot);
    log.monsters
        .iter()
        .map(|m| {
            let mine = log
                .hits
                .iter()
                .filter(|h| h.monster.as_deref() == Some(m.id.as_str()) && Some(h.slot) == local && !h.estimated);
            let (dmg, n) = mine.fold((0i64, 0usize), |(d, n), h| (d + h.damage, n + 1));
            MonsterRow {
                monster: m.clone(),
                hp_lost: (m.max_health - m.last_health).max(0.0),
                your_damage: dmg,
                your_hits: n,
            }
        })
        .collect()
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct HitStats {
    pub hits: usize,
    pub crits: usize,
    pub tenderized: usize,
    pub damage: i64,
    pub max: i64,
    pub active_seconds: f32,
}

impl HitStats {
    pub fn crit_rate(&self) -> f32 {
        if self.hits == 0 { 0.0 } else { self.crits as f32 / self.hits as f32 }
    }
    pub fn avg(&self) -> f32 {
        if self.hits == 0 { 0.0 } else { self.damage as f32 / self.hits as f32 }
    }
    pub fn active_dps(&self) -> f32 {
        if self.active_seconds > 0.0 { self.damage as f32 / self.active_seconds } else { 0.0 }
    }
}

pub fn hit_stats<'a>(hits: impl IntoIterator<Item = &'a Hit>) -> HitStats {
    let hits: Vec<&Hit> = hits.into_iter().collect();
    let first = hits.first().map(|h| h.t).unwrap_or(0.0);
    let last = hits.last().map(|h| h.t).unwrap_or(0.0);
    HitStats {
        hits: hits.len(),
        crits: hits.iter().filter(|h| h.crit).count(),
        tenderized: hits.iter().filter(|h| h.tenderized).count(),
        damage: hits.iter().map(|h| h.damage).sum(),
        max: hits.iter().map(|h| h.damage).max().unwrap_or(0),
        active_seconds: (last - first).max(0.0),
    }
}

/// Exact (non-estimated) hits of the local hunter.
pub fn local_exact_hits(log: &FightLog) -> Vec<&Hit> {
    let local = log.local_player().map(|p| p.slot);
    log.hits.iter().filter(|h| Some(h.slot) == local && !h.estimated).collect()
}

#[derive(Debug, Clone)]
pub struct TrialBest {
    pub weapon: String,
    pub duration_seconds: u32,
    pub best: IndexEntry,
    pub attempts: usize,
}

/// Personal bests per weapon and window, the rule the plugin's F9 panel uses.
pub fn trial_bests(entries: &[IndexEntry]) -> Vec<TrialBest> {
    let mut groups: BTreeMap<(String, u32), TrialBest> = BTreeMap::new();
    for e in entries.iter().filter(|e| e.kind == LogKind::Trial) {
        let weapon = e.weapon.clone().unwrap_or_else(|| "?".into());
        let dur = e.duration_seconds.round() as u32;
        groups
            .entry((weapon.clone(), dur))
            .and_modify(|g| {
                g.attempts += 1;
                if e.total_damage > g.best.total_damage {
                    g.best = e.clone();
                }
            })
            .or_insert_with(|| TrialBest { weapon, duration_seconds: dur, best: e.clone(), attempts: 1 });
    }
    groups.into_values().collect()
}

/// Cumulative local damage rebuilt from hits at `step` second resolution.
pub fn cumulative_from_hits(hits: &[Hit], step: f32) -> Vec<CurvePoint> {
    let mut out = vec![CurvePoint { t: 0.0, damage: 0 }];
    let end = hits.last().map(|h| h.t.ceil()).unwrap_or(0.0);
    let (mut cum, mut i, mut t) = (0i64, 0usize, step);
    while t <= end + 0.001 {
        while i < hits.len() && hits[i].t <= t {
            cum += hits[i].damage;
            i += 1;
        }
        out.push(CurvePoint { t, damage: cum });
        t += step;
    }
    out
}

pub fn format_duration(seconds: f32) -> String {
    let s = seconds.round().max(0.0) as u64;
    if s >= 60 { format!("{}:{:02}", s / 60, s % 60) } else { format!("{s}s") }
}

pub fn format_int(n: i64) -> String {
    let digits = n.abs().to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (i, ch) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(ch);
    }
    if n < 0 { format!("-{out}") } else { out }
}

/// "2026-09-06T00:09:27.12+00:00" -> "2026-09-06 00:09" (no timezone shift; keeps the dependency list small).
pub fn format_date(iso: &str) -> String {
    let date = iso.get(0..10).unwrap_or(iso);
    let time = iso.get(11..16).unwrap_or("");
    if time.is_empty() { date.to_string() } else { format!("{date} {time}") }
}

pub fn pct(x: f32, digits: usize) -> String {
    format!("{:.*}%", digits, x * 100.0)
}

/// Display names for internal action names, keyed by weapon type.
pub fn move_display_name(weapon: Option<&str>, key: &str) -> String {
    const DUAL_BLADES: &[(&str, &str)] = &[
        ("WP_02::RANBU", "Blade Dance"),
        ("WP_02::KIJIN_RUSH", "Demon Flurry Rush"),
        ("WP_02::TWICE_SLASH", "Double Slash"),
        ("WP_02::KIJIN_CHAIN", "Demon Mode chain"),
        ("WP_02::EM_CONST_SPIN", "Clutch Claw spin"),
        ("WP_02::EM_CONST_SPIN_FINISH", "Clutch Claw spin finisher"),
        ("WP_02::CLAW_EM_STICK_ATTACK", "Clutch Claw weapon attack"),
        ("WP_02::CLAW_EM_STICK_ATTACK_LAND", "Clutch Claw weapon attack (landing)"),
        ("WP_02::AIR_SPIN", "Aerial spin"),
    ];
    const COMMON: &[(&str, &str)] = &[
        ("Common::CLAW_EM_STICK_EM_CTRL_DIR_ADJUST_PUSH", "Clutch Claw slinger burst"),
        ("Common::CLAW_EM_STICK_START_L", "Clutch Claw grab"),
    ];
    let table: &[(&str, &str)] = match weapon {
        Some("DualBlades") => DUAL_BLADES,
        _ => &[],
    };
    table
        .iter()
        .chain(COMMON.iter())
        .find(|(k, _)| *k == key)
        .map(|(_, v)| (*v).to_string())
        .unwrap_or_else(|| prettify_action(key))
}

/// `WP_02::KIJIN_SLIDING_ON` -> `Kijin Sliding On`.
pub fn prettify_action(key: &str) -> String {
    let tail = key.rsplit("::").next().unwrap_or(key);
    tail.split('_')
        .filter(|w| !w.is_empty())
        .map(|w| {
            let lower = w.to_lowercase();
            let mut c = lower.chars();
            match c.next() {
                Some(f) => f.to_uppercase().collect::<String>() + c.as_str(),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(name: &str) -> FightLog {
        let path = format!("{}/sample-logs/{name}", env!("CARGO_MANIFEST_DIR"));
        FightLog::parse(&std::fs::read_to_string(path).unwrap()).unwrap()
    }

    fn newest_v2() -> FightLog {
        sample("2026-09-06_000927_66801_complete.json")
    }

    #[test]
    fn parses_schema_1_and_2() {
        let dir = format!("{}/sample-logs", env!("CARGO_MANIFEST_DIR"));
        let mut seen_v1 = false;
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            let name = path.file_name().unwrap().to_str().unwrap();
            if !is_log_file_name(name) {
                continue;
            }
            let log = FightLog::parse(&std::fs::read_to_string(&path).unwrap()).unwrap();
            assert!(!log.players.is_empty(), "{name}");
            if log.schema_version == 1 {
                seen_v1 = true;
                assert!(log.hits.is_empty());
                assert!(log.players[0].is_local);
            }
        }
        assert!(seen_v1, "sample set should include a schema-1 file");
    }

    #[test]
    fn index_matches_derivation() {
        let dir = format!("{}/sample-logs", env!("CARGO_MANIFEST_DIR"));
        let index = parse_index(&std::fs::read_to_string(format!("{dir}/index.json")).unwrap()).unwrap();
        for e in &index {
            let log = sample(&e.file);
            let derived = IndexEntry::from_log(&log, &e.file);
            assert_eq!(derived.total_damage, e.total_damage, "{}", e.file);
            assert_eq!(derived.kind, e.kind);
        }
    }

    #[test]
    fn hits_match_award_damage() {
        let log = newest_v2();
        let me = log.local_player().unwrap();
        let stats = hit_stats(&log.hits);
        let diff = (stats.damage - me.damage).abs() as f32 / me.damage as f32;
        assert!(diff < 0.02, "hooked hits {} vs award {}", stats.damage, me.damage);
        assert!(stats.crit_rate() > 0.0 && stats.crit_rate() < 1.0);
    }

    #[test]
    fn moves_group_by_action() {
        let log = newest_v2();
        let rows = move_breakdown(&log.hits, |k| move_display_name(Some("DualBlades"), k));
        assert!(rows.len() > 5);
        assert!(rows[0].damage >= rows[1].damage);
        let share: f32 = rows.iter().map(|r| r.share).sum();
        assert!((share - 1.0).abs() < 1e-4);
        assert_eq!(rows.iter().map(|r| r.hits).sum::<usize>(), log.hits.len());
        assert_eq!(rows.iter().find(|r| r.key == "WP_02::RANBU").unwrap().name, "Blade Dance");
    }

    #[test]
    fn curves_are_monotonic() {
        let log = newest_v2();
        for (_, points) in damage_curves(&log) {
            assert_eq!(points.len(), log.samples.len());
            assert!(points.windows(2).all(|w| w[1].damage >= w[0].damage));
        }
        assert!(rolling_dps(&log, 0, 20.0).iter().all(|(_, d)| *d >= 0.0));
    }

    #[test]
    fn trial_bests_and_cumulative() {
        let dir = format!("{}/sample-logs", env!("CARGO_MANIFEST_DIR"));
        let index = parse_index(&std::fs::read_to_string(format!("{dir}/index.json")).unwrap()).unwrap();
        let bests = trial_bests(&index);
        assert_eq!(bests.len(), 1);
        assert_eq!(bests[0].weapon, "DualBlades");
        let trial = sample(&bests[0].best.file);
        let curve = cumulative_from_hits(&trial.hits, 1.0);
        assert_eq!(curve.last().unwrap().damage, trial.players[0].damage);
    }

    #[test]
    fn formatting() {
        assert_eq!(format_int(1234567), "1,234,567");
        assert_eq!(format_int(999), "999");
        assert_eq!(format_duration(75.0), "1:15");
        assert_eq!(format_duration(9.6), "10s");
        assert_eq!(prettify_action("WP_02::KIJIN_SLIDING_ON"), "Kijin Sliding On");
        assert_eq!(format_date("2026-09-06T00:09:27.12+00:00"), "2026-09-06 00:09");
    }

    use crate::model::{is_log_file_name, parse_index};
}
