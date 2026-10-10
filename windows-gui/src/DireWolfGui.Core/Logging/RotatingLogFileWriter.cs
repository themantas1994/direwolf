using System.Text;

namespace DireWolfGui.Core.Logging;

/// <summary>
/// Appends log entries to a text file; when it exceeds <see cref="MaxBytes"/> the file is renamed to
/// name.1.ext (older ones shift to .2, .3, ...) and a new file is started. Thread-safe.
/// Text is redacted again before writing as a safety net.
/// </summary>
public sealed class RotatingLogFileWriter : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public RotatingLogFileWriter(string path, long maxBytes = 5 * 1024 * 1024, int maxFiles = 5)
    {
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        Path = System.IO.Path.GetFullPath(path);
        MaxBytes = maxBytes;
        MaxFiles = maxFiles;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    public string Path { get; }
    public long MaxBytes { get; }
    public int MaxFiles { get; }

    public void Write(LogEntry entry) =>
        WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {entry.Severity,-8} {entry.Text}");

    public void WriteLine(string line)
    {
        line = SecretRedactor.Redact(line);
        lock (_lock)
        {
            _writer ??= Open();
            _writer.WriteLine(line);
            _writer.Flush();
            if (_writer.BaseStream.Length >= MaxBytes)
            {
                _writer.Dispose();
                _writer = null;
                Rotate();
            }
        }
    }

    /// <summary>Path of rotated file number <paramref name="n"/> (1 = newest).</summary>
    public string RotatedPath(int n)
    {
        string dir = System.IO.Path.GetDirectoryName(Path)!;
        return System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(Path) + "." + n + System.IO.Path.GetExtension(Path));
    }

    private StreamWriter Open() =>
        new(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));

    private void Rotate()
    {
        if (MaxFiles == 1) { File.Delete(Path); return; }
        File.Delete(RotatedPath(MaxFiles - 1));
        for (int i = MaxFiles - 2; i >= 1; i--)
            if (File.Exists(RotatedPath(i))) File.Move(RotatedPath(i), RotatedPath(i + 1));
        File.Move(Path, RotatedPath(1));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
