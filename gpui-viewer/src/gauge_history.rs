//! Conservative visualization of sampled *local, unverified* monster gauges.
//! Missing observations are gaps, not zeros; no attack attribution or exact
//! proc/break timestamps can be inferred from these two-second snapshots.

use std::collections::BTreeMap;
use crate::model::GaugeSample;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GaugeMetric {
    Stamina,
    Ailment(i32, String),
    Part(i32, String),
}

impl GaugeMetric {
    pub fn key(&self) -> String {
        match self {
            Self::Stamina => "stamina".into(),
            Self::Ailment(id, _) => format!("ailment:{id}"),
            Self::Part(id, _) => format!("part:{id}"),
        }
    }

    pub fn label(&self) -> String {
        match self {
            Self::Stamina => "Stamina remaining".into(),
            Self::Ailment(_, name) => format!("{name} buildup"),
            Self::Part(_, name) => format!("{name} flinch remaining"),
        }
    }
}

fn pct(value: f32, max: f32) -> Option<f32> {
    if !value.is_finite() || !max.is_finite() || max <= 0.0 || value < 0.0 || value > max * 1.01 {
        return None;
    }
    Some((value / max * 100.0).clamp(0.0, 100.0))
}

fn value(sample: &GaugeSample, metric: &GaugeMetric) -> Option<f32> {
    match metric {
        GaugeMetric::Stamina => pct(sample.stamina?, sample.max_stamina?),
        GaugeMetric::Ailment(id, _) => {
            let a = sample.ailments.iter().find(|a| a.id == *id)?;
            pct(a.buildup, a.max_buildup)
        }
        GaugeMetric::Part(id, _) => {
            let p = sample.parts.iter().find(|p| p.part == *id)?;
            pct(p.remaining, p.max)
        }
    }
}

pub fn available_metrics(samples: &[GaugeSample], monster: &str) -> Vec<GaugeMetric> {
    let mut stamina = false;
    let mut ailments = BTreeMap::new();
    let mut parts = BTreeMap::new();
    for sample in samples.iter().filter(|s| s.monster == monster) {
        if value(sample, &GaugeMetric::Stamina).is_some() { stamina = true; }
        for a in &sample.ailments {
            if pct(a.buildup, a.max_buildup).is_some() { ailments.insert(a.id, a.name.clone()); }
        }
        for p in &sample.parts {
            if pct(p.remaining, p.max).is_some() { parts.insert(p.part, p.name.clone()); }
        }
    }
    let mut result = Vec::new();
    if stamina { result.push(GaugeMetric::Stamina); }
    result.extend(ailments.into_iter().map(|(id, name)| GaugeMetric::Ailment(id, name)));
    result.extend(parts.into_iter().map(|(id, name)| GaugeMetric::Part(id, name)));
    result
}

/// Multiple disjoint curves prevent visual interpolation across capture gaps.
pub fn gauge_segments(samples: &[GaugeSample], monster: &str, metric: &GaugeMetric) -> Vec<Vec<(f32, f32)>> {
    let mut result: Vec<Vec<(f32, f32)>> = Vec::new();
    let mut last_t: Option<f32> = None;
    let mut missing = true;
    for sample in samples.iter().filter(|s| s.monster == monster) {
        let Some(v) = value(sample, metric) else { missing = true; continue; };
        if !sample.t.is_finite() || sample.t < 0.0 || last_t.is_some_and(|t| sample.t < t) {
            missing = true;
            continue;
        }
        if missing || sample.clock_adjusted || last_t.is_some_and(|t| sample.t - t > 6.0) {
            result.push(Vec::new());
        }
        result.last_mut().expect("a segment was created").push((sample.t, v));
        last_t = Some(sample.t);
        missing = false;
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn old_logs_unknown_values_and_clock_gaps_are_not_zero() {
        assert!(available_metrics(&[], "m1").is_empty());
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":80,"maxStamina":100,
            "ailments":[{"id":7,"name":"KO","buildup":20,"maxBuildup":100,"procs":0,"duration":0,"maxDuration":0}]},
          {"t":2,"monster":"m2","source":"local-unverified","stamina":40,"maxStamina":100},
          {"t":2,"monster":"m1","source":"local-unverified","stamina":null},
          {"t":4,"monster":"m1","source":"local-unverified","stamina":60,"maxStamina":100},
          {"t":14,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":15,"monster":"m1","source":"local-unverified","stamina":40,"maxStamina":100,"clockAdjusted":true},
          {"t":16,"monster":"m1","source":"local-unverified","stamina":30,"maxStamina":100},
          {"t":40,"monster":"m1","source":"local-unverified","stamina":20,"maxStamina":100}
        ]"#).unwrap();
        assert_eq!(available_metrics(&samples, "m1").iter().map(GaugeMetric::key).collect::<Vec<_>>(), vec!["stamina", "ailment:7"]);
        let lines = gauge_segments(&samples, "m1", &GaugeMetric::Stamina);
        let expected = [vec![(0.0, 80.0)], vec![(4.0, 60.0)], vec![(14.0, 50.0)],
            vec![(15.0, 40.0), (16.0, 30.0)], vec![(40.0, 20.0)]];
        assert_eq!(lines.len(), expected.len(), "missing state and clock adjustments split the timeline");
        for (actual, expected) in lines.iter().zip(expected.iter()) {
            assert_eq!(actual.len(), expected.len());
            for (&(t, pct), &(expected_t, expected_pct)) in actual.iter().zip(expected.iter()) {
                assert_eq!(t, expected_t);
                assert!((pct - expected_pct).abs() < 0.001, "f32 percentages are approximate");
            }
        }
        assert_eq!(gauge_segments(&samples, "m2", &GaugeMetric::Stamina), vec![vec![(2.0, 40.0)]]);
        assert_eq!(gauge_segments(&samples, "m1", &GaugeMetric::Ailment(7, "KO".into())), vec![vec![(0.0, 20.0)]]);
        assert!(gauge_segments(&samples, "m1", &GaugeMetric::Part(2, "Head".into())).is_empty());
    }
}
