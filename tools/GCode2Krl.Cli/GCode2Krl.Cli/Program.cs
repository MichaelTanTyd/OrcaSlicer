using System.Text.Json;
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
        var cleanArgs = new List<string>();

        foreach (var arg in args)
        {
            if (arg == "--split")
                splitMode = true;
            else if (arg.StartsWith("--start-config="))
                configPath = arg["--start-config=".Length..];
            else if (arg.StartsWith("--name="))
                jobName = arg["--name=".Length..];
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

        // ── Setup logging ──
        CrashLogger.SetupLog(outputPath);

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
                RunSplit(inputPath, outputPath, jobName, isOrcaSlicerMode);
            }
            else
            {
                RunStandard(inputPath, outputPath, jobName);
            }

            CrashLogger.CloseLog();
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
    private static void RunSplit(string inputPath, string outputDir, string jobName, bool isOrcaSlicerMode)
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
        // For split mode, sub-programs are generated alongside but would be stranded
        // in the temp Metadata directory. We mirror ALL generated .SRC files to
        // {exe_dir}\krl_output\{jobName}\ so the user has a single, fixed place to find them.
        if (isOrcaSlicerMode)
        {
            var mainSrcPath = Path.Combine(outputDir, jobName + ".SRC");
            if (File.Exists(mainSrcPath))
            {
                // Write main .SRC back to .pp so OrcaSlicer outputs it as the converted result
                var mainContent = File.ReadAllBytes(mainSrcPath);
                File.WriteAllBytes(inputPath, mainContent);
                Console.Error.WriteLine($"  OrcaSlicer: wrote main .SRC back to {inputBase}");
                CrashLogger.Log($"OrcaSlicer mode: wrote main .SRC to input file {inputPath}");

                // Try to mirror split output to current working directory.
                // If OrcaSlicer sets CWD to the user's export directory, files land there.
                // Otherwise, they stay in the .pp's directory (Metadata temp folder).
                MirrorSplitOutput(outputDir, jobName);
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
            var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.StartConfig);
            if (cfg != null)
            {
                // Deep-merge: top-level properties replace, nested objects merge
                s_config = cfg;
            }
            CrashLogger.Log($"Loaded start config from {configPath}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log($"Warning: could not load start config ({configPath}), using defaults. {ex.Message}");
            s_config = new StartConfig();
        }
    }

    /// <summary>
    /// Mirror all generated .SRC files from the temp output directory to the
    /// current working directory (CWD). If OrcaSlicer sets CWD to the user's
    /// export directory, the files land right where the user expects them.
    /// Falls back gracefully — files always remain in the source directory.
    /// </summary>
    private static void MirrorSplitOutput(string sourceDir, string jobName)
    {
        try
        {
            var cwd = Environment.CurrentDirectory;
            Console.Error.WriteLine($"  CWD: {cwd}");
            CrashLogger.Log($"OrcaSlicer CWD: {cwd}");

            // Only mirror if CWD is different from and not inside the source dir
            var srcFull = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var cwdFull = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(srcFull, cwdFull, StringComparison.OrdinalIgnoreCase))
            {
                // Same directory — files are already in the right place, nothing to mirror
                Console.Error.WriteLine($"  Split output is in CWD — files are already where you need them.");
                return;
            }

            // Mirror to CWD
            var srcFiles = Directory.GetFiles(sourceDir, jobName + "*.SRC");
            foreach (var src in srcFiles)
            {
                var dest = Path.Combine(cwd, Path.GetFileName(src));
                File.Copy(src, dest, overwrite: true);
            }

            Console.Error.WriteLine($"  Split output mirrored to CWD: {cwd}\\");
            CrashLogger.Log($"Split output mirrored: {srcFiles.Length} file(s) → {cwd}");
        }
        catch (Exception ex)
        {
            // Best-effort: files are still intact in the source directory
            CrashLogger.Log($"Info: could not mirror split output to CWD: {ex.Message}");
        }
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
        Console.Error.WriteLine("Config JSON fields:");
        Console.Error.WriteLine("  axis.{A1..A6,E1..E4}    Joint angles for PTP ready position");
        Console.Error.WriteLine("  cart.{X,Y,Z,A,B,C,E1..E4}  Approach position and tool orientation");
        Console.Error.WriteLine("  speed                   Approach speed (m/s, default 0.25)");
        Console.Error.WriteLine("  cdis                    Approximation distance (mm, default 100)");
        Console.Error.WriteLine("  advance                 Look-ahead buffer (default 3)");
        Console.Error.WriteLine("  heat.{T1..T6}           Target temperatures per zone");
        Console.Error.WriteLine("  heatMinTemp             Minimum temp to wait for all zones (default 180)");
    }
}
