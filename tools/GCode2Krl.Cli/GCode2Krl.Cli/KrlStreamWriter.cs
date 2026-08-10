namespace GCode2Krl.Cli;

// ──────────────────────────────────────────
// Buffered KRL output writer
//   Accumulates lines in memory, flushes to disk
//   every ~500 KB to keep memory low.
//   Writes Windows-style \r\n line endings.
// ──────────────────────────────────────────

/// <summary>
/// Writes KRL output lines with buffering to avoid holding
/// the entire output in memory at once.
/// </summary>
public sealed class KrlStreamWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly StreamWriter _sw;
    private readonly List<string> _buffer = new();
    private int _bufferSize;

    /// <summary>Threshold in bytes before auto-flushing the buffer</summary>
    private const int FlushThreshold = 500 * 1024; // 500 KB

    /// <summary>Total lines written so far</summary>
    public int LineCount { get; private set; }

    /// <summary>
    /// Create a new writer. Creates parent directories if needed.
    /// </summary>
    public KrlStreamWriter(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _sw = new StreamWriter(_fs, utf8NoBom) { NewLine = "\r\n" };
    }

    /// <summary>Write a single line, auto-flushing at threshold</summary>
    public void WriteLine(string line)
    {
        _buffer.Add(line);
        _bufferSize += line.Length + 2; // +2 for \r\n
        LineCount++;

        if (_bufferSize >= FlushThreshold)
            Flush();
    }

    /// <summary>Write multiple lines at once</summary>
    public void WriteLines(string[] lines)
    {
        foreach (var line in lines)
            WriteLine(line);
    }

    /// <summary>Force-flush buffered lines to disk</summary>
    public void Flush()
    {
        if (_buffer.Count == 0) return;

        for (var i = 0; i < _buffer.Count; i++)
            _sw.WriteLine(_buffer[i]);

        _sw.Flush();
        _fs.Flush();
        _buffer.Clear();
        _bufferSize = 0;
    }

    /// <summary>Flush remaining lines and close the file</summary>
    public void Dispose()
    {
        if (_buffer.Count > 0)
        {
            for (var i = 0; i < _buffer.Count; i++)
                _sw.WriteLine(_buffer[i]);
        }
        _sw.Dispose();
        _fs.Dispose();
    }
}
