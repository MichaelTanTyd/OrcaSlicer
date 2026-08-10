using System.Text.RegularExpressions;

namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// G-code line parser
//   Splits "G1 X100.5 Y200.3 E0.123 F600" into
//   { Cmd="G1", Params={ X=100.5, Y=200.3, E=0.123, F=600 } }
// ──────────────────────────────────────────

/// <summary>Parses a single G-code line into a structured command</summary>
public static partial class GCodeParser
{
    // Match parameter tokens: a letter followed by a number (e.g. X123.45, E0.01, F600)
    // Supports optional leading minus sign, integer or decimal
    [GeneratedRegex(@"^([A-Za-z])(-?\d+\.?\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ParamRegex();

    /// <summary>
    /// Parse a G-code line. Strips inline comment (after ';'), then splits
    /// into a command word and key-value parameters.
    /// Returns null for empty/comment-only lines.
    /// </summary>
    public static GCodeCommand? Parse(string line)
    {
        // Strip inline comment: everything after first ';'
        var semiIdx = line.IndexOf(';');
        var trimmed = (semiIdx >= 0 ? line[..semiIdx] : line).Trim();
        if (trimmed.Length == 0) return null;

        // Split on whitespace
        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var cmd = parts[0].ToUpperInvariant();
        var paramRx = ParamRegex();
        var parameters = new Dictionary<char, double>();

        for (var i = 1; i < parts.Length; i++)
        {
            var m = paramRx.Match(parts[i]);
            if (!m.Success) continue;

            var key = char.ToUpperInvariant(m.Groups[1].Value[0]);
            if (double.TryParse(m.Groups[2].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var val))
            {
                parameters[key] = val;
            }
        }

        return new GCodeCommand(cmd, parameters);
    }
}
