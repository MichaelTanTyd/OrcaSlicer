using System.Globalization;

namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// Numeric formatting helpers
//   fmt1 → 1 decimal place
//   fmt2 → 2 decimal places
//   fmt3 → 3 decimal places
//   setVal → default-value-aware number output
// ──────────────────────────────────────────

/// <summary>Numeric formatting utilities matching JS fmt1/fmt2/fmt3/setVal semantics</summary>
public static class FormatHelper
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Format to 1 decimal place, with fallback default</summary>
    public static string Fmt1(double? v, double def = 0)
        => (v ?? def).ToString("F1", Inv);

    /// <summary>Format to 2 decimal places, with fallback default</summary>
    public static string Fmt2(double? v, double def = 0)
        => (v ?? def).ToString("F2", Inv);

    /// <summary>Format to 3 decimal places, with fallback default</summary>
    public static string Fmt3(double? v, double def = 0)
        => (v ?? def).ToString("F3", Inv);

    /// <summary>
    /// Value-or-previous output. If v is null, returns pre; otherwise v.
    /// Used for coordinate fields that may be absent (undefined) in G-code.
    /// </summary>
    public static string SetVal(double? v, double? pre)
    {
        if (v is null) return (pre ?? 0).ToString(Inv);
        return v.Value.ToString(Inv);
    }
}

// ──────────────────────────────────────────
// E-axis (extruder) state tracker
//   Tracks absolute position, handles M82/M83 relative mode,
//   and G92 E0 reset.
// ──────────────────────────────────────────

/// <summary>Tracks extruder absolute position across G-code lines</summary>
public sealed class EStateTracker
{
    private double _absolute;
    private bool _relative;

    public EStateTracker()
    {
        _absolute = 0;
        _relative = false;
    }

    /// <summary>Set relative (M83) or absolute (M82) extrusion mode</summary>
    public void SetRelative(bool v) => _relative = v;

    /// <summary>Reset absolute E to zero (G92 E0)</summary>
    public void Reset() => _absolute = 0;

    /// <summary>
    /// Apply an E value from a G-code command.
    /// In relative mode, accumulates; in absolute mode, replaces.
    /// Returns the new absolute E position.
    /// </summary>
    public double ApplyE(double? e)
    {
        if (e is null) return _absolute;
        if (_relative)
            _absolute += e.Value;
        else
            _absolute = e.Value;
        return _absolute;
    }
}

// ──────────────────────────────────────────
// Crash-resilience logging
//   Writes to stderr + optional gcode2krl_debug.log
// ──────────────────────────────────────────

/// <summary>Simple debug logger — writes to stderr and optionally a log file</summary>
public static class CrashLogger
{
    private static StreamWriter? _logStream;
    private static readonly object _lock = new();

    /// <summary>Open the debug log file in the output directory</summary>
    public static void SetupLog(string outputPath)
    {
        try
        {
            var logDir = Path.GetDirectoryName(outputPath) ?? ".";
            var logPath = Path.Combine(logDir, "gcode2krl_debug.log");
            _logStream = new StreamWriter(logPath, append: true) { AutoFlush = true };
        }
        catch
        {
            // Best-effort: if we can't open the log, just use stderr
        }
    }

    /// <summary>Write a timestamped message to stderr and the log file</summary>
    public static void Log(string message)
    {
        var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var line = $"[{ts}] {message}";
        Console.Error.WriteLine(line);

        lock (_lock)
        {
            try { _logStream?.WriteLine(line); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Log a fatal error and close the log</summary>
    public static void Fatal(string message)
    {
        var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var line = $"[FATAL] [{ts}] {message}";
        Console.Error.WriteLine(line);

        lock (_lock)
        {
            try
            {
                _logStream?.WriteLine(line);
                _logStream?.Close();
                _logStream = null;
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Close the log file cleanly</summary>
    public static void CloseLog()
    {
        lock (_lock)
        {
            try { _logStream?.Close(); }
            catch { /* best-effort */ }
            _logStream = null;
        }
    }
}

// ──────────────────────────────────────────
// Memory reporting helper (approximate GC stats for progress output)
// ──────────────────────────────────────────

/// <summary>Memory usage reporting (approximate, for progress logs)</summary>
public static class MemReporter
{
    /// <summary>Return a short string like "42MB / 128MB"</summary>
    public static string FmtMem()
    {
        var total = GC.GetTotalMemory(forceFullCollection: false);
        return $"{total / 1024 / 1024}MB";
    }
}
