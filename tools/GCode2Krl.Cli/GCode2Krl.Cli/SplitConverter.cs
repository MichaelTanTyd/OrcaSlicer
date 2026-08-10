using System.Text.RegularExpressions;

namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// Split-mode converter
//   For very large G-code files that would
//   produce KRL >20 MB. Scans layers, groups
//   into ~18 MB chunks, writes 1 main .SRC +
//   N sub-program .SRC files.
//
//   Two-pass approach:
//     Pass 1: scan layers, count command types, estimate output size
//     Pass 2: write sub-programs + main program
// ──────────────────────────────────────────

/// <summary>Handles split-mode conversion for large G-code files</summary>
public static class SplitConverter
{
    // Bytes-per-command estimates (matching JS constants)
    private const int EstG1Bytes = 140;       // t[N]= PNT_LIN(...) + LIN t[N] C_DIS
    private const int EstG0Bytes = 80;        // PNT_G0(...)
    private const int EstFanBytes = 40;       // PNT_FAN_ON/OFF/SET
    private const int EstG92Bytes = 35;       // PNT_G92_E0()
    private const int EstMiscBytes = 40;      // M82/M83/other
    private const int FoldOverhead = 100;     // ;FOLD Layer N + ;ENDFOLD per layer
    private const long TargetChunk = 18 * 1024 * 1024;   // ~18 MB target
    private const long MaxChunk = 20 * 1024 * 1024;      // 20 MB hard cap

    /// <summary>
    /// Convert a large G-code file into a main program + sub-program files.
    /// Falls back to standard single-file mode if no layer markers are found.
    /// </summary>
    public static void ConvertSplit(string inputPath, string outputDir, string jobName,
        StartConfig cfg, Action<int, int>? progressCallback = null)
    {
        CrashLogger.Log($"Split mode: reading {inputPath} ({MemReporter.FmtMem()})");

        // Validate input
        FileInfo fi;
        try { fi = new FileInfo(inputPath); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot stat input: {ex.Message}"); }

        var sizeMb = Math.Round(fi.Length / 1024.0 / 1024.0);
        CrashLogger.Log($"Input size: {sizeMb} MB");

        string[] lines;
        try { lines = File.ReadAllLines(inputPath); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot read input file: {ex.Message}"); }

        if (lines.Length == 0)
            throw new InvalidOperationException("Input file is empty");

        CrashLogger.Log($"File read complete ({MemReporter.FmtMem()}). Scanning layers...");

        // ── Pass 1: Scan layers ──
        var layerInfos = ScanLayers(lines);
        var regularLayers = layerInfos.Where(l => !l.IsPreamble).ToList();
        CrashLogger.Log($"Found {regularLayers.Count} layer(s) + {layerInfos.Count - regularLayers.Count} preamble section(s)");

        if (regularLayers.Count == 0)
        {
            CrashLogger.Log("WARNING: No layer markers found — falling back to single-file output");
            var fallbackPath = Path.Combine(outputDir, jobName + ".SRC");
            StandardConverter.Convert(inputPath, fallbackPath, jobName, cfg, progressCallback);
            return;
        }

        // Group into chunks
        var chunks = GroupLayers(layerInfos);
        CrashLogger.Log($"Grouped into {chunks.Length} chunk(s):");
        foreach (var chunk in chunks.Select((c, i) => new { Chunk = c, Index = i }))
        {
            var c = chunk.Chunk;
            var mb = (c.TotalBytes / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            var lc = c.Layers.Count(l => !l.IsPreamble);
            var range = c.FirstLayerIndex > 0
                ? $"Layers {c.FirstLayerIndex}–{c.LastLayerIndex}"
                : "Preamble";
            CrashLogger.Log($"  Chunk {chunk.Index + 1}: {range}, {lc} layer(s), ~{mb} MB");
        }

        // Ensure output directory
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // ── Pass 2: Write sub-programs ──
        CrashLogger.Log($"Generating sub-programs... ({MemReporter.FmtMem()})");
        var subNames = WriteSubPrograms(outputDir, jobName, chunks, Path.GetFileName(inputPath), lines, cfg);

        // Write main program
        CrashLogger.Log("Generating main program...");
        var targetMb = (int)Math.Round(TargetChunk / 1024.0 / 1024.0);
        var mainPath = Path.Combine(outputDir, jobName + ".SRC");
        using var mainWriter = new KrlStreamWriter(mainPath);
        KrlEmitter.EmitMainProgram(mainWriter, Path.GetFileName(inputPath), jobName,
            subNames, chunks, cfg, targetMb);

        var mainLines = mainWriter.LineCount;
        CrashLogger.Log($"Main program written: {mainPath} ({mainLines} lines)");
        CrashLogger.Log($"Split complete: 1 main + {chunks.Length} sub-program(s) → {outputDir}");
        CrashLogger.Log($"Memory: {MemReporter.FmtMem()}");
    }

    // ── Pass 1: Scan all layers, count commands, estimate output size ──

    private static List<LayerInfo> ScanLayers(string[] lines)
    {
        var layerInfos = new List<LayerInfo>();
        LayerInfo? currentLayer = null;
        var preambleDone = false;
        var layerBoundaryRx = new Regex(@"^;(LAYER_CHANGE|BEFORE_LAYER_CHANGE|AFTER_LAYER_CHANGE)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var zMarkerRx = new Regex(@"^;Z:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        for (var lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            var raw = lines[lineNum];
            var trimmed = raw.Trim();

            // Layer boundary
            if (layerBoundaryRx.IsMatch(trimmed))
            {
                if (currentLayer != null)
                {
                    currentLayer.EndLine = lineNum;
                    layerInfos.Add(currentLayer);
                }
                currentLayer = new LayerInfo
                {
                    StartLine = lineNum + 1,  // 1-based for JS compatibility
                    IsPreamble = false,
                    LayerIndex = layerInfos.Count
                };
                preambleDone = true;
                continue;
            }

            if (zMarkerRx.IsMatch(trimmed)) continue;

            // Skip other comments
            if (raw.StartsWith(';')) continue;

            // Pre-first-layer content → preamble
            if (!preambleDone && currentLayer == null)
            {
                currentLayer = new LayerInfo
                {
                    StartLine = 1,
                    IsPreamble = true,
                    LayerIndex = -1
                };
            }

            var parsed = GCodeParser.Parse(raw);
            if (parsed is null) continue;

            var cmd = parsed.Value.Cmd;

            if (cmd == "G0" || cmd == "G00") currentLayer!.G0Count++;
            else if (cmd == "G1" || cmd == "G01") currentLayer!.G1Count++;
            else if (cmd == "M106" || cmd == "M107") currentLayer!.FanCount++;
            else if (cmd == "G92")
            {
                if (parsed.Value.Params.ContainsKey('E'))
                    currentLayer!.G92Count++;
            }
            else if (cmd == "M82" || cmd == "M83") currentLayer!.MiscCount++;
        }

        // Push last layer
        if (currentLayer != null)
        {
            currentLayer.EndLine = lines.Length;
            layerInfos.Add(currentLayer);
        }

        // Calculate estimated bytes
        foreach (var li in layerInfos)
        {
            li.EstimatedBytes =
                li.G1Count * EstG1Bytes +
                li.G0Count * EstG0Bytes +
                li.FanCount * EstFanBytes +
                li.G92Count * EstG92Bytes +
                li.MiscCount * EstMiscBytes +
                (li.IsPreamble ? 0 : FoldOverhead);
        }

        return layerInfos;
    }

    // ── Group layers into ~18 MB chunks ──

    private static ChunkInfo[] GroupLayers(List<LayerInfo> layerInfos)
    {
        var preamble = layerInfos.FirstOrDefault(l => l.IsPreamble);
        var layers = layerInfos.Where(l => !l.IsPreamble).ToList();

        var chunks = new List<ChunkInfo>();
        var currentChunk = new ChunkInfo();

        // Preamble always goes into the first chunk
        if (preamble != null && (preamble.G1Count > 0 || preamble.G0Count > 0 || preamble.FanCount > 0))
        {
            currentChunk.Layers.Add(preamble);
            currentChunk.TotalBytes += preamble.EstimatedBytes;
        }

        foreach (var layer in layers)
        {
            var candidate = currentChunk.TotalBytes + layer.EstimatedBytes;

            if (currentChunk.Layers.Count == 0 || candidate <= TargetChunk)
            {
                // Fits within target
                currentChunk.Layers.Add(layer);
                currentChunk.TotalBytes += layer.EstimatedBytes;
                if (currentChunk.FirstLayerIndex == 0)
                    currentChunk.FirstLayerIndex = layer.LayerIndex;
                currentChunk.LastLayerIndex = layer.LayerIndex;
            }
            else if (candidate <= MaxChunk)
            {
                // Slightly over target but within max
                currentChunk.Layers.Add(layer);
                currentChunk.TotalBytes += layer.EstimatedBytes;
                currentChunk.LastLayerIndex = layer.LayerIndex;
            }
            else
            {
                // Exceeds max: finalize current, start new
                chunks.Add(currentChunk);
                currentChunk = new ChunkInfo
                {
                    Layers = { layer },
                    TotalBytes = layer.EstimatedBytes,
                    FirstLayerIndex = layer.LayerIndex,
                    LastLayerIndex = layer.LayerIndex
                };

                if (layer.EstimatedBytes > MaxChunk)
                {
                    CrashLogger.Log($"WARNING: Layer {layer.LayerIndex} alone is ~{Math.Round(layer.EstimatedBytes / 1024.0 / 1024.0)}MB — exceeds {Math.Round(MaxChunk / 1024.0 / 1024.0)}MB max");
                }
            }
        }

        if (currentChunk.Layers.Count > 0)
            chunks.Add(currentChunk);

        return chunks.ToArray();
    }

    // ── Pass 2: Write sub-programs ──

    private static string[] WriteSubPrograms(string outputDir, string jobName, ChunkInfo[] chunks,
        string inputName, string[] lines, StartConfig cfg)
    {
        var oriA = FormatHelper.Fmt2(cfg.Cart.A);
        var oriB = FormatHelper.Fmt2(cfg.Cart.B);
        var oriC = FormatHelper.Fmt2(cfg.Cart.C);

        // Build layer→chunk map
        var layerChunkMap = new Dictionary<int, int>();
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            foreach (var layer in chunks[ci].Layers)
            {
                if (!layer.IsPreamble)
                    layerChunkMap[layer.LayerIndex] = ci;
            }
        }

        const int preambleChunk = 0;

        // Create sub-program names & writers
        var subNames = chunks.Select((_, i) => jobName + "_" + (i + 1)).ToArray();
        var writers = new KrlStreamWriter[chunks.Length];
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            var sp = Path.Combine(outputDir, subNames[ci] + ".SRC");
            writers[ci] = new KrlStreamWriter(sp);

            // Sub-program header
            writers[ci].WriteLines(KrlEmitter.EmitHeader(inputName));
            writers[ci].WriteLine($"DEF {subNames[ci]}()");
            writers[ci].WriteLine("");
            writers[ci].WriteLine("  ;═══════════════════════════════════════════════════════════");
            writers[ci].WriteLine($"  ;{jobName} Sub-program #{ci + 1}");

            var c = chunks[ci];
            var mb = (c.TotalBytes / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            if (!c.Layers.All(l => l.IsPreamble))
                writers[ci].WriteLine($"  ;Layers {c.FirstLayerIndex}–{c.LastLayerIndex}, ~{mb} MB");
            else
                writers[ci].WriteLine($"  ;Preamble only, ~{mb} MB");

            writers[ci].WriteLine("  ;═══════════════════════════════════════════════════════════");
            writers[ci].WriteLine("");
            writers[ci].WriteLine("  DECL E6POS t[10]");
            writers[ci].WriteLine("");
        }

        // ── State machine ──
        var eState = new EStateTracker();
        int currentChunkIdx = -1;
        KrlStreamWriter? currentWriter = null;
        int layerCount = 0;
        int totalLayers = 0;
        double? prevZ = null;
        bool fanOn = false;
        bool inLayerFold = false;
        bool g92Emitted = false;
        int tIdx = 0;
        bool preambleActive = true;

        // Ensure we're writing to the correct chunk writer
        void EnsureWriter(int chunkIdx)
        {
            if (chunkIdx != currentChunkIdx)
            {
                currentChunkIdx = chunkIdx;
                currentWriter = writers[chunkIdx];
            }
        }

        int NextT()
        {
            if (tIdx == 10)
                currentWriter?.WriteLine("         ;back to t[1] avoid advance");
            tIdx = (tIdx % 10) + 1;
            return tIdx;
        }

        void CloseLayerFold()
        {
            if (inLayerFold && currentWriter != null)
            {
                currentWriter.WriteLine("      ;ENDFOLD");
                currentWriter.WriteLine("");
                inLayerFold = false;
            }
        }

        void OpenLayerFold(int num, int? pct)
        {
            if (currentWriter != null)
            {
                currentWriter.WriteLine($"      ;FOLD Layer {num}");
                if (pct.HasValue)
                    currentWriter.WriteLine($"         ;Progress = {pct.Value}");
                inLayerFold = true;
            }
        }

        // Count total layers
        foreach (var raw in lines)
        {
            if (Regex.IsMatch(raw, @"^;(LAYER_CHANGE|BEFORE_LAYER_CHANGE|AFTER_LAYER_CHANGE|Z:)", RegexOptions.IgnoreCase))
                totalLayers++;
        }

        // Regex patterns
        var layerBoundaryRx = new Regex(@"^;(LAYER_CHANGE|BEFORE_LAYER_CHANGE|AFTER_LAYER_CHANGE)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var zMarkerRx = new Regex(@"^;Z:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var wipeStartRx = new Regex(@"^;WIPE_START", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var wipeEndRx = new Regex(@"^;WIPE_END", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        for (var lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            var raw = lines[lineNum];

            // Pass-through layer-related comments only
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

            // ── Layer boundary → switch chunk ──
            if (layerBoundaryRx.IsMatch(raw))
            {
                preambleActive = false;
                CloseLayerFold();
                layerCount++;

                if (layerChunkMap.TryGetValue(layerCount, out var ci))
                    EnsureWriter(ci);
                else if (currentChunkIdx < 0)
                    EnsureWriter(preambleChunk);

                var pct = totalLayers > 0 ? (int?)Math.Round((double)layerCount / totalLayers * 100) : null;
                OpenLayerFold(layerCount, pct);
                currentWriter?.WriteLine("         PNT_G92_E0()");
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
            if (wipeStartRx.IsMatch(raw)) { currentWriter?.WriteLine("         ; WIPE_START"); continue; }
            if (wipeEndRx.IsMatch(raw)) { currentWriter?.WriteLine("         ; WIPE_END"); continue; }

            // Ensure preamble has a writer
            if (preambleActive && currentChunkIdx < 0)
                EnsureWriter(preambleChunk);

            var parsed = GCodeParser.Parse(raw);
            if (parsed is null) continue;

            var cmd = parsed.Value.Cmd;
            var prms = parsed.Value.Params;
            double? G(char key) => prms.TryGetValue(key, out var v) ? v : null;

            // M82/M83
            if (cmd == "M82") { eState.SetRelative(false); continue; }
            if (cmd == "M83") { eState.SetRelative(true); continue; }

            // G92 E0
            if (cmd == "G92" && G('E') is 0)
            {
                if (g92Emitted) { g92Emitted = false; continue; }
                eState.Reset();
                currentWriter?.WriteLine("         PNT_G92_E0()");
                continue;
            }

            // Fan
            if (cmd == "M106")
            {
                if (currentWriter == null) continue;
                var s = G('S') ?? 255;
                if (s == 0)
                {
                    if (fanOn) currentWriter.WriteLine("         PNT_FAN_OFF()");
                    fanOn = false;
                }
                else
                {
                    var pct = (int)Math.Round(s / 255.0 * 100);
                    currentWriter.WriteLine($"         PNT_FAN_SET({pct})");
                    fanOn = true;
                }
                continue;
            }
            if (cmd == "M107")
            {
                if (fanOn) currentWriter?.WriteLine("         PNT_FAN_OFF()");
                fanOn = false;
                continue;
            }

            // G0
            if (cmd == "G0" || cmd == "G00")
            {
                if (currentWriter == null) continue;
                var x = G('X'); var y = G('Y'); var z = G('Z');
                var vel = G('F') is { } f ? (int)Math.Round(f / 60.0) : 300;
                currentWriter.WriteLine($"         PNT_G0({FormatHelper.SetVal(x, 0)}, {FormatHelper.SetVal(y, 0)}, {FormatHelper.SetVal(z, prevZ ?? 0)}, {oriA}, {oriB}, {oriC}, {vel})");
                continue;
            }

            // G1
            if (cmd == "G1" || cmd == "G01")
            {
                if (currentWriter == null) continue;
                var x = G('X'); var y = G('Y'); var z = G('Z');
                var e = eState.ApplyE(G('E'));
                var vel = G('F') is { } f2 ? (int)Math.Round(f2 / 60.0) : 50;
                var idx = NextT();

                currentWriter.WriteLine($"         t[{idx}]= PNT_LIN({FormatHelper.Fmt3(x, 0)}, {FormatHelper.Fmt3(y, 0)}, {FormatHelper.Fmt3(z, prevZ ?? 0)}, {oriA}, {oriB}, {oriC}, {FormatHelper.Fmt3(e)}, {FormatHelper.Fmt1(vel)})");
                currentWriter.WriteLine($"         LIN t[{idx}] C_DIS");
            }
        }

        // Close final layer fold
        CloseLayerFold();

        // Close all sub-programs
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            writers[ci].WriteLine("");
            writers[ci].WriteLine("END");
            var lc = writers[ci].LineCount;
            writers[ci].Dispose();
            CrashLogger.Log($"Sub-program #{ci + 1} written: {subNames[ci]}.SRC ({lc} lines)");
        }

        return subNames;
    }
}
