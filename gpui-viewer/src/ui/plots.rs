//! Custom plots on top of gpui-component's `Plot` primitives: several line series on
//! shared axes plus dashed vertical event markers. `LineChart` only draws one series,
//! and a hunt has up to four hunters.

use gpui_kit::component::{
    ActiveTheme as _,
    plot::{AXIS_GAP, AxisText, Grid, IntoPlot, Plot, PlotAxis, scale::{Scale, ScaleLinear}, shape::Line},
};
use gpui_kit::*;

#[derive(Clone)]
pub struct Series {
    pub label: SharedString,
    pub color: Hsla,
    pub points: Vec<(f32, f32)>,
}

#[derive(Clone)]
pub struct Marker {
    pub t: f32,
    pub color: Hsla,
}

#[derive(IntoPlot)]
pub struct LinesPlot {
    series: Vec<Series>,
    markers: Vec<Marker>,
    x_max: f32,
    y_max: f32,
    x_unit: &'static str,
}

impl LinesPlot {
    pub fn new(series: Vec<Series>, markers: Vec<Marker>) -> Self {
        let x_max = series
            .iter()
            .flat_map(|s| s.points.iter().map(|p| p.0))
            .chain(markers.iter().map(|m| m.t))
            .fold(0.0f32, f32::max)
            .max(1.0);
        let y_max = series.iter().flat_map(|s| s.points.iter().map(|p| p.1)).fold(0.0f32, f32::max).max(1.0);
        Self { series, markers, x_max, y_max: nice_ceiling(y_max), x_unit: "s" }
    }

}

/// Rounds up to 1, 2 or 5 times a power of ten so the top grid line is a round number.
fn nice_ceiling(v: f32) -> f32 {
    if v <= 0.0 {
        return 1.0;
    }
    let exp = v.log10().floor();
    let base = 10f32.powf(exp);
    let m = v / base;
    let nice = if m <= 1.0 { 1.0 } else if m <= 2.0 { 2.0 } else if m <= 5.0 { 5.0 } else { 10.0 };
    nice * base
}

fn short_number(v: f32) -> String {
    if v >= 1_000_000.0 {
        format!("{:.1}M", v / 1_000_000.0)
    } else if v >= 10_000.0 {
        format!("{:.0}k", v / 1000.0)
    } else if v >= 1000.0 {
        format!("{:.1}k", v / 1000.0)
    } else {
        format!("{v:.0}")
    }
}

impl Plot for LinesPlot {
    fn paint(&mut self, bounds: Bounds<Pixels>, window: &mut Window, cx: &mut App) {
        const LEFT_GUTTER: f32 = 44.0;
        let width = bounds.size.width.as_f32();
        let height = bounds.size.height.as_f32() - AXIS_GAP;
        if width <= LEFT_GUTTER + 10.0 || height <= 10.0 {
            return;
        }

        let x = ScaleLinear::new(vec![0.0f64, self.x_max as f64], vec![LEFT_GUTTER, width]);
        let y = ScaleLinear::new(vec![0.0f64, self.y_max as f64], vec![height, 6.0]);

        // Grid + y labels at 4 even steps.
        let y_ticks: Vec<f32> = (0..=4).map(|i| self.y_max * i as f32 / 4.0).collect();
        Grid::new()
            .y(y_ticks.iter().filter_map(|v| y.tick(&(*v as f64))).map(px).collect())
            .stroke(cx.theme().border)
            .dash_array(&[px(4.), px(2.)])
            .paint(&bounds, window);

        // Event markers as dashed vertical lines, one Grid per marker (own colour).
        for marker in &self.markers {
            if let Some(tick) = x.tick(&(marker.t as f64)) {
                Grid::new().x(vec![px(tick)]).stroke(marker.color).dash_array(&[px(3.), px(3.)]).paint(&bounds, window);
            }
        }

        let muted = cx.theme().muted_foreground;
        let x_labels = (0..=5).filter_map(|i| {
            let t = self.x_max * i as f32 / 5.0;
            x.tick(&(t as f64)).map(|tick| {
                let align = if i == 0 { TextAlign::Left } else if i == 5 { TextAlign::Right } else { TextAlign::Center };
                AxisText::new(format!("{}{}", t.round(), self.x_unit), tick, muted).align(align)
            })
        });
        let y_labels = y_ticks.iter().filter_map(|v| y.tick(&(*v as f64)).map(|tick| AxisText::new(short_number(*v), tick, muted)));
        PlotAxis::new()
            .x(height)
            .x_label(x_labels)
            .y(LEFT_GUTTER)
            .y_label(y_labels)
            .stroke(cx.theme().border)
            .paint(&bounds, window, cx);

        for series in &self.series {
            let (xs, ys) = (x.clone(), y.clone());
            Line::new()
                .data(series.points.iter().copied())
                .x(move |p: &(f32, f32)| xs.tick(&(p.0 as f64)))
                .y(move |p: &(f32, f32)| ys.tick(&(p.1 as f64)))
                .stroke(series.color)
                .stroke_width(px(2.))
                .paint(&bounds, window);
        }
    }
}

/// Colour swatches + labels to show under a plot.
pub fn legend(series: &[Series], cx: &App) -> impl IntoElement {
    let mut row = div().flex().flex_wrap().gap_3().text_xs().text_color(cx.theme().muted_foreground);
    for s in series {
        row = row.child(
            div()
                .flex()
                .items_center()
                .gap_1()
                .child(div().size_2p5().rounded_full().bg(s.color))
                .child(s.label.clone()),
        );
    }
    row
}

/// Party HUD colours by slot, matching the overlay.
pub fn slot_color(slot: usize) -> Hsla {
    let (r, g, b) = match slot {
        0 => (0xff, 0x9e, 0x2e),
        1 => (0x61, 0xdb, 0x6b),
        2 => (0x52, 0xb8, 0xff),
        _ => (0xf2, 0x6b, 0xb8),
    };
    rgb(((r as u32) << 16) | ((g as u32) << 8) | b as u32).into()
}
