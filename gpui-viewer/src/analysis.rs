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

/// Unsmooth rate: damage gained since the previous sample divided by elapsed time.
/// A missing slot is zero; duplicate/out-of-order timestamps have no rate.
pub fn interval_dps(log: &FightLog, slot: usize) -> Vec<(f32, f32)> {
    let mut previous = (0.0, 0);
    log.samples.iter().map(|sample| {
        let damage = sample.damage.get(slot).copied().unwrap_or(0);
        let dt = sample.t - previous.0;
        let rate = if dt > 0.0 { (damage - previous.1).max(0) as f32 / dt } else { 0.0 };
        if sample.t >= previous.0 { previous = (sample.t, damage); }
        (sample.t, rate)
    }).collect()
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

#[derive(Debug, Clone, PartialEq)]
pub struct PartMetrics {
    pub dps: f32,
    pub avg: f32,
    pub max: i64,
    pub crit_rate: Option<f32>,
    pub tenderized_rate: Option<f32>,
    pub estimated: bool,
}

fn part_metrics(hits: &[&Hit], duration: f32) -> PartMetrics {
    let stats = hit_stats(hits.iter().copied());
    let estimated = hits.iter().any(|h| h.estimated);
    PartMetrics {
        dps: if duration > 0.0 { stats.damage as f32 / duration } else { 0.0 },
        avg: stats.avg(), max: stats.max, estimated,
        crit_rate: (!estimated).then(|| stats.crit_rate()),
        tenderized_rate: (!estimated).then(|| if stats.hits > 0 { stats.tenderized as f32 / stats.hits as f32 } else { 0.0 }),
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct PartHunterRow {
    pub slot: usize,
    pub name: String,
    pub damage: i64,
    pub hits: usize,
    pub share: f32,
    pub estimated: bool,
    pub metrics: PartMetrics,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PartRow {
    pub part: Option<i32>,
    pub name: String,
    pub damage: i64,
    pub hits: usize,
    pub share: f32,
    pub hunters: Vec<PartHunterRow>,
    pub metrics: PartMetrics,
}

pub fn has_part_data(log: &FightLog) -> bool {
    log.hits.iter().any(|h| hit_part(h).is_some())
}

/// Parts need a monster identity; award deltas cannot establish which part was hit.
pub fn hit_part(hit: &Hit) -> Option<i32> {
    if hit.estimated || hit.monster.is_none() { None } else { hit.part }
}

/// None selects unassigned damage, not a union of unrelated monster parts.
pub fn part_hits<'a>(log: &'a FightLog, monster_id: Option<&str>, part: Option<i32>) -> Vec<&'a Hit> {
    log.hits.iter().filter(|h| hit_part(h) == part && h.monster.as_deref() == monster_id).collect()
}

/// Damage and all hunter contributions, scoped to the selected monster.
pub fn part_breakdown(log: &FightLog, monster_id: Option<&str>) -> Vec<PartRow> {
    let mut groups: BTreeMap<Option<i32>, Vec<&Hit>> = BTreeMap::new();
    let mut total = 0;
    for hit in log.hits.iter().filter(|h| h.monster.as_deref() == monster_id) {
        total += hit.damage;
        groups.entry(hit_part(hit)).or_default().push(hit);
    }
    let mut rows: Vec<_> = groups.into_iter().map(|(part, hits)| {
        let damage: i64 = hits.iter().map(|h| h.damage).sum();
        let mut by_slot: BTreeMap<usize, Vec<&Hit>> = BTreeMap::new();
        for hit in &hits { by_slot.entry(hit.slot).or_default().push(hit); }
        let mut hunters: Vec<_> = by_slot.into_iter().map(|(slot, own)| {
            let own_damage: i64 = own.iter().map(|h| h.damage).sum();
            PartHunterRow {
                slot,
                name: log.players.iter().find(|p| p.slot == slot).map(|p| p.name.clone()).unwrap_or_else(|| format!("Slot {}", slot + 1)),
                damage: own_damage, hits: own.len(),
                share: if damage > 0 { own_damage as f32 / damage as f32 } else { 0.0 },
                estimated: own.iter().any(|h| h.estimated),
                metrics: part_metrics(&own, log.duration_seconds),
            }
        }).collect();
        hunters.sort_by(|a, b| b.damage.cmp(&a.damage));
        PartRow {
            part,
            name: match part {
                None => "Unknown part".into(),
                Some(id) => hits.iter().find_map(|h| h.part_name.clone()).unwrap_or_else(|| format!("Part {id}")),
            },
            damage, hits: hits.len(),
            share: if total > 0 { damage as f32 / total as f32 } else { 0.0 },
            hunters, metrics: part_metrics(&hits, log.duration_seconds),
        }
    }).collect();
    rows.sort_by(|a, b| b.damage.cmp(&a.damage));
    rows
}

#[derive(Debug, Clone, PartialEq)]
pub struct HitTimeline {
    pub slot: usize,
    pub damage: Vec<(f32, f32)>,
    pub dps: Vec<(f32, f32)>,
    pub rolling: Vec<(f32, f32)>,
}

/// Part curves use hit timestamps, never whole-party award samples. Include idle
/// bins, the final partial interval, and the remainder of the hunt after last hit.
pub fn hit_timeline(log: &FightLog, hits: &[&Hit], window: f32) -> Vec<HitTimeline> {
    let mut sorted: Vec<_> = hits.iter().copied().filter(|h| h.t.is_finite() && h.t >= 0.0 && h.damage > 0).collect();
    sorted.sort_by(|a, b| a.t.total_cmp(&b.t));
    let end = log.duration_seconds.max(0.0).max(sorted.last().map(|h| h.t).unwrap_or(0.0));
    let end = if end.is_finite() { end } else { sorted.last().map(|h| h.t).unwrap_or(0.0) };
    let window = if window.is_finite() && window > 0.0 { window } else { 20.0 };
    let mut times = vec![0.0];
    let mut t = 2.0;
    while t < end { times.push(t); t += 2.0; }
    if end > 0.0 { times.push(end); }
    let mut groups: BTreeMap<usize, Vec<&Hit>> = BTreeMap::new();
    for hit in sorted { groups.entry(hit.slot).or_default().push(hit); }
    groups.into_iter().map(|(slot, own)| {
        let mut series = HitTimeline { slot, damage: Vec::new(), dps: Vec::new(), rolling: Vec::new() };
        let (mut right, mut left, mut cumulative, mut rolling, mut previous, mut previous_time) = (0, 0, 0i64, 0i64, 0i64, 0.0);
        for &t in &times {
            if t > 0.0 || end == 0.0 {
                while right < own.len() && own[right].t <= t {
                    cumulative += own[right].damage;
                    rolling += own[right].damage;
                    right += 1;
                }
            }
            let cutoff = t - window;
            while left < right && cutoff > 0.0 && own[left].t <= cutoff {
                rolling -= own[left].damage;
                left += 1;
            }
            let dt = t - previous_time;
            series.damage.push((t, cumulative as f32));
            series.dps.push((t, if dt > 0.0 { (cumulative - previous) as f32 / dt } else { 0.0 }));
            series.rolling.push((t, if t > 0.0 { rolling as f32 / t.min(window) } else { 0.0 }));
            previous = cumulative;
            previous_time = t;
        }
        series
    }).collect()
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
    fn interval_rates_preserve_bursts_and_idle_intervals() {
        let mut log = newest_v2();
        log.samples = [(0., 0), (2., 100), (5., 100), (6., 400), (6., 400), (8., 500)]
            .into_iter().map(|(t, d)| crate::model::Sample { t, damage: vec![d] }).collect();
        assert_eq!(interval_dps(&log, 0), vec![(0., 0.), (2., 50.), (5., 0.), (6., 300.), (6., 0.), (8., 50.)]);
        assert!(interval_dps(&log, 3).iter().all(|(_, rate)| *rate == 0.));
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
    fn parts_rank_hunters_and_unknown() {
        let mut log = newest_v2();
        let monster = log.monsters[0].id.clone();
        log.players = vec![
            crate::model::Player {
                slot: 0,
                name: "Local".into(),
                is_local: true,
                weapon: Some("DualBlades".into()),
                damage: 190,
                dps: 0.0,
                percent: 0.0,
                carts: 0,
            },
            crate::model::Player {
                slot: 1,
                name: "Teammate".into(),
                is_local: false,
                weapon: None,
                damage: 80,
                dps: 0.0,
                percent: 0.0,
                carts: 0,
            },
        ];
        log.hits = vec![
            crate::model::Hit {
                t: 1.0,
                slot: 0,
                monster: Some(monster.clone()),
                damage: 100,
                crit: false,
                tenderized: false,
                attack_id: 1,
                action_set: 1,
                action_id: 1,
                action: None,
                part: Some(2),
                part_name: Some("Head".into()),
                estimated: false,
            },
            crate::model::Hit {
                t: 2.0,
                slot: 0,
                monster: Some(monster.clone()),
                damage: 50,
                crit: false,
                tenderized: false,
                attack_id: 1,
                action_set: 1,
                action_id: 1,
                action: None,
                part: Some(2),
                part_name: Some("Head".into()),
                estimated: false,
            },
            crate::model::Hit {
                t: 3.0,
                slot: 1,
                monster: Some(monster.clone()),
                damage: 80,
                crit: false,
                tenderized: false,
                attack_id: -1,
                action_set: 1,
                action_id: 1,
                action: None,
                part: None,
                part_name: None,
                estimated: true,
            },
        ];
        let rows = part_breakdown(&log, Some(&monster));
        assert_eq!(rows[0].name, "Head");
        assert_eq!(rows[0].damage, 150);
        assert_eq!(rows[0].hunters[0].name, "Local");
        let unknown = rows.iter().find(|r| r.part.is_none()).unwrap();
        assert_eq!(unknown.damage, 80);
        assert!(has_part_data(&log));
    }

    fn part_fixture() -> FightLog {
        FightLog::parse(include_str!("../../tests/fixtures/part-detail.json")).unwrap()
    }

    #[test]
    fn part_detail_metrics_and_unknown_estimates() {
        let log = part_fixture();
        let rows = part_breakdown(&log, Some("m1"));
        let head = rows.iter().find(|r| r.part == Some(0)).unwrap();
        assert_eq!((head.damage, head.hits), (180, 5));
        assert_eq!((head.metrics.dps, head.metrics.avg, head.metrics.max), (20.0, 36.0, 80));
        assert_eq!((head.metrics.crit_rate, head.metrics.tenderized_rate), (Some(0.4), Some(0.4)));
        assert_eq!(head.hunters.iter().map(|h| (h.slot, h.damage, h.hits)).collect::<Vec<_>>(), vec![(0, 160, 4), (1, 20, 1)]);
        assert!((head.hunters[0].metrics.dps - 160.0 / 9.0).abs() < 0.001);
        let unknown = rows.iter().find(|r| r.part.is_none()).unwrap();
        assert_eq!(unknown.damage, 315);
        assert_eq!((unknown.metrics.crit_rate, unknown.metrics.tenderized_rate), (None, None));
        assert!(part_hits(&log, Some("m1"), Some(0)).iter().all(|h| !h.estimated));
        assert_eq!(part_breakdown(&log, Some("m2"))[0].damage, 999);
        assert!(part_breakdown(&log, Some("m3")).is_empty());
    }

    #[test]
    fn unassigned_teammate_damage_stays_visible_without_double_counting() {
        let mut log = part_fixture();
        let unassigned = part_breakdown(&log, None);
        assert_eq!(unassigned.len(), 1);
        assert_eq!((unassigned[0].part, unassigned[0].damage, unassigned[0].hits), (None, 200, 2));
        assert!(unassigned[0].metrics.estimated);
        assert_eq!(unassigned[0].metrics.crit_rate, None);
        assert_eq!(unassigned[0].hunters.iter().map(|h| (h.slot, h.damage)).collect::<Vec<_>>(), vec![(2, 130), (1, 70)]);
        let curves = hit_timeline(&log, &part_hits(&log, None, None), 20.0);
        assert_eq!((curves[0].slot, curves[0].damage.last().unwrap().1), (1, 70.0));
        assert_eq!((curves[1].slot, curves[1].damage.last().unwrap().1), (2, 130.0));
        let monster_damage: i64 = log.monsters.iter().flat_map(|m| part_breakdown(&log, Some(&m.id))).map(|r| r.damage).sum();
        assert_eq!(monster_damage + unassigned[0].damage, log.hits.iter().map(|h| h.damage).sum::<i64>());
        assert!(part_hits(&log, None, Some(0)).is_empty());

        log.hits.retain(|h| h.monster.is_none());
        log.monsters.clear();
        assert_eq!(part_breakdown(&log, None)[0].damage, 200);
        // A part id without a monster cannot identify a body part, even in an exact row.
        log.hits[0].estimated = false;
        log.hits[0].part = Some(0);
        log.hits[0].part_name = Some("Head".into());
        assert_eq!(part_breakdown(&log, None).iter().map(|r| r.part).collect::<Vec<_>>(), vec![None]);
        assert!(!has_part_data(&log));
    }

    #[test]
    fn part_curves_rebuild_hits_with_idle_and_partial_intervals() {
        let log = part_fixture();
        let timeline = hit_timeline(&log, &part_hits(&log, Some("m1"), Some(0)), 4.0);
        assert_eq!(timeline[0].damage, vec![(0.0, 0.0), (2.0, 40.0), (4.0, 40.0), (6.0, 120.0), (8.0, 120.0), (9.0, 160.0)]);
        assert_eq!(timeline[0].dps.iter().map(|p| p.1).collect::<Vec<_>>(), vec![0.0, 20.0, 0.0, 40.0, 0.0, 40.0]);
        assert_eq!(timeline[0].rolling.iter().map(|p| p.1).collect::<Vec<_>>(), vec![0.0, 20.0, 10.0, 20.0, 20.0, 10.0]);
        assert_eq!(timeline[1].damage.last(), Some(&(9.0, 20.0)));
        let tail = hit_timeline(&log, &part_hits(&log, Some("m1"), Some(1)), 4.0);
        assert_eq!(tail[0].damage.last(), Some(&(9.0, 100.0)));
        assert_eq!(tail[0].rolling.last(), Some(&(9.0, 0.0)));
    }

    #[test]
    fn part_charts_accept_old_empty_and_zero_duration_logs() {
        let mut log = part_fixture();
        for hit in &mut log.hits { hit.part = None; hit.part_name = None; }
        assert_eq!(part_breakdown(&log, Some("m1")).iter().map(|r| r.part).collect::<Vec<_>>(), vec![None]);
        assert!(hit_timeline(&log, &[], 20.0).is_empty());
        log.duration_seconds = 0.0;
        log.hits.truncate(1);
        log.hits[0].t = 0.0;
        log.hits[0].damage = 10;
        let hits: Vec<_> = log.hits.iter().collect();
        let curves = hit_timeline(&log, &hits, 20.0);
        assert_eq!(curves[0].damage, vec![(0.0, 10.0)]);
        assert_eq!(curves[0].dps, vec![(0.0, 0.0)]);
        assert_eq!(part_breakdown(&log, Some("m1"))[0].metrics.dps, 0.0);
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
