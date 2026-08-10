using System.Text.Json.Serialization;

namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// Data models — all simple POCOs / records
// ──────────────────────────────────────────

/// <summary>Parsed G-code command: { Cmd, Params }</summary>
public readonly record struct GCodeCommand(string Cmd, Dictionary<char, double> Params);

/// <summary>Joint axis angles for the PTP ready position</summary>
public sealed class AxisConfig
{
    public double A1 { get; set; } = 5;
    public double A2 { get; set; } = -85.75;
    public double A3 { get; set; } = 109.02;
    public double A4 { get; set; } = 0.30;
    public double A5 { get; set; } = 64.18;
    public double A6 { get; set; } = 2.93;
    public double E1 { get; set; } = 0;
    public double E2 { get; set; } = 0;
    public double E3 { get; set; } = 0;
    public double E4 { get; set; } = 0;
}

/// <summary>Cartesian approach position & tool orientation</summary>
public sealed class CartConfig
{
    public double X { get; set; } = 0;
    public double Y { get; set; } = 0;
    public double Z { get; set; } = 200;
    public double A { get; set; } = 112;
    public double B { get; set; } = 20.03;
    public double C { get; set; } = -177.85;
    public double E1 { get; set; } = 0;
    public double E2 { get; set; } = 0;
    public double E3 { get; set; } = 0;
    public double E4 { get; set; } = 0;
}

/// <summary>Heater temperature targets per zone</summary>
public sealed class HeatConfig
{
    public double T1 { get; set; } = 220;
    public double T2 { get; set; } = 220;
    public double T3 { get; set; } = 230;
    public double T4 { get; set; } = 220;
    public double T5 { get; set; } = 0;
    public double T6 { get; set; } = 0;
}

/// <summary>Full start-position configuration (loaded from JSON)</summary>
public sealed class StartConfig
{
    public AxisConfig Axis { get; set; } = new();
    public CartConfig Cart { get; set; } = new();
    public double Speed { get; set; } = 0.25;
    public double Cdis { get; set; } = 100;
    public int Advance { get; set; } = 3;
    public HeatConfig Heat { get; set; } = new();
    public int HeatMinTemp { get; set; } = 180;

    /// <summary>Enable split mode (main .SRC + sub-programs). Default: false (single file)</summary>
    public bool Split { get; set; } = false;

    /// <summary>
    /// Optional directory to mirror split sub-program files.
    /// When set in OrcaSlicer mode, sub-program .SRC files are copied here
    /// so you don't have to hunt through temp directories.
    /// Leave empty/null to rely on the embedded path comment in the main .SRC.
    /// </summary>
    public string? SplitOutputDir { get; set; }
}

/// <summary>Metadata for a single layer — used in split-mode Pass 1</summary>
public sealed class LayerInfo
{
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public bool IsPreamble { get; set; }
    public int LayerIndex { get; set; }

    public int G1Count { get; set; }
    public int G0Count { get; set; }
    public int FanCount { get; set; }
    public int G92Count { get; set; }
    public int MiscCount { get; set; }

    /// <summary>Estimated output size in bytes</summary>
    public long EstimatedBytes { get; set; }
}

/// <summary>A group of layers that fit within the chunk size limit</summary>
public sealed class ChunkInfo
{
    public List<LayerInfo> Layers { get; set; } = new();
    public long TotalBytes { get; set; }
    public int FirstLayerIndex { get; set; }
    public int LastLayerIndex { get; set; } = -1;
}

/// <summary>AOT-compatible JSON serialization context</summary>
[JsonSerializable(typeof(StartConfig))]
[JsonSerializable(typeof(AxisConfig))]
[JsonSerializable(typeof(CartConfig))]
[JsonSerializable(typeof(HeatConfig))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
