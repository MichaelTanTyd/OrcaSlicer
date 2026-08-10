using System.Text.RegularExpressions;

namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// Standard (single-file) G-code → KRL converter
//   Reads G-code line-by-line, runs a state machine,
//   and writes KRL output through a buffered writer.
// ──────────────────────────────────────────

/// <summary>Converts a G-code file into a single KRL .SRC file</summary>
public static class StandardConverter
{
    /// <summary>
    /// Convert a G-code file to KRL format.
    /// Returns the number of output lines written.
    /// </summary>
    /// <param name="inputPath">Path to the .gcode input file</param>
    /// <param name="outputPath">Path to the .src output file</param>
    /// <param name="jobName">KRL program name (DEF block)</param>
    /// <param name="cfg">Start position configuration</param>
    /// <param name="progressCallback">Optional: called every 500K lines for progress reporting</param>
    public static int Convert(string inputPath, string outputPath, string jobName,
        StartConfig cfg, Action<int, int>? progressCallback = null)
    {
        CrashLogger.Log($"Reading input: {inputPath} ({MemReporter.FmtMem()})");

        // Validate input file
        FileInfo fi;
        try { fi = new FileInfo(inputPath); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot stat input: {ex.Message}"); }

        var sizeMb = Math.Round(fi.Length / 1024.0 / 1024.0);
        CrashLogger.Log($"Input size: {sizeMb} MB");

        if (fi.Length > 500 * 1024 * 1024)
            CrashLogger.Log($"WARNING: File is {sizeMb} MB — may run out of memory");

        // Read entire file (split mode handles very large files; standard mode assumes manageable size)
        string[] lines;
        try
        {
            lines = File.ReadAllLines(inputPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot read input file: {ex.Message}");
        }

        if (lines.Length == 0)
            throw new InvalidOperationException("Input file is empty");

        CrashLogger.Log($"File read complete ({MemReporter.FmtMem()})");
        CrashLogger.Log($"Starting conversion: {lines.Length} input lines → {outputPath}");

        using var writer = new KrlStreamWriter(outputPath);

        // ── Header & DEF ──
        var inputName = Path.GetFileName(inputPath);
        writer.WriteLines(KrlEmitter.EmitHeader(inputName));
        writer.WriteLine($"DEF {jobName}()");
        writer.WriteLines(KrlEmitter.EmitBanner());
        writer.WriteLine("");

        // ── Init Motion FOLD ──
        writer.WriteLines(KrlEmitter.EmitInitMotion(cfg));

        // ── Init PLC FOLD ──
        writer.WriteLines(KrlEmitter.EmitInitPlc(cfg));

        // ── Print Path FOLD (open) ──
        writer.WriteLine("   ;FOLD Print Path");
        writer.WriteLine("");

        // ── Extract print metadata from comments ──
        ExtractAndWritePrintInfo(lines, writer);

        // ── State machine ──
        var eState = new EStateTracker();
        int tIdx = 0;           // t[n] index, 1-10 cycling
        int layerCount = 0;
        int totalLayers = 0;
        double? prevZ = null;
        bool fanOn = false;
        bool inLayerFold = false;
        bool g92Emitted = false;

        // Orientation constants from start config
        var oriA = FormatHelper.Fmt2(cfg.Cart.A);
        var oriB = FormatHelper.Fmt2(cfg.Cart.B);
        var oriC = FormatHelper.Fmt2(cfg.Cart.C);

        // Count total layers for progress percentage
        foreach (var raw in lines)
        {
            if (Regex.IsMatch(raw, @"^;(LAYER_CHANGE|BEFORE_LAYER_CHANGE|AFTER_LAYER_CHANGE|Z:)", RegexOptions.IgnoreCase))
                totalLayers++;
        }

        // ── Increment t index, cycling 1-10 ──
        int NextT()
        {
            if (tIdx == 10)
                writer.WriteLine("         ;back to t[1] aviod advance");
            tIdx = (tIdx % 10) + 1;
            return tIdx;
        }

        // ── Close any open layer FOLD ──
        void CloseLayerFold()
        {
            if (inLayerFold)
            {
                writer.WriteLine("      ;ENDFOLD");
                writer.WriteLine("");
                inLayerFold = false;
            }
        }

        // ── Open a new layer FOLD ──
        void OpenLayerFold(int num, int? pct)
        {
            writer.WriteLine($"      ;FOLD Layer {num}");
            if (pct.HasValue)
                writer.WriteLine($"         ;Progress = {pct.Value}");
            inLayerFold = true;
        }

        // Regex for layer boundary detection (shared across loop iterations)
        var layerBoundaryRx = new Regex(@"^;(LAYER_CHANGE|BEFORE_LAYER_CHANGE|AFTER_LAYER_CHANGE)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var zMarkerRx = new Regex(@"^;Z:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var wipeStartRx = new Regex(@"^;WIPE_START", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var wipeEndRx = new Regex(@"^;WIPE_END", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        int lineNum = 0;
        int lastProgressLog = 0;

        foreach (var raw in lines)
        {
            lineNum++;

            // Progress logging every 500K lines
            if (progressCallback != null && lineNum - lastProgressLog >= 500_000)
            {
                progressCallback(lineNum, lines.Length);
                lastProgressLog = lineNum;
            }

            // Fast path: skip comments that aren't layer-related
            if (raw.StartsWith(';'))
            {
                if (layerBoundaryRx.IsMatch(raw) || zMarkerRx.IsMatch(raw) ||
                    wipeStartRx.IsMatch(raw) || wipeEndRx.IsMatch(raw) ||
                    raw.StartsWith(";HEIGHT:") || raw.StartsWith(";TYPE:"))
                {
                    // handled below
                }
                else
                {
                    continue;
                }
            }

            // ── Layer change detection ──
            if (layerBoundaryRx.IsMatch(raw))
            {
                CloseLayerFold();
                layerCount++;
                var pct = totalLayers > 0 ? (int?)Math.Round((double)layerCount / totalLayers * 100) : null;
                OpenLayerFold(layerCount, pct);
                writer.WriteLine("         PNT_G92_E0()");
                g92Emitted = true;
                continue;
            }

            // ── Z marker ──
            if (zMarkerRx.IsMatch(raw))
            {
                var zParts = raw.Split(':');
                if (zParts.Length >= 2 && double.TryParse(zParts[1].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var zVal))
                {
                    if (prevZ.HasValue && zVal > prevZ.Value + 0.001)
                        layerCount++;
                    prevZ = zVal;
                }
                continue;
            }

            // ── Wipe markers ──
            if (wipeStartRx.IsMatch(raw)) { writer.WriteLine("         ; WIPE_START"); continue; }
            if (wipeEndRx.IsMatch(raw)) { writer.WriteLine("         ; WIPE_END"); continue; }

            // ── Parse the G-code line ──
            var parsed = GCodeParser.Parse(raw);
            if (parsed is null) continue;

            var cmd = parsed.Value.Cmd;
            var p = parsed.Value.Params;
            double? GetParam(char key) => p.TryGetValue(key, out var v) ? v : null;

            // ── M82 / M83: extrusion mode ──
            if (cmd == "M82") { eState.SetRelative(false); continue; }
            if (cmd == "M83") { eState.SetRelative(true); continue; }

            // ── G92 E0: reset extruder ──
            if (cmd == "G92" && GetParam('E') is 0)
            {
                if (g92Emitted) { g92Emitted = false; continue; }
                eState.Reset();
                writer.WriteLine("         PNT_G92_E0()");
                continue;
            }

            // ── Fan control (M106 / M107) ──
            if (cmd == "M106")
            {
                var s = GetParam('S') ?? 255;
                if (s == 0)
                {
                    if (fanOn) writer.WriteLine("         PNT_FAN_OFF()");
                    fanOn = false;
                }
                else
                {
                    var pct = (int)Math.Round(s / 255.0 * 100);
                    writer.WriteLine($"         PNT_FAN_SET({pct})");
                    fanOn = true;
                }
                continue;
            }
            if (cmd == "M107")
            {
                if (fanOn) writer.WriteLine("         PNT_FAN_OFF()");
                fanOn = false;
                continue;
            }

            // ── G0: Travel move (no extrusion) ──
            if (cmd == "G0" || cmd == "G00")
            {
                var x = GetParam('X');
                var y = GetParam('Y');
                var z = GetParam('Z');
                var vel = GetParam('F') is { } f ? (int)Math.Round(f / 60.0) : 300;
                writer.WriteLine($"         PNT_G0({FormatHelper.SetVal(x, 0)}, {FormatHelper.SetVal(y, 0)}, {FormatHelper.SetVal(z, prevZ ?? 0)}, {oriA}, {oriB}, {oriC}, {vel})");
                continue;
            }

            // ── G1: Extrusion/print move ──
            if (cmd == "G1" || cmd == "G01")
            {
                var x = GetParam('X');
                var y = GetParam('Y');
                var z = GetParam('Z');
                var e = eState.ApplyE(GetParam('E'));
                var vel = GetParam('F') is { } f2 ? (int)Math.Round(f2 / 60.0) : 50;
                var idx = NextT();

                writer.WriteLine($"         t[{idx}]= PNT_LIN({FormatHelper.Fmt3(x, 0)}, {FormatHelper.Fmt3(y, 0)}, {FormatHelper.Fmt3(z, prevZ ?? 0)}, {oriA}, {oriB}, {oriC}, {FormatHelper.Fmt3(e)}, {FormatHelper.Fmt1(vel)})");
                writer.WriteLine($"         LIN t[{idx}] C_DIS");
                continue;
            }
        }

        // Close final layer fold
        CloseLayerFold();

        // ── Close Print Path FOLD ──
        writer.WriteLine("   ;ENDFOLD");
        writer.WriteLine("");

        // ── Finish FOLD ──
        writer.WriteLines(KrlEmitter.EmitFinish());

        // ── END ──
        writer.WriteLine("");
        writer.WriteLine("END");

        var result = writer.LineCount;
        CrashLogger.Log($"Conversion complete: {lines.Length} input lines → {result} output lines ({MemReporter.FmtMem()})");
        return result;
    }

    /// <summary>Extract print metadata from G-code header comments and write as KRL comments</summary>
    private static void ExtractAndWritePrintInfo(string[] lines, KrlStreamWriter writer)
    {
        string? printTime = null, printWeight = null, printSize = null;
        string? filamentType = null, nozzleDiam = null, layerHeight = null;

        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.StartsWith("; estimated printing time"))
                printTime = JoinAfterColon(t);
            else if (t.StartsWith("; total filament weight"))
                printWeight = JoinAfterColon(t);
            else if (t.StartsWith("; filament_type ="))
                filamentType = AfterFirst(t, '=');
            else if (t.StartsWith("; nozzle_diameter ="))
                nozzleDiam = AfterFirst(t, '=');
            else if (t.StartsWith("; layer_height ="))
                layerHeight = AfterFirst(t, '=');
            else if (t.StartsWith("; model_bounding_box"))
                printSize = JoinAfterColon(t);
        }

        if (printTime != null) writer.WriteLine($"      ;PrintTime = {printTime}");
        if (printWeight != null) writer.WriteLine($"      ;PrintWeight = {printWeight}");
        if (printSize != null) writer.WriteLine($"      ;PrintSize = (X,Y,Z) = ({printSize})");
        if (filamentType != null) writer.WriteLine($"      ;Material = {filamentType}");
        if (nozzleDiam != null) writer.WriteLine($"      ;Nozzle = {nozzleDiam}mm");
        if (layerHeight != null) writer.WriteLine($"      ;LayerH = {layerHeight}mm");
    }

    /// <summary>Extract text after the first colon, joining remaining parts with ':'</summary>
    private static string? JoinAfterColon(string text)
    {
        var idx = text.IndexOf(':');
        if (idx < 0 || idx + 1 >= text.Length) return null;
        return text[(idx + 1)..].Trim();
    }

    /// <summary>Extract text after the first occurrence of a delimiter</summary>
    private static string? AfterFirst(string text, char delimiter)
    {
        var idx = text.IndexOf(delimiter);
        if (idx < 0 || idx + 1 >= text.Length) return null;
        return text[(idx + 1)..].Trim();
    }
}
