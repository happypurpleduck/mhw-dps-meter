//! Serde mirror of the plugin's `FightLog.cs` (schema 3). Schema-1 files lack
//! the newer fields, so everything added after 0.3.0 has a default.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum LogKind {
    #[default]
    Quest,
    Trial,
}

impl LogKind {
    pub fn label(self) -> &'static str {
        match self {
            LogKind::Quest => "quest",
            LogKind::Trial => "trial",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FightLog {
    #[serde(default = "one")]
    pub schema_version: u32,
    #[serde(default)]
    pub kind: LogKind,
    #[serde(default)]
    pub plugin_version: Option<String>,
    #[serde(default)]
    pub game_build: u32,
    #[serde(default)]
    pub damage_scope: Option<String>,
    #[serde(default)]
    pub quest_id: i64,
    pub quest_name: String,
    #[serde(default)]
    pub result: String,
    #[serde(default)]
    pub stage_id: i64,
    #[serde(default)]
    pub stage: Option<String>,
    #[serde(default)]
    pub started_at: String,
    #[serde(default)]
    pub ended_at: Option<String>,
    #[serde(default)]
    pub duration_seconds: f32,
    #[serde(default)]
    pub timer_source: Option<String>,
    #[serde(default)]
    pub hit_coverage: Option<String>,
    #[serde(default)]
    pub rewards: Option<Rewards>,
    pub players: Vec<Player>,
    #[serde(default)]
    pub monsters: Vec<Monster>,
    #[serde(default)]
    pub samples: Vec<Sample>,
    #[serde(default)]
    pub hits: Vec<Hit>,
    // Optional experimental observations must never reject an otherwise-valid hunt.
    #[serde(default, deserialize_with = "deserialize_gauge_samples")]
    pub gauge_samples: Vec<GaugeSample>,
    #[serde(default)]
    pub gauge_samples_dropped: usize,
    #[serde(default)]
    pub gauge_clock_discontinuities: usize,
    #[serde(default)]
    pub events: Vec<Event>,
}

/// Optional locally sampled MHW monster gauges; never assume the client is host.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GaugeSample {
    pub t: f32,
    #[serde(default)]
    pub raw_hunt_t: Option<f32>,
    #[serde(default)]
    pub clock_adjusted: bool,
    /// True on a monster's next stored sample after a clock discontinuity.
    /// None in pre-fix schema-3 logs where only clock_adjusted was emitted.
    #[serde(default)]
    pub clock_boundary: Option<bool>,
    pub monster: String,
    pub source: String,
    #[serde(default)]
    pub stamina: Option<f32>,
    #[serde(default)]
    pub max_stamina: Option<f32>,
    #[serde(default)]
    pub parts: Vec<PartGauge>,
    #[serde(default)]
    pub ailments: Vec<AilmentGauge>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PartGauge {
    pub part: i32,
    pub name: String,
    pub remaining: f32,
    pub max: f32,
    #[serde(default)]
    pub completed_flinches: Option<i32>,
    #[serde(default)]
    pub next_break_threshold: Option<i32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AilmentGauge {
    pub id: i32,
    pub name: String,
    #[serde(default)]
    pub buildup: Option<f32>,
    #[serde(default)]
    pub max_buildup: Option<f32>,
    #[serde(default)]
    pub procs: Option<i32>,
    /// Optional sampled duration; absent/invalid must not become a zero reading.
    #[serde(default)]
    pub duration: Option<f32>,
    #[serde(default)]
    pub max_duration: Option<f32>,
}


/// Accept only valid optional gauges, independently of core hunt-log parsing.
/// A recognizable monster with an invalid capture time becomes a non-plottable
/// gap marker (t=-1), so later readings cannot cross an observed discontinuity.
fn deserialize_gauge_samples<'de, D>(deserializer: D) -> Result<Vec<GaugeSample>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = serde_json::Value::deserialize(deserializer)?;
    Ok(value.as_array().map(|items| items.iter().filter_map(decode_gauge_sample).collect())
        .unwrap_or_default())
}

fn gauge_number(value: Option<&serde_json::Value>) -> Option<f32> {
    value?.as_f64().filter(|n| n.is_finite() && n.abs() <= f32::MAX as f64).map(|n| n as f32)
}

fn gauge_integer(value: Option<&serde_json::Value>) -> Option<i32> {
    let value = value?;
    // JS JSON.parse cannot distinguish 1, 1.0, and 1e0. Accept finite
    // mathematically integral float representations too, but preserve the
    // signed i32 bounds shared by the web viewer. Never round a fraction.
    if let Some(integer) = value.as_i64() {
        return i32::try_from(integer).ok();
    }
    let number = value.as_f64()?;
    if !number.is_finite() || number.fract() != 0.0
        || number < f64::from(i32::MIN) || number > f64::from(i32::MAX)
    {
        return None;
    }
    Some(number as i32)
}

fn decode_gauge_sample(value: &serde_json::Value) -> Option<GaugeSample> {
    let obj = value.as_object()?;
    let monster = obj.get("monster")?.as_str()?.to_string();
    if monster.is_empty() { return None; }
    let time = gauge_number(obj.get("t")).filter(|t| *t >= 0.0);
    let gap = time.is_none();

    let parts = if gap { Vec::new() } else {
        obj.get("parts").and_then(serde_json::Value::as_array).into_iter()
            .flatten().filter_map(|item| {
                let part = item.as_object()?;
                Some(PartGauge {
                    part: gauge_integer(part.get("part"))?,
                    name: part.get("name")?.as_str()?.to_string(),
                    remaining: gauge_number(part.get("remaining"))?,
                    max: gauge_number(part.get("max"))?,
                    completed_flinches: gauge_integer(part.get("completedFlinches")),
                    next_break_threshold: gauge_integer(part.get("nextBreakThreshold")),
                })
            }).collect()
    };
    let ailments = if gap { Vec::new() } else {
        obj.get("ailments").and_then(serde_json::Value::as_array).into_iter()
            .flatten().filter_map(|item| {
                let ailment = item.as_object()?;
                Some(AilmentGauge {
                    id: gauge_integer(ailment.get("id"))?,
                    name: ailment.get("name")?.as_str()?.to_string(),
                    buildup: gauge_number(ailment.get("buildup")),
                    max_buildup: gauge_number(ailment.get("maxBuildup")),
                    procs: gauge_integer(ailment.get("procs")),
                    duration: gauge_number(ailment.get("duration")),
                    max_duration: gauge_number(ailment.get("maxDuration")),
                })
            }).collect()
    };
    Some(GaugeSample {
        t: time.unwrap_or(-1.0),
        raw_hunt_t: gauge_number(obj.get("rawHuntT")),
        clock_adjusted: obj.get("clockAdjusted").and_then(serde_json::Value::as_bool).unwrap_or(false),
        clock_boundary: obj.get("clockBoundary").and_then(serde_json::Value::as_bool),
        monster,
        source: obj.get("source").and_then(serde_json::Value::as_str).unwrap_or("unknown").to_string(),
        stamina: if gap { None } else { gauge_number(obj.get("stamina")) },
        max_stamina: if gap { None } else { gauge_number(obj.get("maxStamina")) },
        parts,
        ailments,
    })
}

fn one() -> u32 {
    1
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Player {
    #[serde(default)]
    pub slot: usize,
    pub name: String,
    #[serde(default)]
    pub is_local: bool,
    #[serde(default)]
    pub weapon: Option<String>,
    #[serde(default)]
    pub damage: i64,
    #[serde(default)]
    pub dps: f32,
    #[serde(default)]
    pub percent: f32,
    /// Times this hunter carted (absent in older logs).
    #[serde(default)]
    pub carts: i32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Monster {
    pub id: String,
    #[serde(default)]
    pub r#type: i64,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub variant: i64,
    #[serde(default)]
    pub max_health: f32,
    #[serde(default)]
    pub last_health: f32,
    #[serde(default)]
    pub first_seen_t: f32,
    #[serde(default)]
    pub died_t: Option<f32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Sample {
    pub t: f32,
    /// Cumulative party damage per slot (length 4).
    #[serde(default)]
    pub damage: Vec<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Hit {
    pub t: f32,
    #[serde(default)]
    pub slot: usize,
    #[serde(default)]
    pub monster: Option<String>,
    pub damage: i64,
    #[serde(default)]
    pub crit: bool,
    #[serde(default)]
    pub tenderized: bool,
    #[serde(default)]
    pub attack_id: i64,
    #[serde(default)]
    pub action_set: i64,
    #[serde(default)]
    pub action_id: i64,
    /// Internal action name, e.g. `WP_02::RANBU`.
    #[serde(default)]
    pub action: Option<String>,
    /// Monster part index for local exact hits when resolvable.
    #[serde(default)]
    pub part: Option<i32>,
    /// Display name for `part` when known.
    #[serde(default)]
    pub part_name: Option<String>,
    /// Teammate row: award-table delta credited to the current move, not an exact hit.
    #[serde(default)]
    pub estimated: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Rewards {
    #[serde(default)]
    pub zenny: i64,
    #[serde(default)]
    pub hunter_rank_points: i64,
    #[serde(default)]
    pub stars: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Event {
    pub t: f32,
    pub r#type: String,
    #[serde(default)]
    pub slot: Option<usize>,
    #[serde(default)]
    pub monster: Option<String>,
    #[serde(default)]
    pub detail: Option<String>,
}

/// One row of `logs/index.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IndexEntry {
    pub file: String,
    #[serde(default)]
    pub kind: LogKind,
    #[serde(default)]
    pub quest_id: i64,
    #[serde(default)]
    pub quest_name: String,
    #[serde(default)]
    pub result: String,
    #[serde(default)]
    pub started_at: String,
    #[serde(default)]
    pub duration_seconds: f32,
    #[serde(default)]
    pub total_damage: i64,
    #[serde(default)]
    pub damage_scope: Option<String>,
    #[serde(default)]
    pub weapon: Option<String>,
    #[serde(default)]
    pub players: Vec<String>,
    #[serde(default)]
    pub monsters: Vec<String>,
}

impl IndexEntry {
    /// Same derivation as `FightLogIndexEntry.From` in the plugin.
    pub fn from_log(log: &FightLog, file: impl Into<String>) -> Self {
        let mut monsters: Vec<String> = log.monsters.iter().map(|m| m.name.clone()).collect();
        monsters.sort();
        monsters.dedup();
        Self {
            file: file.into(),
            kind: log.kind,
            quest_id: log.quest_id,
            quest_name: log.quest_name.clone(),
            result: log.result.clone(),
            started_at: log.started_at.clone(),
            duration_seconds: log.duration_seconds,
            total_damage: log.players.iter().map(|p| p.damage).sum(),
            damage_scope: log.damage_scope.clone(),
            weapon: log.players.iter().find(|p| p.is_local).and_then(|p| p.weapon.clone()),
            players: log.players.iter().map(|p| p.name.clone()).collect(),
            monsters,
        }
    }
}

impl FightLog {
    pub fn parse(text: &str) -> anyhow::Result<Self> {
        let mut log: FightLog = serde_json::from_str(text)?;
        // Schema 1 never marked the local hunter; a solo log is necessarily you.
        if log.players.len() == 1 && !log.players[0].is_local {
            log.players[0].is_local = true;
        }
        Ok(log)
    }

    pub fn local_player(&self) -> Option<&Player> {
        self.players.iter().find(|p| p.is_local).or_else(|| self.players.first())
    }

    pub fn total_damage(&self) -> i64 {
        self.players.iter().map(|p| p.damage).sum()
    }

    pub fn is_local_only(&self) -> bool {
        self.damage_scope.as_deref() == Some("local-only")
    }

    pub fn has_observed_local_damage(&self) -> bool {
        !self.is_local_only() || self.players.iter().any(|p| p.is_local)
    }

    /// Hits of one hunter (teammates only have estimated rows).
    pub fn hits_for_slot(&self, slot: usize) -> Vec<&Hit> {
        self.hits.iter().filter(|h| h.slot == slot).collect()
    }

    /// Slots that have hit rows, local hunter first.
    pub fn slots_with_hits(&self) -> Vec<usize> {
        let local = self.local_player().map(|p| p.slot);
        let mut slots: Vec<usize> = self.hits.iter().map(|h| h.slot).collect();
        slots.sort();
        slots.dedup();
        slots.sort_by_key(|s| if Some(*s) == local { 0 } else { 1 + *s });
        slots
    }
}

pub fn parse_index(text: &str) -> anyhow::Result<Vec<IndexEntry>> {
    Ok(serde_json::from_str(text)?)
}

/// Per-hunt log files only: excludes index.json and the live-debug files.
pub fn is_log_file_name(name: &str) -> bool {
    name.ends_with(".json") && name != "index.json" && !name.starts_with("live-debug")
}

#[cfg(test)]
mod guiding_lands_scope_tests {
    use super::*;

    #[test]
    fn shared_guiding_lands_synthetic_fixtures_preserve_coverage_boundaries() {
        let solo = FightLog::parse(include_str!("../../tests/fixtures/guiding-lands-solo.json")).unwrap();
        assert!(solo.is_local_only());
        assert!(solo.has_observed_local_damage());
        assert_eq!(solo.quest_id, 0);
        assert_eq!(solo.stage_id, 109);
        assert_eq!(solo.players.len(), 1);
        assert_eq!(solo.players[0].damage, 360);
        assert_eq!(solo.players[0].percent, 100.0); // never an actual multiplayer party share
        assert_eq!(solo.hits.len(), 2);
        assert!(solo.gauge_samples.iter().all(|g| g.source == "local-unverified"));
        assert_eq!(IndexEntry::from_log(&solo, "solo.json").damage_scope.as_deref(), Some("local-only"));

        let multi = FightLog::parse(include_str!("../../tests/fixtures/guiding-lands-multiplayer-gauge-only.json")).unwrap();
        assert!(multi.is_local_only());
        assert!(!multi.has_observed_local_damage());
        assert_eq!(multi.quest_id, 0);
        assert!(multi.players.is_empty());
        assert!(multi.hits.is_empty());
        assert_eq!(multi.monsters.len(), 2);
        assert_eq!(multi.gauge_samples.len(), 2);
        assert_eq!(multi.gauge_samples[0].monster, "m1");
        assert_eq!(multi.gauge_samples[1].monster, "m2");
        assert!(multi.gauge_samples.iter().all(|g| g.source == "local-unverified"));
        assert_eq!(IndexEntry::from_log(&multi, "gauge-only.json").total_damage, 0);
    }

    #[test]
    fn parses_guiding_lands_local_only_and_old_quest_defaults() {
        let gl = FightLog::parse(r#"{"questName":"The Guiding Lands","questId":0,"stageId":109,"damageScope":"local-only","players":[],"gaugeSamples":[{"t":2,"monster":"m1","source":"local-unverified"}]}"#).unwrap();
        assert!(gl.is_local_only());
        assert_eq!(gl.gauge_samples.len(), 1);
        let entry = IndexEntry::from_log(&gl, "guiding-lands.json");
        assert_eq!(entry.damage_scope.as_deref(), Some("local-only"));
        let old = FightLog::parse(r#"{"questName":"Normal hunt","players":[]}"#).unwrap();
        assert!(!old.is_local_only());
        assert!(old.has_observed_local_damage());
        assert!(old.damage_scope.is_none());
        assert!(IndexEntry::from_log(&old, "old.json").damage_scope.is_none());
    }
}
