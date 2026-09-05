//! Serde mirror of the plugin's `FightLog.cs` (schema 2). Schema-1 files lack
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
    pub players: Vec<Player>,
    #[serde(default)]
    pub monsters: Vec<Monster>,
    #[serde(default)]
    pub samples: Vec<Sample>,
    #[serde(default)]
    pub hits: Vec<Hit>,
    #[serde(default)]
    pub events: Vec<Event>,
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
}

pub fn parse_index(text: &str) -> anyhow::Result<Vec<IndexEntry>> {
    Ok(serde_json::from_str(text)?)
}

/// Per-hunt log files only: excludes index.json and the live-debug files.
pub fn is_log_file_name(name: &str) -> bool {
    name.ends_with(".json") && name != "index.json" && !name.starts_with("live-debug")
}
