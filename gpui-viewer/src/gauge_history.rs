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
    let mut last_source: Option<&str> = None;
    let mut missing = true;
    let mut was_adjusted = false;
    for sample in samples.iter().filter(|s| s.monster == monster) {
        // clock_adjusted is a persistent normalization offset. A per-monster
        // clock_boundary marks one actual discontinuity on the next stored
        // sample; legacy schema-3 logs use the first adjusted edge as fallback.
        let boundary = sample.clock_boundary.unwrap_or(sample.clock_adjusted && !was_adjusted);
        was_adjusted = sample.clock_adjusted;
        let Some(v) = value(sample, metric) else { missing = true; continue; };
        if !sample.t.is_finite() || sample.t < 0.0 || last_t.is_some_and(|t| sample.t < t) {
            missing = true;
            continue;
        }
        // A provenance change breaks continuity even between valid, nearby
        // readings. Never imply interpolation between different sources.
        if missing || boundary || last_source.is_some_and(|source| source != sample.source.as_str())
            || last_t.is_some_and(|t| sample.t - t > 6.0)
        {
            result.push(Vec::new());
        }
        result.last_mut().expect("a segment was created").push((sample.t, v));
        last_source = Some(sample.source.as_str());
        last_t = Some(sample.t);
        missing = false;
    }
    result
}



#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct GaugeCoverage {
    pub observations: usize,
    pub usable: usize,
    pub missing_metric: usize,
    pub invalid_time: usize,
    pub out_of_order: usize,
    pub gaps_over_6s: usize,
    pub source_changes: usize,
    pub clock_boundaries: usize,
    pub non_default_source: usize,
}

/// Counts *stored* quality independent of numeric metric availability.
pub fn gauge_sample_quality(samples: &[GaugeSample], monster: &str) -> GaugeCoverage {
    gauge_coverage_inner(samples, monster, None)
}

/// Counts *stored* observations for one monster/metric, not gameplay coverage.
/// The reader does not establish host authority or how much of the hunt was observed.
pub fn gauge_coverage(samples: &[GaugeSample], monster: &str, metric: &GaugeMetric) -> GaugeCoverage {
    gauge_coverage_inner(samples, monster, Some(metric))
}

fn gauge_coverage_inner(samples: &[GaugeSample], monster: &str, metric: Option<&GaugeMetric>) -> GaugeCoverage {
    let mut coverage = GaugeCoverage::default();
    let mut previous_capture_t: Option<f32> = None;
    // Source transitions are counted in stored capture order regardless of
    // damaged or regressing timestamps. Gap durations need comparable times.
    let mut previous_source: Option<&str> = None;
    let mut last_usable_t: Option<f32> = None;
    let mut was_adjusted = false;
    for sample in samples.iter().filter(|sample| sample.monster == monster) {
        coverage.observations += 1;
        let boundary = sample.clock_boundary.unwrap_or(sample.clock_adjusted && !was_adjusted);
        was_adjusted = sample.clock_adjusted;
        if boundary { coverage.clock_boundaries += 1; }
        if sample.source != "local-unverified" { coverage.non_default_source += 1; }
        if previous_source.is_some_and(|source| source != sample.source.as_str()) {
            coverage.source_changes += 1;
        }
        previous_source = Some(sample.source.as_str());
        if !sample.t.is_finite() || sample.t < 0.0 {
            coverage.invalid_time += 1;
            previous_capture_t = None;
            continue;
        }
        // A clock boundary or regressing time cannot establish a comparable
        // elapsed interval. Never use the backwards sample as the next anchor.
        let regressed = previous_capture_t.is_some_and(|prev_t| sample.t < prev_t);
        if !boundary && !regressed
            && previous_capture_t.is_some_and(|prev_t| sample.t - prev_t > 6.0)
        {
            coverage.gaps_over_6s += 1;
        }
        previous_capture_t = if regressed { None } else { Some(sample.t) };
        let Some(metric) = metric else { continue };
        if value(sample, metric).is_none() {
            coverage.missing_metric += 1;
        } else if last_usable_t.is_some_and(|t| sample.t < t) {
            coverage.out_of_order += 1;
        } else {
            coverage.usable += 1;
            last_usable_t = Some(sample.t);
        }
    }
    coverage
}

/// Metric-independent monster quality label, including when nothing can be charted.
pub fn gauge_sample_quality_label(quality: &GaugeCoverage) -> String {
    format!(
        "Stored samples for this monster: {}; invalid timestamps: {}. Observed boundaries: {} gaps over 6s, {} source changes, {} clock boundaries; {} non-default source label(s) (authority unverified). These are counts of stored observations, not a percentage of hunt coverage.",
        quality.observations, quality.invalid_time, quality.gaps_over_6s,
        quality.source_changes, quality.clock_boundaries, quality.non_default_source,
    )
}

/// Hunt-wide recording evidence. This cannot be assigned to a monster or
/// converted into an expected-sample coverage percentage.
pub fn hunt_wide_quality_notice(dropped: usize, clock_discontinuities: usize) -> Option<String> {
    if dropped == 0 && clock_discontinuities == 0 {
        return None;
    }
    Some(format!(
        "Hunt-wide quality: {dropped} due observation interval(s) omitted by recording cap; \
         {clock_discontinuities} clock correction(s). These totals cannot be assigned to a \
         monster. Do not align gauge curves with hit/DPS times as exact events.",
    ))
}

#[derive(Debug, Clone, PartialEq)]
pub struct CounterInterval {
    pub start_t: f32,
    pub end_t: f32,
    pub before: i32,
    pub after: i32,
    pub increase: i32,
}

/// Captured counter *differences* are known only within the sampling interval.
/// Never turn an observed increment into an exact proc/break event or attacker.
pub fn observed_counter_intervals(
    samples: &[GaugeSample], monster: &str, metric: &GaugeMetric,
) -> Vec<CounterInterval> {
    let mut intervals = Vec::new();
    let mut last: Option<(f32, i32, &str)> = None;
    let mut was_adjusted = false;

    for sample in samples.iter().filter(|s| s.monster == monster) {
        let boundary = sample.clock_boundary.unwrap_or(sample.clock_adjusted && !was_adjusted);
        was_adjusted = sample.clock_adjusted;
        let count = match metric {
            GaugeMetric::Stamina => None,
            GaugeMetric::Ailment(id, _) => sample.ailments.iter().find(|a| a.id == *id).and_then(|a| a.procs),
            GaugeMetric::Part(id, _) => sample.parts.iter().find(|p| p.part == *id).and_then(|p| p.completed_flinches),
        };
        let Some(count) = count.filter(|count| *count >= 0) else {
            last = None;
            continue;
        };
        if !sample.t.is_finite() || sample.t < 0.0 {
            last = None;
            continue;
        }
        if let Some((prev_t, prev_count, prev_source)) = last {
            if !boundary && sample.t > prev_t && sample.t - prev_t <= 6.0
                && sample.source == prev_source && count > prev_count
            {
                intervals.push(CounterInterval {
                    start_t: prev_t, end_t: sample.t,
                    before: prev_count, after: count, increase: count - prev_count,
                });
            }
        }
        last = Some((sample.t, count, &sample.source));
    }
    intervals
}

#[cfg(test)]
mod tests {
    use super::*;








    #[test]
    fn monster_quality_survives_all_unplottable_samples() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":-1,"monster":"m1","source":"unknown","clockBoundary":true},
          {"t":2,"monster":"m2","source":"local-unverified","stamina":40,"maxStamina":100},
          {"t":-1,"monster":"m1","source":"local-unverified"}
        ]"#).unwrap();
        assert!(available_metrics(&samples, "m1").is_empty());
        let quality = gauge_sample_quality(&samples, "m1");
        assert_eq!(quality, GaugeCoverage {
            observations: 2, invalid_time: 2, source_changes: 1,
            clock_boundaries: 1, non_default_source: 1,
            ..GaugeCoverage::default()
        });
        let label = gauge_sample_quality_label(&quality);
        assert!(label.contains("2; invalid timestamps: 2"));
        assert!(label.contains("1 source changes"));
        assert!(label.contains("1 clock boundaries"));
        assert!(label.contains("1 non-default source"));
        assert_eq!(gauge_sample_quality(&samples, "m2").observations, 1);
        assert_eq!(gauge_sample_quality(&samples, "m2").invalid_time, 0);
        assert_eq!(gauge_coverage(&samples, "m1", &GaugeMetric::Stamina).usable, 0);
    }

    #[test]
    fn quality_gaps_exclude_backwards_times_and_clock_boundaries() {
        fn parsed(source: &str) -> Vec<GaugeSample> {
            serde_json::from_str(source).unwrap()
        }
        let backwards = parsed(r#"[
          {"t":10,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":1,"monster":"m2","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":2,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":11,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":19,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100}
        ]"#);
        let metric = GaugeMetric::Stamina;
        let quality = gauge_coverage(&backwards, "m1", &metric);
        assert_eq!(quality.gaps_over_6s, 1, "11→19 is comparable, 2→11 is not");
        assert_eq!(quality.out_of_order, 1);
        assert_eq!(quality.source_changes, 0);
        assert_eq!(gauge_coverage(&backwards, "m2", &metric).gaps_over_6s, 0);

        let corrected = parsed(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":1,"monster":"m2","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":20,"monster":"m1","source":"local-unverified","clockBoundary":true,"stamina":50,"maxStamina":100},
          {"t":20,"monster":"m2","source":"local-unverified","clockBoundary":true,"stamina":50,"maxStamina":100},
          {"t":22,"monster":"m1","source":"local-unverified","clockBoundary":false,"stamina":50,"maxStamina":100},
          {"t":30,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100}
        ]"#);
        let quality = gauge_coverage(&corrected, "m1", &metric);
        assert_eq!((quality.gaps_over_6s, quality.clock_boundaries), (1, 1));
        assert_eq!(gauge_coverage(&corrected, "m2", &metric).gaps_over_6s, 0);

        let legacy = parsed(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":20,"monster":"m1","source":"local-unverified","clockAdjusted":true,"stamina":50,"maxStamina":100},
          {"t":22,"monster":"m1","source":"local-unverified","clockAdjusted":true,"stamina":50,"maxStamina":100},
          {"t":30,"monster":"m1","source":"local-unverified","clockAdjusted":true,"stamina":50,"maxStamina":100}
        ]"#);
        let quality = gauge_coverage(&legacy, "m1", &metric);
        assert_eq!((quality.gaps_over_6s, quality.clock_boundaries), (1, 1));

        let source_flip = parsed(r#"[
          {"t":10,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":2,"monster":"m1","source":"unknown","stamina":50,"maxStamina":100},
          {"t":11,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100}
        ]"#);
        let quality = gauge_coverage(&source_flip, "m1", &metric);
        assert_eq!(quality.gaps_over_6s, 0);
        assert_eq!(quality.source_changes, 2);
        assert_eq!(quality.out_of_order, 1);
    }

    #[test]
    fn coverage_counts_source_switches_at_invalid_and_backward_times() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":10,"maxStamina":100},
          {"t":0,"monster":"m2","source":"local-unverified","stamina":10,"maxStamina":100},
          {"t":-1,"monster":"m1","source":"unknown"},
          {"t":2,"monster":"m1","source":"unknown","stamina":30,"maxStamina":100},
          {"t":1,"monster":"m1","source":"local-unverified","stamina":40,"maxStamina":100},
          {"t":3,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":4,"monster":"m2","source":"local-unverified","stamina":20,"maxStamina":100}
        ]"#).unwrap();
        let m1 = gauge_coverage(&samples, "m1", &GaugeMetric::Stamina);
        assert_eq!(m1.observations, 5);
        assert_eq!(m1.invalid_time, 1);
        assert_eq!(m1.out_of_order, 1);
        assert_eq!(m1.source_changes, 2);
        assert_eq!(m1.non_default_source, 2);
        assert_eq!(m1.gaps_over_6s, 0);
        assert_eq!(gauge_coverage(&samples, "m2", &GaugeMetric::Stamina).source_changes, 0);
    }

    #[test]
    fn hunt_quality_notice_survives_zero_recognized_gauges() {
        let samples: Vec<GaugeSample> = serde_json::from_str("[]").unwrap();
        assert!(available_metrics(&samples, "m1").is_empty());
        let quality = hunt_wide_quality_notice(9, 2).expect("hunt-wide metadata must remain visible");
        assert!(quality.contains("9 due observation"));
        assert!(quality.contains("2 clock correction"));
        assert_eq!(hunt_wide_quality_notice(0, 0), None);
    }

    #[test]
    fn coverage_reports_stored_metric_quality_without_inferred_hunt_coverage() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":10,"maxStamina":100},
          {"t":0,"monster":"m2","source":"local-unverified","stamina":40,"maxStamina":100},
          {"t":2,"monster":"m1","source":"local-unverified","stamina":20,"maxStamina":100},
          {"t":2,"monster":"m2","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":4,"monster":"m1","source":"local-unverified"},
          {"t":12,"monster":"m1","source":"unknown","stamina":30,"maxStamina":100},
          {"t":14,"monster":"m1","source":"unknown","stamina":40,"maxStamina":100},
          {"t":-1,"monster":"m1","source":"local-unverified","clockBoundary":true},
          {"t":16,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":18,"monster":"m1","source":"local-unverified","stamina":60,"maxStamina":100},
          {"t":17,"monster":"m1","source":"local-unverified","stamina":65,"maxStamina":100},
          {"t":20,"monster":"m1","source":"local-unverified"},
          {"t":22,"monster":"m1","source":"local-unverified","stamina":70,"maxStamina":100}
        ]"#).unwrap();
        let m1 = gauge_coverage(&samples, "m1", &GaugeMetric::Stamina);
        assert_eq!(m1, GaugeCoverage {
            observations: 11, usable: 7, missing_metric: 2, invalid_time: 1,
            out_of_order: 1, gaps_over_6s: 1, source_changes: 2,
            clock_boundaries: 1, non_default_source: 2,
        });
        let m2 = gauge_coverage(&samples, "m2", &GaugeMetric::Stamina);
        assert_eq!(m2, GaugeCoverage {
            observations: 2, usable: 2, ..GaugeCoverage::default()
        });
        assert_eq!(gauge_coverage(&[], "m1", &GaugeMetric::Stamina), GaugeCoverage::default());
    }

    #[test]
    fn alternating_single_reading_sources_stay_isolated() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":10,"maxStamina":100},
          {"t":2,"monster":"m1","source":"unknown","stamina":20,"maxStamina":100},
          {"t":4,"monster":"m1","source":"local-unverified","stamina":30,"maxStamina":100}
        ]"#).unwrap();
        let segments = gauge_segments(&samples, "m1", &GaugeMetric::Stamina);
        assert_eq!(segments.iter().map(Vec::len).collect::<Vec<_>>(), vec![1, 1, 1]);
        assert_eq!(gauge_coverage(&samples, "m1", &GaugeMetric::Stamina).source_changes, 2);
    }

    #[test]
    fn history_does_not_join_readings_across_provenance_changes() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","stamina":10,"maxStamina":100},
          {"t":0,"monster":"m2","source":"local-unverified","stamina":30,"maxStamina":100},
          {"t":2,"monster":"m1","source":"local-unverified","stamina":20,"maxStamina":100},
          {"t":2,"monster":"m2","source":"local-unverified","stamina":40,"maxStamina":100},
          {"t":4,"monster":"m1","source":"unknown","stamina":30,"maxStamina":100},
          {"t":6,"monster":"m1","source":"unknown","stamina":40,"maxStamina":100},
          {"t":8,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100},
          {"t":10,"monster":"m1","source":"local-unverified","stamina":60,"maxStamina":100}
        ]"#).unwrap();
        let curves = gauge_segments(&samples, "m1", &GaugeMetric::Stamina);
        assert_eq!(curves.len(), 3, "two source switches must split three curves");
        assert_eq!(curves.iter().map(Vec::len).collect::<Vec<_>>(), vec![2, 2, 2]);
        assert_eq!(curves.iter().map(|segment| segment[0].0).collect::<Vec<_>>(), vec![0.0, 4.0, 8.0]);
        assert_eq!(gauge_segments(&samples, "m2", &GaugeMetric::Stamina).len(), 1);
    }

    #[test]
    fn counter_changes_are_sample_intervals_not_precise_proc_events() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":80,"max":100,"completedFlinches":0}],"ailments":[{"id":7,"name":"KO","buildup":10,"maxBuildup":100,"procs":0,"duration":0,"maxDuration":0}]},
          {"t":0,"monster":"m2","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":80,"max":100,"completedFlinches":7}]},
          {"t":2,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":70,"max":100,"completedFlinches":1}],"ailments":[{"id":7,"name":"KO","buildup":15,"maxBuildup":100,"procs":2,"duration":0,"maxDuration":0}]},
          {"t":2,"monster":"m2","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":70,"max":100,"completedFlinches":8}]},
          {"t":4,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":70,"max":100,"completedFlinches":1}],"ailments":[{"id":7,"name":"KO","buildup":15,"maxBuildup":100,"procs":2,"duration":0,"maxDuration":0}]},
          {"t":-1,"monster":"m1","source":"local-unverified","clockBoundary":true},
          {"t":6,"monster":"m1","source":"local-unverified","clockBoundary":false,"parts":[{"part":2,"name":"Head","remaining":50,"max":100,"completedFlinches":3}],"ailments":[{"id":7,"name":"KO","buildup":20,"maxBuildup":100,"procs":4,"duration":0,"maxDuration":0}]},
          {"t":8,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":40,"max":100,"completedFlinches":4}],"ailments":[{"id":7,"name":"KO","buildup":20,"maxBuildup":100,"procs":5,"duration":0,"maxDuration":0}]},
          {"t":20,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":40,"max":100,"completedFlinches":5}]},
          {"t":22,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":80,"max":100,"completedFlinches":1}]},
          {"t":24,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":60,"max":100,"completedFlinches":2}]},
          {"t":26,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":50,"max":100}]},
          {"t":28,"monster":"m1","source":"local-unverified","parts":[{"part":2,"name":"Head","remaining":50,"max":100,"completedFlinches":3}]},
          {"t":30,"monster":"m1","source":"local-unverified","clockBoundary":true,"parts":[{"part":2,"name":"Head","remaining":40,"max":100,"completedFlinches":4}]},
          {"t":32,"monster":"m1","source":"local-unverified","clockBoundary":false,"parts":[{"part":2,"name":"Head","remaining":30,"max":100,"completedFlinches":5}]},
          {"t":34,"monster":"m1","source":"another-source","parts":[{"part":2,"name":"Head","remaining":30,"max":100,"completedFlinches":6}]},
          {"t":36,"monster":"m1","source":"another-source","parts":[{"part":2,"name":"Head","remaining":20,"max":100,"completedFlinches":7}]}
        ]"#).unwrap();
        let part = observed_counter_intervals(&samples, "m1", &GaugeMetric::Part(2, "Head".into()));
        assert_eq!(part.iter().map(|x| (x.start_t, x.end_t, x.increase)).collect::<Vec<_>>(),
            vec![(0.0, 2.0, 1), (6.0, 8.0, 1), (22.0, 24.0, 1),
                (30.0, 32.0, 1), (34.0, 36.0, 1)]);
        let ailment = observed_counter_intervals(&samples, "m1", &GaugeMetric::Ailment(7, "KO".into()));
        assert_eq!(ailment.iter().map(|x| (x.start_t, x.end_t, x.increase)).collect::<Vec<_>>(),
            vec![(0.0, 2.0, 2), (6.0, 8.0, 1)]);
        assert_eq!(observed_counter_intervals(&samples, "m2", &GaugeMetric::Part(2, "Head".into())).len(), 1);
        assert!(observed_counter_intervals(&samples, "m1", &GaugeMetric::Stamina).is_empty());

        // Missing optional counters cannot masquerade as an observed 0 -> 1.
        let invalid: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","buildup":10,"maxBuildup":100,"duration":0,"maxDuration":0}]},
          {"t":2,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","buildup":20,"maxBuildup":100,"procs":1,"duration":0,"maxDuration":0}]}
        ]"#).unwrap();
        assert!(observed_counter_intervals(&invalid, "m1", &GaugeMetric::Ailment(7, "KO".into())).is_empty());
    }

    #[test]
    fn post_reset_samples_join_one_new_curve_in_new_and_legacy_logs() {
        let new_samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":100,"monster":"m1","source":"local-unverified","stamina":10,"maxStamina":100,"clockAdjusted":false,"clockBoundary":false},
          {"t":102,"monster":"m1","source":"local-unverified","stamina":20,"maxStamina":100,"clockAdjusted":false,"clockBoundary":false},
          {"t":104,"monster":"m1","source":"local-unverified","stamina":30,"maxStamina":100,"clockAdjusted":true,"clockBoundary":true},
          {"t":106,"monster":"m1","source":"local-unverified","stamina":40,"maxStamina":100,"clockAdjusted":true,"clockBoundary":false},
          {"t":108,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100,"clockAdjusted":true,"clockBoundary":false},
          {"t":110,"monster":"m1","source":"local-unverified","stamina":60,"maxStamina":100,"clockAdjusted":true,"clockBoundary":false}
        ]"#).unwrap();
        let segments = gauge_segments(&new_samples, "m1", &GaugeMetric::Stamina);
        assert_eq!(segments.len(), 2, "one clock jump yields exactly two curves");
        assert_eq!(segments[0].len(), 2);
        assert_eq!(segments[1].len(), 4, "three later valid samples share the corrected curve");
        assert_eq!(segments[1].last().unwrap().0, 110.0);

        // Pre-fix schema-3 logs have a persistent clockAdjusted flag but
        // omitted clockBoundary. Only the false -> true edge is a boundary.
        let old_samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":100,"monster":"m1","source":"local-unverified","stamina":10,"maxStamina":100},
          {"t":102,"monster":"m1","source":"local-unverified","stamina":20,"maxStamina":100},
          {"t":104,"monster":"m1","source":"local-unverified","stamina":30,"maxStamina":100,"clockAdjusted":true},
          {"t":106,"monster":"m1","source":"local-unverified","stamina":40,"maxStamina":100,"clockAdjusted":true},
          {"t":108,"monster":"m1","source":"local-unverified","stamina":50,"maxStamina":100,"clockAdjusted":true},
          {"t":110,"monster":"m1","source":"local-unverified","stamina":60,"maxStamina":100,"clockAdjusted":true}
        ]"#).unwrap();
        let legacy = gauge_segments(&old_samples, "m1", &GaugeMetric::Stamina);
        assert_eq!(legacy.len(), 2);
        assert_eq!(legacy[1].len(), 4);
    }

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
