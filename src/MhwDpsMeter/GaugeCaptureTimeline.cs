namespace MhwDpsMeter;

/// <summary>
/// Normalizes sampled gauge timestamps in capture order across native quest
/// clock resets and quest/local timer-source changes. It does not alter existing
/// hit or quest-award timestamps; logs preserve rawHuntT and mark adjustments.
/// </summary>
internal sealed class GaugeCaptureTimeline
{
    private double? _lastMonotonic;
    private string _lastSource = "";
    private float _lastT;
    private float _offset;

    public int Discontinuities { get; private set; }

    public void Reset()
    {
        _lastMonotonic = null;
        _lastSource = "";
        _lastT = 0;
        _offset = 0;
        Discontinuities = 0;
    }

    public (float T, bool Adjusted, bool Boundary) Advance(
        float rawHuntT, double monotonicSeconds, string clockSource)
    {
        if (!float.IsFinite(rawHuntT) || rawHuntT < 0)
            rawHuntT = 0;
        if (!double.IsFinite(monotonicSeconds))
            monotonicSeconds = _lastMonotonic ?? 0;

        var normalized = rawHuntT + _offset;
        var boundary = false;
        if (_lastMonotonic is double lastMono)
        {
            var delta = (float)Math.Clamp(monotonicSeconds - lastMono, 0, 60_000);
            var sourceChanged = !string.Equals(_lastSource, clockSource, StringComparison.Ordinal);
            // A source switch, a backwards reset or a sudden forward discontinuity
            // must preserve capture order without presenting jump-prone quest t.
            if (sourceChanged || normalized < _lastT - 0.01f
                || normalized > _lastT + delta + 5f)
            {
                normalized = _lastT + delta;
                _offset = normalized - rawHuntT;
                Discontinuities++;
                boundary = true;
            }
            normalized = Math.Max(_lastT, normalized);
        }

        _lastMonotonic = monotonicSeconds;
        _lastSource = clockSource;
        _lastT = normalized;
        // Adjusted is a persistent offset; Boundary is one discontinuity event.
        return (normalized, Math.Abs(_offset) > 0.001f, boundary);
    }
}
