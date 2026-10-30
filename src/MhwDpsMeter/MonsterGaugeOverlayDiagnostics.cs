namespace MhwDpsMeter;

/// <summary>Aligns the F9 verbose gauge-status row with the availability
/// calculated after processing the checkbox in the same ImGui frame.
/// Other diagnostics retain their existing source and contents.</summary>
internal static class MonsterGaugeOverlayDiagnostics
{
    private const string GaugeLinePrefix = "\nMonster gauges: ";

    public static string AlignGaugeLine(string diagnostics,
        MonsterGaugeOverlayAvailability availability)
    {
        // BuildDiagnosticsText emits the gauge line after a newline.
        // Only replace the owned line, not a coincidental occurrence in a
        // free-form status, player name or other diagnostic value.
        var lineStart = diagnostics.IndexOf(GaugeLinePrefix, StringComparison.Ordinal);
        if (lineStart < 0)
            return diagnostics;

        var valueStart = lineStart + GaugeLinePrefix.Length;
        var lineEnd = diagnostics.IndexOf('\n', valueStart);
        if (lineEnd < 0)
            lineEnd = diagnostics.Length;

        return diagnostics[..valueStart] + availability.StatusText + diagnostics[lineEnd..];
    }
}
