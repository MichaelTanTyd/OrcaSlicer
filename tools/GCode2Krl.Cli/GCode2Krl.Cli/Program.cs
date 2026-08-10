using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// CLI entry point
//   Parses args, loads config, orchestrates
//   standard or split conversion.
//
// Usage:
//   OrcaSlicer mode:  gcode2krl <gcode_file>
//   Standalone mode:  gcode2krl <input.gcode> <output.src> [--name=JOB]
//   Split mode:       gcode2krl <input.gcode> <output_dir> --split [--name=JOB]
//   With config:      gcode2krl ... --start-config=config.json
// ──────────────────────────────────────────

public static class Program
{
  private static StartConfig s_config = new();

  public static int Main(string[] args)
  {
    // ── Setup logging ──
    CrashLogger.SetupLog();

    // Global exception handler — write crash log before exit
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
          var ex = e.ExceptionObject as Exception;
          CrashLogger.Fatal($"CRASH (unhandled): {ex?.Message}\n{ex?.StackTrace ?? "(no stack)"}");
          Environment.Exit(1);
        };

    try
    {
      return Run(args);
    }
    catch (Exception ex)
    {
      CrashLogger.Fatal($"FATAL: {ex.Message}\n{ex.StackTrace ?? "(no stack)"}");
      Console.Error.WriteLine($"ERROR: {ex.Message}");
      return 1;
    }
  }

  private static int Run(string[] args)
  {
    // ── Parse arguments ──
    bool splitMode = false;
    string? configPath = null;
    string? jobName = null;
    string? mirrorDir = null;
    var cleanArgs = new List<string>();

    foreach (var arg in args)
    {
      if (arg == "--split")
        splitMode = true;
      else if (arg.StartsWith("--start-config="))
        configPath = arg["--start-config=".Length..];
      else if (arg.StartsWith("--name="))
        jobName = arg["--name=".Length..];
      else if (arg.StartsWith("--output-dir="))
        mirrorDir = arg["--output-dir=".Length..];
      else
        cleanArgs.Add(arg);

    }

    // ── Validate arguments ──
    if (cleanArgs.Count < 1)
    {
      PrintUsage();
      return 1;
    }

    var inputPath = cleanArgs[0];

    // Determine output path and detect OrcaSlicer mode (single-arg invocation)
    string outputPath;
    var isOrcaSlicerMode = cleanArgs.Count == 1;
    if (splitMode)
    {
      outputPath = cleanArgs.Count >= 2 ? cleanArgs[1] : Path.GetDirectoryName(inputPath)!;
    }
    else
    {
      outputPath = cleanArgs.Count >= 2 ? cleanArgs[1] : inputPath;
    }

    // ── Load start config ──
    if (configPath != null)
      LoadStartConfig(configPath);

    // ── Config-driven overrides (CLI flags always win) ──
    // If the user didn't pass --split on the command line,
    // check the JSON config's "split" field to decide.
    if (!splitMode) splitMode = s_config.Split;

    // If the user didn't pass --output-dir, use config's splitOutputDir
    mirrorDir ??= s_config.SplitOutputDir;



    var version = splitMode ? "v4.0 (split mode)" : "v3.2";
    CrashLogger.Log($"gcode2krl {version} starting");
    CrashLogger.Log($"Args: {string.Join(" ", cleanArgs)}{(splitMode ? " --split" : "")}");

    // Validate input file exists
    if (!File.Exists(inputPath))
    {
      var msg = $"ERROR: File not found: {inputPath}";
      CrashLogger.Log(msg);
      Console.Error.WriteLine(msg);
      return 1;
    }

    // Determine job name
    if (string.IsNullOrEmpty(jobName))
    {
      if (splitMode)
        jobName = SanitizeJobName(Path.GetFileNameWithoutExtension(inputPath));
      else
        jobName = SanitizeJobName(Path.GetFileNameWithoutExtension(outputPath));

      if (string.IsNullOrEmpty(jobName) || char.IsDigit(jobName[0]))
        jobName = "P_" + jobName;
    }

    // ── Execute ──
    try
    {
      if (splitMode)
      {
        RunSplit(inputPath, outputPath, jobName, isOrcaSlicerMode, mirrorDir);
      }
      else
      {
        RunStandard(inputPath, outputPath, jobName);
      }

      CrashLogger.CloseLog();
      // Console.ReadKey();
      return 0;
    }
    catch (Exception ex)
    {
      CrashLogger.Log($"FATAL: {ex.Message}");
      CrashLogger.Log($"Stack: {ex.StackTrace ?? "(no stack)"}");
      Console.Error.WriteLine($"ERROR: Conversion failed: {ex.Message}");
      if (ex.StackTrace != null)
        Console.Error.WriteLine(ex.StackTrace);
      return 1;
    }
  }

  // ── Standard mode ──

  private static void RunStandard(string inputPath, string outputPath, string jobName)
  {
    var inputBase = Path.GetFileName(inputPath);
    var outputBase = Path.GetFileName(outputPath);
    CrashLogger.Log($"Converting {inputBase} → {outputBase} [{jobName}]");
    Console.Error.WriteLine($"gcode2krl v3.2: Converting {inputBase}...");

    var lineCount = StandardConverter.Convert(inputPath, outputPath, jobName, s_config,
        (current, total) =>
        {
          var pct = total > 0 ? (int)Math.Round((double)current / total * 100) : 0;
          Console.Error.WriteLine($"  Progress: {pct}% ({current}/{total} lines, {MemReporter.FmtMem()})");
        });

    Console.Error.WriteLine($"Generated: {outputBase} ({lineCount} lines)");
    Console.Error.WriteLine("gcode2krl: Done! Rename to .SRC before loading to KUKA controller.");
    CrashLogger.Log($"Success: {lineCount} lines written");
  }

  // ── Split mode ──

  /// <summary>
  /// Run split-mode conversion.
  /// When isOrcaSlicerMode is true (single-arg invocation), the main .SRC content
  /// is written back to the input file so OrcaSlicer can output the converted result.
  /// </summary>
  private static void RunSplit(string inputPath, string outputDir, string jobName, bool isOrcaSlicerMode, string? mirrorDir)
  {
    var inputBase = Path.GetFileName(inputPath);
    var targetMb = (int)Math.Round(18.0);
    var maxMb = 20;

    Console.Error.WriteLine($"gcode2krl v4.0 (split): Converting {inputBase} → {outputDir}\\");
    Console.Error.WriteLine($"  Target chunk: ~{targetMb} MB, max: {maxMb} MB");

    SplitConverter.ConvertSplit(inputPath, outputDir, jobName, s_config,
        (current, total) =>
        {
          var pct = total > 0 ? (int)Math.Round((double)current / total * 100) : 0;
          Console.Error.WriteLine($"  Progress: {pct}% ({current}/{total} lines, {MemReporter.FmtMem()})");
        });

    // ── OrcaSlicer mode: write main .SRC back to input temp file ──
    // OrcaSlicer invokes the post-processor with a single argument (the temp .pp file).
    // It expects the post-processor to modify this file in-place; whatever content
    // the file has after processing is what OrcaSlicer outputs to the user's directory.
    //
    // For split mode, sub-programs cannot follow because the post-processor
    // has no knowledge of the user's chosen export path (that's an OrcaSlicer UI concern).
    // As a workaround, we embed the sub-program file paths directly in the main .SRC
    // header so the user knows exactly where to find them.
    if (isOrcaSlicerMode)
    {
      var mainSrcPath = Path.Combine(outputDir, jobName + ".SRC");
      if (File.Exists(mainSrcPath))
      {
        // Build a header comment listing all sub-program locations
        var splitHeader = BuildSplitLocationHeader(outputDir, jobName);

        // Read main .SRC content
        var mainContent = File.ReadAllBytes(mainSrcPath);

        // Insert location header after the &PARAM lines, before DEF
        var mainText = System.Text.Encoding.UTF8.GetString(mainContent);
        var defIdx = mainText.IndexOf("\nDEF ");
        if (defIdx >= 0)
        {
          mainText = mainText[..(defIdx + 1)] + splitHeader + mainText[(defIdx + 1)..];
        }
        else
        {
          // Fallback: prepend before END
          var endIdx = mainText.LastIndexOf("\nEND");
          if (endIdx >= 0)
            mainText = mainText[..endIdx] + splitHeader + mainText[endIdx..];
        }

        // Write modified content back to .pp so OrcaSlicer outputs it
        File.WriteAllBytes(inputPath, System.Text.Encoding.UTF8.GetBytes(mainText));
        Console.Error.WriteLine($"  OrcaSlicer: wrote main .SRC (with sub-program paths) back to {inputBase}");
        CrashLogger.Log($"OrcaSlicer mode: wrote main .SRC to input file {inputPath}");

        // Mirror to user-specified directory if --output-dir was provided
        MirrorSplitOutput(outputDir, jobName, mirrorDir);
      }
    }

    Console.Error.WriteLine($"gcode2krl: Split done! {outputDir}\\");
    Console.Error.WriteLine($"  Load all .SRC files to KUKA controller, run {jobName}.SRC");
    CrashLogger.Log($"Split success → {outputDir}");
  }

  // ── Helpers ──

  /// <summary>Remove non-alphanumeric characters from a job name for KRL compatibility</summary>
  private static string SanitizeJobName(string name)
  {
    return Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
  }

  /// <summary>Load start configuration from a JSON file, merging with defaults</summary>
  private static void LoadStartConfig(string configPath)
  {
    try
    {
      var json = File.ReadAllText(configPath);

      // Manually parse with JsonDocument to avoid System.Text.Json source-generator
      // nested-object deserialization bug where some properties silently return defaults.
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      var cfg = new StartConfig();

      // Split mode
      if (root.TryGetProperty("split", out var sp)) cfg.Split = sp.GetBoolean();
      if (root.TryGetProperty("splitOutputDir", out var sod) && sod.ValueKind != JsonValueKind.Null)
        cfg.SplitOutputDir = sod.GetString();

      // Axis
      if (root.TryGetProperty("axis", out var ax))
      {
        cfg.Axis = new AxisConfig();
        if (ax.TryGetProperty("A1", out var v)) cfg.Axis.A1 = v.GetDouble();
        if (ax.TryGetProperty("A2", out v)) cfg.Axis.A2 = v.GetDouble();
        if (ax.TryGetProperty("A3", out v)) cfg.Axis.A3 = v.GetDouble();
        if (ax.TryGetProperty("A4", out v)) cfg.Axis.A4 = v.GetDouble();
        if (ax.TryGetProperty("A5", out v)) cfg.Axis.A5 = v.GetDouble();
        if (ax.TryGetProperty("A6", out v)) cfg.Axis.A6 = v.GetDouble();
        if (ax.TryGetProperty("E1", out v)) cfg.Axis.E1 = v.GetDouble();
        if (ax.TryGetProperty("E2", out v)) cfg.Axis.E2 = v.GetDouble();
        if (ax.TryGetProperty("E3", out v)) cfg.Axis.E3 = v.GetDouble();
        if (ax.TryGetProperty("E4", out v)) cfg.Axis.E4 = v.GetDouble();
      }

      // Cart
      if (root.TryGetProperty("cart", out var ct))
      {
        cfg.Cart = new CartConfig();
        if (ct.TryGetProperty("X", out var v)) cfg.Cart.X = v.GetDouble();
        if (ct.TryGetProperty("Y", out v)) cfg.Cart.Y = v.GetDouble();
        if (ct.TryGetProperty("Z", out v)) cfg.Cart.Z = v.GetDouble();
        if (ct.TryGetProperty("A", out v)) cfg.Cart.A = v.GetDouble();
        if (ct.TryGetProperty("B", out v)) cfg.Cart.B = v.GetDouble();
        if (ct.TryGetProperty("C", out v)) cfg.Cart.C = v.GetDouble();
        if (ct.TryGetProperty("E1", out v)) cfg.Cart.E1 = v.GetDouble();
        if (ct.TryGetProperty("E2", out v)) cfg.Cart.E2 = v.GetDouble();
        if (ct.TryGetProperty("E3", out v)) cfg.Cart.E3 = v.GetDouble();
        if (ct.TryGetProperty("E4", out v)) cfg.Cart.E4 = v.GetDouble();
      }

      // Top-level values
      if (root.TryGetProperty("speed", out var sv)) cfg.Speed = sv.GetDouble();
      if (root.TryGetProperty("cdis", out var cv)) cfg.Cdis = cv.GetDouble();
      if (root.TryGetProperty("advance", out var av)) cfg.Advance = av.GetInt32();
      if (root.TryGetProperty("heatMinTemp", out var ht)) cfg.HeatMinTemp = ht.GetInt32();

      // Heat
      if (root.TryGetProperty("heat", out var he))
      {
        cfg.Heat = new HeatConfig();
        if (he.TryGetProperty("T1", out var v)) cfg.Heat.T1 = v.GetDouble();
        if (he.TryGetProperty("T2", out v)) cfg.Heat.T2 = v.GetDouble();
        if (he.TryGetProperty("T3", out v)) cfg.Heat.T3 = v.GetDouble();
        if (he.TryGetProperty("T4", out v)) cfg.Heat.T4 = v.GetDouble();
        if (he.TryGetProperty("T5", out v)) cfg.Heat.T5 = v.GetDouble();
        if (he.TryGetProperty("T6", out v)) cfg.Heat.T6 = v.GetDouble();
      }

      s_config = cfg;
      CrashLogger.Log($"Loaded start config from {configPath}");
      CrashLogger.Log($"Config readback — axis.A1={cfg.Axis.A1}, axis.A2={cfg.Axis.A2}, axis.E1={cfg.Axis.E1}");
    }
    catch (Exception ex)
    {
      CrashLogger.Log($"Warning: could not load start config ({configPath}), using defaults. {ex.Message}");
      s_config = new StartConfig();
    }
  }

  /// <summary>
  /// Mirror ALL generated .SRC files (main + sub-programs) to a user-specified directory
  /// with clean timestamp-based filenames like Split_08101534.src, Split_08101534_1.src.
  /// </summary>
  private static void MirrorSplitOutput(string sourceDir, string jobName, string? mirrorDir)
  {
    if (string.IsNullOrWhiteSpace(mirrorDir))
    {
      Console.Error.WriteLine($"  Split output is in: {sourceDir}\\");
      Console.Error.WriteLine($"  Tip: set splitOutputDir in your start-config.json for auto-mirror");
      return;
    }

    try
    {
      Directory.CreateDirectory(mirrorDir);

      // Timestamp prefix like "Split_08101534"
      var now = DateTime.Now;
      var tsPrefix = $"Split_{now:MMddHHmmss}";

      // Copy main .SRC as Split_MMddHHmmss.src
      var mainSrc = Path.Combine(sourceDir, jobName + ".SRC");
      if (File.Exists(mainSrc))
      {
        var mainDest = Path.Combine(mirrorDir, tsPrefix + ".src");
        File.Copy(mainSrc, mainDest, overwrite: true);
      }

      // Copy sub-programs as Split_MMddHHmmss_1.src, _2.src, etc.
      var subFiles = Directory.GetFiles(sourceDir, jobName + "_*.SRC")
          .OrderBy(f => f)
          .ToArray();

      for (var i = 0; i < subFiles.Length; i++)
      {
        var subDest = Path.Combine(mirrorDir, $"{tsPrefix}_{i + 1}.src");
        File.Copy(subFiles[i], subDest, overwrite: true);
      }

      var totalFiles = (File.Exists(mainSrc) ? 1 : 0) + subFiles.Length;
      Console.Error.WriteLine($"  Mirrored {totalFiles} file(s) → {mirrorDir}\\");
      Console.Error.WriteLine($"    {tsPrefix}.src  (main)");
      for (var i = 0; i < subFiles.Length; i++)
        Console.Error.WriteLine($"    {tsPrefix}_{i + 1}.src  (sub)");

      CrashLogger.Log($"Mirrored {totalFiles} file(s) to {mirrorDir} as {tsPrefix}_*.src");
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"  Warning: mirror failed ({ex.Message})");
      Console.Error.WriteLine($"  Files are still in: {sourceDir}\\");
      CrashLogger.Log($"Mirror failed: {ex.Message}");
    }
  }

  /// <summary>
  /// Build a header comment block listing the paths of all split output files.
  /// Embedded in the main .SRC so the user knows exactly where sub-programs live.
  /// </summary>
  private static string BuildSplitLocationHeader(string outputDir, string jobName)
  {
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("");
    sb.AppendLine("  ;═══════════════════════════════════════════════════════════");
    sb.AppendLine("  ;KRL SPLIT OUTPUT — Sub-program files are located at:");
    sb.AppendLine($"  ;  {outputDir}\\");

    try
    {
      var files = Directory.GetFiles(outputDir, jobName + "*.SRC")
          .Select(f => Path.GetFileName(f))
          .Where(f => f != jobName + ".SRC")  // skip self
          .OrderBy(f => f);

      foreach (var f in files)
        sb.AppendLine($"  ;    {f}");
    }
    catch { /* best-effort */ }

    sb.AppendLine("  ;═══════════════════════════════════════════════════════════");
    return sb.ToString();
  }

  /// <summary>Print CLI usage instructions</summary>
  private static void PrintUsage()
  {
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  OrcaSlicer mode:  gcode2krl <gcode_file>                                  (in-place)");
    Console.Error.WriteLine("  Standalone mode:  gcode2krl <input.gcode> <output.src> [--name=JOB]       (input→output)");
    Console.Error.WriteLine("  Split mode:       gcode2krl <input.gcode> <output_dir> --split             (main + sub-programs)");
    Console.Error.WriteLine("  With config:      gcode2krl <input> <output> --start-config=config.json");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("Split mode generates a main .SRC + N sub-program .SRC files,");
    Console.Error.WriteLine("each ~18 MB, split at complete layer boundaries.");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --name=JOB             KRL program name (default: derived from filename)");
    Console.Error.WriteLine("  --start-config=FILE    Load axis/cart/heat config from JSON");
    Console.Error.WriteLine("  --output-dir=PATH      Mirror split .SRC files to this directory (split mode only)");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("OrcaSlicer post-processing example:");
    Console.Error.WriteLine("  gcode2krl.exe --split --output-dir=D:\\MyKRLPrints");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("Config JSON fields:");
    Console.Error.WriteLine("  axis.{A1..A6,E1..E4}     Joint angles for PTP ready position");
    Console.Error.WriteLine("  cart.{X,Y,Z,A,B,C,E1..E4} Approach position and tool orientation");
    Console.Error.WriteLine("  speed                    Approach speed (m/s, default 0.25)");
    Console.Error.WriteLine("  cdis                     Approximation distance (mm, default 100)");
    Console.Error.WriteLine("  advance                  Look-ahead buffer (default 3)");
    Console.Error.WriteLine("  heat.{T1..T6}            Target temperatures per zone");
    Console.Error.WriteLine("  heatMinTemp              Minimum temp to wait for all zones (default 180)");
    Console.Error.WriteLine("  split                    true to enable split mode (default false)");
    Console.Error.WriteLine("  splitOutputDir           Mirror sub-programs to this directory (optional)");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("Example start-config.json for OrcaSlicer:");
    Console.Error.WriteLine("  {");
    Console.Error.WriteLine("    \"split\": false,");
    Console.Error.WriteLine("    \"splitOutputDir\": null,");
    Console.Error.WriteLine("    \"heat\": { \"T1\": 220, \"T2\": 220, \"T3\": 230, \"T4\": 220 }");
    Console.Error.WriteLine("  }");
  }
}
