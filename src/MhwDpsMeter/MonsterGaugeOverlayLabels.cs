using System.Globalization;
using System.Text;

namespace MhwDpsMeter;

/// <summary>Presentation-only bounded names. The full original name remains
/// available to sampling, selection, stable ImGui IDs, and persisted logs.</summary>
internal static class MonsterGaugeOverlayLabels
{
    public const int MaxNameTextElements = 32;

    public static string Display(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown sampled monster";

        // Enumerate grapheme clusters, not UTF-16 chars: combining marks
        // and surrogate pairs must not be cut in half.
        var enumerator = StringInfo.GetTextElementEnumerator(name);
        var result = new StringBuilder();
        var count = 0;
        var pendingSpace = false;
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (string.IsNullOrWhiteSpace(element))
            {
                if (result.Length > 0)
                    pendingSpace = true;
                continue;
            }

            if (count == MaxNameTextElements)
                return result.ToString() + "…";

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(element);
            count++;
        }
        return result.Length == 0 ? "Unknown sampled monster" : result.ToString();
    }
}
