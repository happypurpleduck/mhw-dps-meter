//! Loading a logs folder: from disk (native) or from a served URL (native and browser).

use std::{collections::HashMap, sync::Arc};

use anyhow::{Context as _, anyhow};

use crate::model::{FightLog, IndexEntry, is_log_file_name, parse_index};

/// Everything the viewer needs for one logs folder, fully parsed. Log files are a few
/// hundred KB each and there are tens of them, so eager loading keeps the UI code simple.
#[derive(Clone, Default)]
pub struct Loaded {
    pub label: String,
    pub entries: Vec<IndexEntry>,
    pub logs: HashMap<String, Arc<FightLog>>,
}

impl Loaded {
    /// Builds from `(file name, contents)` pairs; `index.json` is optional.
    pub fn from_files(label: impl Into<String>, files: Vec<(String, String)>) -> anyhow::Result<Self> {
        let mut logs = HashMap::new();
        let mut index: Option<Vec<IndexEntry>> = None;
        let mut errors = Vec::new();
        for (name, text) in files {
            if name == "index.json" {
                match parse_index(&text) {
                    Ok(entries) => index = Some(entries),
                    Err(err) => errors.push(format!("index.json: {err}")),
                }
            } else if is_log_file_name(&name) {
                match FightLog::parse(&text) {
                    Ok(log) => {
                        logs.insert(name, Arc::new(log));
                    }
                    Err(err) => errors.push(format!("{name}: {err}")),
                }
            }
        }
        if logs.is_empty() {
            return Err(anyhow!(if errors.is_empty() {
                "no fight-log JSON files found".to_string()
            } else {
                errors.join("\n")
            }));
        }

        // Trust the files we hold over the index (it may list files that are gone, or
        // predate totalDamage/weapon: a logged hunt never has 0 damage).
        let mut entries: Vec<IndexEntry> = match index {
            Some(index) => {
                let mut listed: Vec<IndexEntry> = index
                    .into_iter()
                    .filter(|e| logs.contains_key(&e.file))
                    .map(|e| if e.total_damage == 0 { IndexEntry::from_log(&logs[&e.file], &e.file) } else { e })
                    .collect();
                for (name, log) in &logs {
                    if !listed.iter().any(|e| &e.file == name) {
                        listed.push(IndexEntry::from_log(log, name));
                    }
                }
                listed
            }
            None => logs.iter().map(|(name, log)| IndexEntry::from_log(log, name)).collect(),
        };
        entries.sort_by(|a, b| b.started_at.cmp(&a.started_at));

        Ok(Self { label: label.into(), entries, logs })
    }

    pub fn log(&self, file: &str) -> Option<Arc<FightLog>> {
        self.logs.get(file).cloned()
    }
}

#[cfg(not(target_family = "wasm"))]
pub fn load_dir(path: &std::path::Path) -> anyhow::Result<Loaded> {
    let mut files = Vec::new();
    for entry in std::fs::read_dir(path).with_context(|| format!("reading {}", path.display()))? {
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if !name.ends_with(".json") {
            continue;
        }
        let text = std::fs::read_to_string(entry.path()).with_context(|| format!("reading {name}"))?;
        files.push((name, text));
    }
    let label = path
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| path.display().to_string());
    Loaded::from_files(label, files)
}

/// Fetches `index.json` from `base` and then every listed file.
pub async fn load_url(client: Arc<dyn gpui_kit::http_client::HttpClient>, base: &str) -> anyhow::Result<Loaded> {
    let root = if base.ends_with('/') { base.to_string() } else { format!("{base}/") };
    let index_text = fetch_text(&client, &format!("{root}index.json")).await?;
    let index = parse_index(&index_text)?;
    let mut files = vec![("index.json".to_string(), index_text)];
    for entry in &index {
        match fetch_text(&client, &format!("{root}{}", entry.file)).await {
            Ok(text) => files.push((entry.file.clone(), text)),
            Err(err) => log::warn!("skipping {}: {err}", entry.file),
        }
    }
    Loaded::from_files(root, files)
}

async fn fetch_text(client: &Arc<dyn gpui_kit::http_client::HttpClient>, url: &str) -> anyhow::Result<String> {
    use futures::AsyncReadExt as _;
    use gpui_kit::http_client::AsyncBody;

    let response = client.get(url, AsyncBody::default(), true).await.with_context(|| format!("GET {url}"))?;
    if !response.status().is_success() {
        return Err(anyhow!("GET {url}: HTTP {}", response.status()));
    }
    let mut text = String::new();
    response.into_body().read_to_string(&mut text).await?;
    Ok(text)
}

/// Where a native build looks for logs when started without arguments.
#[cfg(not(target_family = "wasm"))]
pub fn default_log_dirs() -> Vec<std::path::PathBuf> {
    let mut dirs = Vec::new();
    if let Ok(env) = std::env::var("MHW_LOGS") {
        dirs.push(env.into());
    }
    let tail = "steamapps/common/Monster Hunter World/nativePC/plugins/CSharp/MhwDpsMeter/logs";
    if let Some(home) = std::env::var_os("HOME").or_else(|| std::env::var_os("USERPROFILE")) {
        let home = std::path::PathBuf::from(home);
        dirs.push(home.join(".steam/steam").join(tail));
        dirs.push(home.join(".local/share/Steam").join(tail));
    }
    dirs.push(std::path::PathBuf::from(r"C:\Program Files (x86)\Steam").join(tail));
    dirs.push(std::path::PathBuf::from("/mnt/steam/SteamLibrary").join(tail));
    dirs
}

#[cfg(not(target_family = "wasm"))]
pub fn sample_dir() -> Option<std::path::PathBuf> {
    let candidates = [
        std::path::PathBuf::from("sample-logs"),
        std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("sample-logs"),
    ];
    candidates.into_iter().find(|p| p.join("index.json").is_file())
}
