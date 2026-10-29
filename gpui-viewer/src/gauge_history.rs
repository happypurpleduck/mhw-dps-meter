//! Conservative visualization of sampled *local, unverified* monster gauges.
//! Missing observations are gaps, not zeros; no attack attribution or exact
//! proc/break timestamps can be inferred from these two-second snapshots.

use std::collections::BTreeMap;
use crate::model::GaugeSample;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GaugeMetric {
    Stamina,
    Ailment(i32, String),
    AilmentDuration(i32, String),
    Part(i32, String),
}

impl GaugeMetric {
    pub fn key(&self) -> String {
        match self {
            Self::Stamina => "stamina".into(),
            Self::Ailment(id, _) => format!("ailment:{id}"),
            Self::AilmentDuration(id, _) => format!("ailment-duration:{id}"),
            Self::Part(id, _) => format!("part:{id}"),
        }
    }

    pub fn label(&self) -> String {
        match self {
            Self::Stamina => "Stamina remaining".into(),
            Self::Ailment(_, name) => format!("{name} buildup"),
            Self::AilmentDuration(_, name) => format!("{name} duration remaining"),
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
        GaugeMetric::Ailment(id, _) => sample.ailments.iter()
            .filter(|a| a.id == *id)
            .find_map(|a| a.buildup.zip(a.max_buildup)
                .and_then(|(current, max)| pct(current, max))),
        GaugeMetric::AilmentDuration(id, _) => sample.ailments.iter()
            .filter(|a| a.id == *id)
            .find_map(|a| a.duration.zip(a.max_duration)
                .and_then(|(current, max)| pct(current, max))),
        GaugeMetric::Part(id, _) => {
            let p = sample.parts.iter().find(|p| p.part == *id)?;
            pct(p.remaining, p.max)
        }
    }
}

pub fn available_metrics(samples: &[GaugeSample], monster: &str) -> Vec<GaugeMetric> {
    let mut stamina = false;
    let mut ailments: BTreeMap<i32, (String, bool, bool)> = BTreeMap::new();
    let mut parts = BTreeMap::new();
    for sample in samples.iter().filter(|s| s.monster == monster) {
        if value(sample, &GaugeMetric::Stamina).is_some() { stamina = true; }
        for a in &sample.ailments {
            let buildup = a.buildup.zip(a.max_buildup)
                .is_some_and(|(current, max)| pct(current, max).is_some());
            let duration = a.duration.zip(a.max_duration)
                .is_some_and(|(current, max)| pct(current, max).is_some());
            if buildup || duration {
                let entry = ailments.entry(a.id).or_insert_with(|| (a.name.clone(), false, false));
                entry.0 = a.name.clone();
                entry.1 |= buildup;
                entry.2 |= duration;
            }
        }
        for p in &sample.parts {
            if pct(p.remaining, p.max).is_some() { parts.insert(p.part, p.name.clone()); }
        }
    }
    let mut result = Vec::new();
    if stamina { result.push(GaugeMetric::Stamina); }
    for (id, (name, has_buildup, has_duration)) in ailments {
        if has_buildup { result.push(GaugeMetric::Ailment(id, name.clone())); }
        if has_duration { result.push(GaugeMetric::AilmentDuration(id, name)); }
    }
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


#[derive(Debug, Clone, PartialEq)]
pub enum GaugeQualityMarker {
    ClockBoundary(Option<f32>),
    InvalidTime,
    SourceChange(Option<f32>),
    CaptureGap { start: f32, end: f32 },
    BackwardTime(f32),
}

/// Shared presentation contract: newest five emitted markers in reverse
/// stored-capture order, including multiple markers on one observation.
pub fn recent_gauge_quality_markers(markers: &[GaugeQualityMarker]) -> Vec<&GaugeQualityMarker> {
    markers.iter().rev().take(5).collect()
}

impl GaugeQualityMarker {
    pub fn label(&self) -> String {
        match self {
            Self::ClockBoundary(Some(t)) => format!(
                "Clock normalization boundary; stored sample at {t:.1}s (not a game event)."),
            Self::ClockBoundary(None) => "Clock normalization boundary; capture time unavailable (not a game event).".into(),
            Self::InvalidTime => "Invalid stored capture timestamp; no game-event time can be inferred.".into(),
            Self::SourceChange(Some(t)) => format!(
                "Observation source label changed; stored sample at {t:.1}s (authority unverified)."),
            Self::SourceChange(None) => "Observation source label changed; capture time unavailable (authority unverified).".into(),
            Self::CaptureGap { start, end } => format!(
                "Comparable stored-sample gap from {start:.1}s to {end:.1}s (>6s); no intervening event inferred."),
            Self::BackwardTime(t) => format!(
                "Stored timestamp moved backward; stored sample at {t:.1}s (elapsed time incomparable)."),
        }
    }
}

/// Observed stored-capture interruptions for a single fight-local monster.
/// Does not create a timed combat event, host claim, or expected coverage.
pub fn gauge_quality_markers(samples: &[GaugeSample], monster: &str) -> Vec<GaugeQualityMarker> {
    let mut markers = Vec::new();
    let mut previous_t: Option<f32> = None;
    let mut previous_source: Option<&str> = None;
    let mut was_adjusted = false;
    for sample in samples.iter().filter(|sample| sample.monster == monster) {
        let boundary = sample.clock_boundary.unwrap_or(sample.clock_adjusted && !was_adjusted);
        was_adjusted = sample.clock_adjusted;
        let valid_t = sample.t.is_finite() && sample.t >= 0.0;
        let at = valid_t.then_some(sample.t);
        if boundary { markers.push(GaugeQualityMarker::ClockBoundary(at)); }
        if previous_source.is_some_and(|source| source != sample.source.as_str()) {
            markers.push(GaugeQualityMarker::SourceChange(at));
        }
        previous_source = Some(sample.source.as_str());
        if !valid_t {
            markers.push(GaugeQualityMarker::InvalidTime);
            previous_t = None;
            continue;
        }
        let regressed = previous_t.is_some_and(|t| sample.t < t);
        if regressed {
            markers.push(GaugeQualityMarker::BackwardTime(sample.t));
        }
        if !boundary && !regressed {
            if let Some(start) = previous_t.filter(|t| sample.t - t > 6.0) {
                markers.push(GaugeQualityMarker::CaptureGap { start, end: sample.t });
            }
        }
        previous_t = if regressed { None } else { Some(sample.t) };
    }
    markers
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
            GaugeMetric::Stamina | GaugeMetric::AilmentDuration(..) => None,
            GaugeMetric::Ailment(id, _) => sample.ailments.iter()
                .filter(|a| a.id == *id)
                .filter_map(|a| a.procs)
                .find(|count| *count >= 0),
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


#[derive(Debug, Clone, PartialEq)]
pub struct CounterTimelineEntry {
    pub metric: GaugeMetric,
    pub interval: CounterInterval,
}

impl CounterTimelineEntry {
    pub fn label(&self) -> String {
        match &self.metric {
            GaugeMetric::Ailment(_, name) => format!("{name} sampled proc counter"),
            GaugeMetric::Part(_, name) => format!("{name} sampled flinch counter"),
            _ => String::new(),
        }
    }
}

/// Newest five stored positive counter changes across all recorded local gauges,
/// independent of chartable percentage metrics. Each interval must pass the same
/// per-metric missing, source, time and clock guards as the existing analyzer.
pub fn observed_counter_timeline(samples: &[GaugeSample], monster: &str) -> Vec<CounterTimelineEntry> {
    let mut metrics = BTreeMap::new();
    for sample in samples.iter().filter(|s| s.monster == monster) {
        for a in &sample.ailments {
            if a.procs.is_some_and(|count| count >= 0) {
                metrics.insert(format!("ailment:{}", a.id), GaugeMetric::Ailment(a.id, a.name.clone()));
            }
        }
        for p in &sample.parts {
            if p.completed_flinches.is_some_and(|count| count >= 0) {
                metrics.insert(format!("part:{}", p.part), GaugeMetric::Part(p.part, p.name.clone()));
            }
        }
    }
    let mut result = Vec::new();
    for metric in metrics.into_values() {
        for interval in observed_counter_intervals(samples, monster, &metric) {
            result.push(CounterTimelineEntry { metric: metric.clone(), interval });
        }
    }
    result.sort_by(|a, b| b.interval.end_t.total_cmp(&a.interval.end_t)
        .then_with(|| b.interval.start_t.total_cmp(&a.interval.start_t))
        .then_with(|| a.metric.key().cmp(&b.metric.key())));
    result.truncate(5);
    result
}

#[cfg(test)]
mod tests {
    use super::*;














    #[test]
    fn shared_malformed_import_i32_bounds_match_web_and_never_invent_counter_intervals() {
        let log = crate::model::FightLog::parse(
            include_str!("../../tests/fixtures/gauge-counter-i32-parity.json")
        ).expect("valid hunt with intentionally malformed optional gauge fields");
        let samples = &log.gauge_samples;
        assert_eq!(samples.len(), 13);
        assert_eq!(samples[0].parts.iter().map(|p| p.part).collect::<Vec<_>>(), vec![2, 3]);
        assert_eq!(samples[0].ailments.iter().map(|a| a.id).collect::<Vec<_>>(), vec![7, 8]);
        assert_eq!(samples[0].parts[0].completed_flinches, None);
        assert_eq!(samples[0].parts[0].next_break_threshold, None);
        assert_eq!(samples[0].ailments[0].procs, None);
        assert_eq!(samples[1].ailments[0].procs, None);
        assert_eq!(samples[2].ailments[0].procs, None);
        assert_eq!(samples[2].parts[0].completed_flinches, None);
        assert_eq!(samples[4].parts.iter().find(|p| p.part == 4).unwrap().completed_flinches, None);
        assert_eq!(samples[4].parts.iter().find(|p| p.part == 5).unwrap().completed_flinches, None);
        assert_eq!(samples[4].ailments.iter().find(|a| a.id == 9).unwrap().procs, None);
        assert_eq!(samples[4].ailments.iter().find(|a| a.id == 10).unwrap().procs, None);
        let ko = GaugeMetric::Ailment(7, "KO".into());
        let head = GaugeMetric::Part(2, "Head".into());
        for metric in [&ko, &head] {
            assert_eq!(observed_counter_intervals(samples, "m1", metric).iter()
                .map(|x| (x.start_t, x.end_t, x.before, x.after, x.increase))
                .collect::<Vec<_>>(), vec![(6., 8., 1, 2, 1)]);
        }
        assert_eq!(observed_counter_timeline(samples, "m1").iter()
            .map(|e| (e.metric.key(), e.interval.start_t, e.interval.end_t, e.interval.increase))
            .collect::<Vec<_>>(), vec![
            ("ailment:7".into(), 6., 8., 1),
            ("ailment:8".into(), 6., 8., 1),
            ("part:2".into(), 6., 8., 1),
            ("part:3".into(), 6., 8., 1),
            ("ailment:8".into(), 4., 6., 1),
        ]);
        assert_eq!(samples[5].ailments[0].procs, Some(2147483646));
        assert_eq!(samples[6].ailments[0].procs, Some(2147483647));
        assert_eq!(observed_counter_timeline(samples, "m2").iter()
            .map(|e| (e.metric.key(), e.interval.before, e.interval.after, e.interval.increase))
            .collect::<Vec<_>>(), vec![
            ("ailment:7".into(), 2147483646, 2147483647, 1),
            ("part:2".into(), 2147483646, 2147483647, 1),
        ]);

        // Raw shared JSON uses 1.0, 2e0, and 3e0. GPUI accepts their
        // integral values just as JS JSON.parse does, but rejects 3.5.
        let integral_float_samples: Vec<_> = samples.iter().filter(|s| s.monster == "m3").collect();
        assert_eq!(integral_float_samples.len(), 6);
        assert_eq!(integral_float_samples[0].parts[0].part, 2);
        assert_eq!(integral_float_samples[0].parts[0].completed_flinches, Some(1));
        assert_eq!(integral_float_samples[0].parts[0].next_break_threshold, Some(3));
        assert_eq!(integral_float_samples[0].ailments[0].id, 7);
        assert_eq!(integral_float_samples[0].ailments[0].procs, Some(1));
        assert_eq!(integral_float_samples[1].parts[0].completed_flinches, Some(2));
        assert_eq!(integral_float_samples[2].ailments[0].procs, Some(3));
        assert_eq!(integral_float_samples[3].parts[0].completed_flinches, None);
        assert_eq!(integral_float_samples[3].ailments[0].procs, None);
        assert_eq!(observed_counter_timeline(samples, "m3").iter()
            .map(|e| (e.metric.key(), e.interval.start_t, e.interval.end_t, e.interval.increase))
            .collect::<Vec<_>>(), vec![
            ("ailment:7".into(), 8., 10., 1),
            ("part:2".into(), 8., 10., 1),
            ("ailment:7".into(), 2., 4., 1),
            ("part:2".into(), 2., 4., 1),
            ("ailment:7".into(), 0., 2., 1),
        ]);
    }

    #[test]
    fn counter_timeline_combines_monster_metrics_with_newest_five_deterministic_order() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","procs":0},{"id":6,"name":"Exhaust","procs":0}]},
          {"t":0,"monster":"m2","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","procs":10},{"id":6,"name":"Exhaust","procs":10}]},
          {"t":2,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":5,"maxDuration":10},
            {"id":7,"name":"KO","procs":1},{"id":6,"name":"Exhaust","procs":1}]},
          {"t":4,"monster":"m1","source":"local-unverified","parts":[
            {"part":2,"name":"Head","remaining":30,"max":100,"completedFlinches":2}],
            "ailments":[{"id":7,"name":"KO","procs":2},{"id":6,"name":"Exhaust","procs":2}]},
          {"t":6,"monster":"m1","source":"local-unverified","parts":[
            {"part":2,"name":"Head","remaining":30,"max":100,"completedFlinches":3}],
            "ailments":[{"id":7,"name":"KO","procs":3},{"id":6,"name":"Exhaust","procs":3}]},
          {"t":8,"monster":"m1","source":"local-unverified","parts":[
            {"part":2,"name":"Head","remaining":30,"max":100,"completedFlinches":4}],
            "ailments":[{"id":7,"name":"KO","procs":4},{"id":6,"name":"Exhaust","procs":4}]},
          {"t":10,"monster":"m1","source":"local-unverified","parts":[
            {"part":2,"name":"Head","remaining":30,"max":100,"completedFlinches":5}],
            "ailments":[{"id":7,"name":"KO","procs":5},{"id":6,"name":"Exhaust","procs":5}]},
          {"t":12,"monster":"m1","source":"unknown","ailments":[
            {"id":7,"name":"KO","procs":6},{"id":6,"name":"Exhaust","procs":6}]},
          {"t":14,"monster":"m1","source":"unknown","ailments":[
            {"id":7,"name":"KO","procs":7},{"id":6,"name":"Exhaust","procs":7}]},
          {"t":2,"monster":"m2","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","procs":11},{"id":6,"name":"Exhaust","procs":11}]}
        ]"#).unwrap();
        let recent = observed_counter_timeline(&samples, "m1");
        assert_eq!(recent.iter().map(|item| (item.metric.key(), item.interval.start_t,
            item.interval.end_t, item.interval.increase)).collect::<Vec<_>>(), vec![
            ("ailment:6".into(), 12., 14., 1), ("ailment:7".into(), 12., 14., 1),
            ("ailment:6".into(), 8., 10., 1), ("ailment:7".into(), 8., 10., 1),
            ("part:2".into(), 8., 10., 1),
        ]);
        assert_eq!(recent.iter().map(CounterTimelineEntry::label).collect::<Vec<_>>(),
            vec!["Exhaust sampled proc counter", "KO sampled proc counter",
                "Exhaust sampled proc counter", "KO sampled proc counter",
                "Head sampled flinch counter"]);
        assert_eq!(observed_counter_timeline(&samples, "m2").iter()
            .map(|item| (item.metric.key(), item.interval.start_t, item.interval.end_t))
            .collect::<Vec<_>>(), vec![
            ("ailment:6".into(), 0., 2.), ("ailment:7".into(), 0., 2.),
        ]);
    }

    #[test]
    fn counter_only_capture_intervals_visible_without_numeric_metric_or_exact_events() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","procs":0}]},
          {"t":2,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","procs":1}]},
          {"t":4,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO"}]},
          {"t":6,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","procs":3}]},
          {"t":8,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","procs":4}]},
          {"t":10,"monster":"m1","source":"local-unverified","clockBoundary":true,"ailments":[{"id":7,"name":"KO","procs":5}]},
          {"t":12,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":6}]},
          {"t":14,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":7}]},
          {"t":16,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":8}]}
        ]"#).unwrap();
        assert!(available_metrics(&samples, "m1").is_empty());
        assert_eq!(observed_counter_timeline(&samples, "m1").iter()
            .map(|v| (v.metric.key(), v.interval.start_t, v.interval.end_t, v.interval.before, v.interval.after))
            .collect::<Vec<_>>(), vec![
            ("ailment:7".into(), 14., 16., 7, 8),
            ("ailment:7".into(), 12., 14., 6, 7),
            ("ailment:7".into(), 6., 8., 3, 4),
            ("ailment:7".into(), 0., 2., 0, 1),
        ]);
        assert!(observed_counter_timeline(&samples, "absent").is_empty());
        assert!(observed_counter_timeline(&[], "m1").is_empty());

        let ui = include_str!("ui/detail.rs");
        let presented = ui.find("if !counter_timeline.is_empty()")
            .expect("counter-only timeline rendered when it has observations");
        let metric_guard = ui.find("if choices.is_empty()")
            .expect("numeric chart metric fallback remains");
        assert!(presented < metric_guard, "counter-only evidence must render outside chart-availability guard");
    }

    #[test]
    fn backward_clock_boundary_preserves_both_markers_and_last_five_display_order() {
        use GaugeQualityMarker as M;
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified"},
          {"t":9,"monster":"m1","source":"local-unverified"},
          {"t":7,"monster":"m1","source":"unknown","clockBoundary":true},
          {"t":10,"monster":"m1","source":"unknown"},
          {"t":19,"monster":"m1","source":"unknown"},
          {"t":5,"monster":"m2","source":"local-unverified"},
          {"t":-1,"monster":"m1","source":"local-unverified"}
        ]"#).unwrap();
        assert!(available_metrics(&samples, "m1").is_empty());
        let markers = gauge_quality_markers(&samples, "m1");
        assert_eq!(markers, vec![
            M::CaptureGap { start: 0.0, end: 9.0 },
            M::ClockBoundary(Some(7.0)),
            M::SourceChange(Some(7.0)),
            M::BackwardTime(7.0),
            M::CaptureGap { start: 10.0, end: 19.0 },
            M::SourceChange(None),
            M::InvalidTime,
        ]);
        let recent = recent_gauge_quality_markers(&markers);
        assert_eq!(recent, vec![
            &M::InvalidTime,
            &M::SourceChange(None),
            &M::CaptureGap { start: 10.0, end: 19.0 },
            &M::BackwardTime(7.0),
            &M::SourceChange(Some(7.0)),
        ]);
        assert_eq!(recent.len(), 5);
        assert!(recent[0].label().contains("no game-event time"));
        assert!(recent[3].label().contains("elapsed time incomparable"));
        assert!(gauge_quality_markers(&samples, "m2").is_empty());
        assert!(recent_gauge_quality_markers(&[]).is_empty());
        assert_eq!(recent_gauge_quality_markers(&markers[..2]), vec![&markers[1], &markers[0]]);
        let q = gauge_sample_quality(&samples, "m1");
        assert_eq!((q.observations, q.clock_boundaries, q.invalid_time,
            q.gaps_over_6s, q.source_changes), (6, 1, 1, 2, 2));

        // Source-level UI placement contract; actual GUI testing still requires #17.
        let ui = include_str!("ui/detail.rs");
        let rendered = ui.find("for marker in recent_gauge_quality_markers(&interruption_markers)")
            .expect("GPUI uses the tested latest-five helper");
        let empty_metric = ui.find("if choices.is_empty()")
            .expect("GPUI retains a no-chartable-metric fallback");
        assert!(rendered < empty_metric, "capture quality must render outside metric guard");
    }

    #[test]
    fn interruption_markers_are_capture_evidence_not_precise_combat_events() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified"},
          {"t":0,"monster":"m2","source":"local-unverified"},
          {"t":10,"monster":"m1","source":"local-unverified"},
          {"t":-1,"monster":"m1","source":"unknown","clockBoundary":true},
          {"t":7,"monster":"m1","source":"unknown"},
          {"t":2,"monster":"m1","source":"local-unverified"},
          {"t":20,"monster":"m1","source":"local-unverified"},
          {"t":5,"monster":"m2","source":"local-unverified"}
        ]"#).unwrap();
        use GaugeQualityMarker as M;
        let markers = gauge_quality_markers(&samples, "m1");
        assert_eq!(markers, vec![
            M::CaptureGap { start: 0.0, end: 10.0 },
            M::ClockBoundary(None),
            M::SourceChange(None),
            M::InvalidTime,
            M::SourceChange(Some(2.0)),
            M::BackwardTime(2.0),
        ]);
        assert!(gauge_quality_markers(&samples, "m2").is_empty());
        assert!(markers[1].label().contains("capture time unavailable"));
        assert!(markers[0].label().contains("no intervening event inferred"));
        let q = gauge_sample_quality(&samples, "m1");
        assert_eq!((q.gaps_over_6s, q.clock_boundaries, q.source_changes, q.invalid_time), (1, 1, 2, 1));

        let legacy: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","clockAdjusted":false},
          {"t":20,"monster":"m1","source":"local-unverified","clockAdjusted":true},
          {"t":22,"monster":"m1","source":"local-unverified","clockAdjusted":true}
        ]"#).unwrap();
        assert_eq!(gauge_quality_markers(&legacy, "m1"), vec![M::ClockBoundary(Some(20.0))]);
    }

    #[test]
    fn partial_duplicate_ailment_ids_use_first_valid_pair_per_metric() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","buildup":20,"maxBuildup":100},
            {"id":7,"name":"KO","duration":5,"maxDuration":10}]},
          {"t":1,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":9,"maxDuration":0},
            {"id":7,"name":"KO","duration":2,"maxDuration":10},
            {"id":7,"name":"KO","buildup":60,"maxBuildup":100}]},
          {"t":2,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","buildup":90,"maxBuildup":0,"duration":3},
            {"id":7,"name":"KO","buildup":40,"maxBuildup":100,"maxDuration":10}]},
          {"t":0,"monster":"m2","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":9,"maxDuration":10}]}
        ]"#).unwrap();
        assert_eq!(available_metrics(&samples, "m1").iter().map(GaugeMetric::key).collect::<Vec<_>>(),
            vec!["ailment:7", "ailment-duration:7"]);
        let buildup = GaugeMetric::Ailment(7, "KO".into());
        let duration = GaugeMetric::AilmentDuration(7, "KO".into());
        let buildup_values = gauge_segments(&samples, "m1", &buildup).into_iter()
            .flatten().map(|(_, p)| p).collect::<Vec<_>>();
        let duration_values = gauge_segments(&samples, "m1", &duration).into_iter()
            .flatten().map(|(_, p)| p).collect::<Vec<_>>();
        for (actual, expected) in buildup_values.iter().zip([20.0, 60.0, 40.0]) {
            assert!((actual - expected).abs() < 0.001, "sampled percentage has f32 rounding");
        }
        for (actual, expected) in duration_values.iter().zip([50.0, 20.0]) {
            assert!((actual - expected).abs() < 0.001, "sampled percentage has f32 rounding");
        }
        assert_eq!(buildup_values.len(), 3);
        assert_eq!(duration_values.len(), 2);
        assert_eq!(gauge_segments(&samples, "m2", &duration), vec![vec![(0.0, 90.0)]]);
        let c = gauge_coverage(&samples, "m1", &duration);
        assert_eq!((c.observations, c.usable, c.missing_metric), (3, 2, 1));
    }

    #[test]
    fn ailment_duration_is_independent_from_buildup_and_missing_is_not_zero() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","buildup":30,"maxBuildup":100,"duration":5,"maxDuration":10,"procs":0}]},
          {"t":1,"monster":"m2","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","buildup":40,"maxBuildup":100,"duration":4,"maxDuration":10}]},
          {"t":2,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":2,"maxDuration":10}]},
          {"t":4,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","buildup":40,"maxBuildup":100,"maxDuration":10}]},
          {"t":6,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":4,"maxDuration":10}]},
          {"t":8,"monster":"m1","source":"local-unverified","clockBoundary":true,"ailments":[
            {"id":7,"name":"KO","duration":1,"maxDuration":10}]},
          {"t":10,"monster":"m1","source":"local-unverified","clockBoundary":false,"ailments":[
            {"id":7,"name":"KO","duration":0,"maxDuration":10}]},
          {"t":12,"monster":"m1","source":"unknown","ailments":[
            {"id":7,"name":"KO","duration":0,"maxDuration":10}]},
          {"t":14,"monster":"m1","source":"unknown","ailments":[
            {"id":7,"name":"KO","duration":9,"maxDuration":0}]},
          {"t":16,"monster":"m1","source":"unknown","ailments":[
            {"id":7,"name":"KO","maxDuration":10}]}
        ]"#).unwrap();
        let keys = available_metrics(&samples, "m1").iter().map(GaugeMetric::key).collect::<Vec<_>>();
        assert_eq!(keys, vec!["ailment:7", "ailment-duration:7"]);
        let duration = GaugeMetric::AilmentDuration(7, "KO".into());
        assert_eq!(duration.label(), "KO duration remaining");
        let segments = gauge_segments(&samples, "m1", &duration);
        assert_eq!(segments.len(), 4);
        assert_eq!(segments.iter().map(Vec::len).collect::<Vec<_>>(), vec![2, 1, 2, 1]);
        let actual: Vec<_> = segments.iter().flatten().copied().collect();
        let expected = [(0., 50.), (2., 20.), (6., 40.), (8., 10.), (10., 0.), (12., 0.)];
        for (&(t, p), &(et, ep)) in actual.iter().zip(expected.iter()) {
            assert_eq!(t, et);
            assert!((p - ep).abs() < 0.001);
        }
        let buildup = GaugeMetric::Ailment(7, "KO".into());
        assert_eq!(gauge_segments(&samples, "m1", &buildup).iter().flatten().map(|x| x.0).collect::<Vec<_>>(),
            vec![0.0, 4.0]);
        assert_eq!(gauge_segments(&samples, "m2", &duration), vec![vec![(1.0, 40.0)]]);
        let coverage = gauge_coverage(&samples, "m1", &duration);
        assert_eq!((coverage.observations, coverage.usable, coverage.missing_metric,
            coverage.clock_boundaries, coverage.source_changes), (9, 6, 3, 1, 1));
        assert!(observed_counter_intervals(&samples, "m1", &duration).is_empty());
        assert_eq!(samples[3].ailments[0].duration, None, "missing duration is unknown");
    }

    #[test]
    fn duration_only_ailments_can_be_selected_without_fabricated_buildup() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[
            {"id":6,"name":"Exhaust","duration":0,"maxDuration":20},
            {"id":7,"name":"KO","buildup":20,"maxBuildup":100},
            {"id":8,"name":"Unknown","maxDuration":30}]}
        ]"#).unwrap();
        assert_eq!(available_metrics(&samples, "m1").iter().map(GaugeMetric::key).collect::<Vec<_>>(),
            vec!["ailment-duration:6", "ailment:7"]);
        assert_eq!(gauge_segments(&samples, "m1", &GaugeMetric::AilmentDuration(6, "Exhaust".into())),
            vec![vec![(0.0, 0.0)]]);
        assert!(gauge_segments(&samples, "m1", &GaugeMetric::AilmentDuration(8, "Unknown".into())).is_empty());
    }

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
    fn duplicate_partial_ailments_keep_valid_observed_proc_intervals() {
        let samples: Vec<GaugeSample> = serde_json::from_str(r#"[
          {"t":0,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","buildup":30,"maxBuildup":100,"procs":0}]},
          {"t":0,"monster":"m2","source":"local-unverified","ailments":[{"id":7,"name":"KO","buildup":30,"maxBuildup":100,"procs":9}]},
          {"t":2,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":5,"maxDuration":10},
            {"id":7,"name":"KO","procs":-2,"buildup":33,"maxBuildup":100},
            {"id":7,"name":"KO","buildup":50,"maxBuildup":100,"procs":1}]},
          {"t":2,"monster":"m2","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":1,"maxDuration":10},{"id":7,"name":"KO","procs":10}]},
          {"t":4,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":4,"maxDuration":10},{"id":7,"name":"KO","procs":3}]},
          {"t":6,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","duration":2,"maxDuration":10}]},
          {"t":8,"monster":"m1","source":"local-unverified","ailments":[{"id":7,"name":"KO","procs":4}]},
          {"t":10,"monster":"m1","source":"local-unverified","ailments":[
            {"id":7,"name":"KO","duration":2,"maxDuration":10},{"id":7,"name":"KO","procs":5}]},
          {"t":12,"monster":"m1","source":"local-unverified","clockBoundary":true,"ailments":[{"id":7,"name":"KO","procs":6}]},
          {"t":14,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":7}]},
          {"t":16,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":8}]},
          {"t":18,"monster":"m1","source":"unknown","ailments":[
            {"id":7,"name":"KO","procs":-1},{"id":7,"name":"KO","duration":2,"maxDuration":10}]},
          {"t":20,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":9}]},
          {"t":22,"monster":"m1","source":"unknown","ailments":[{"id":7,"name":"KO","procs":10}]}
        ]"#).unwrap();
        let ko = GaugeMetric::Ailment(7, "KO".into());
        let tuples = |monster: &str| observed_counter_intervals(&samples, monster, &ko).iter()
            .map(|change| (change.start_t, change.end_t, change.before, change.after, change.increase))
            .collect::<Vec<_>>();
        assert_eq!(tuples("m1"), vec![(0., 2., 0, 1, 1), (2., 4., 1, 3, 2),
            (8., 10., 4, 5, 1), (14., 16., 7, 8, 1), (20., 22., 9, 10, 1)]);
        assert_eq!(tuples("m2"), vec![(0., 2., 9, 10, 1)]);
        assert!(observed_counter_intervals(&samples, "m1", &GaugeMetric::AilmentDuration(7, "KO".into())).is_empty());
        assert!(observed_counter_intervals(&samples, "m1", &GaugeMetric::Ailment(8, "Other".into())).is_empty());
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
