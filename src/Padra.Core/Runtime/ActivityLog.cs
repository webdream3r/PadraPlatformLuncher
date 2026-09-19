using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Padra.Core.Runtime;

public sealed record LogEntry(long Seq, DateTimeOffset At, string Text);

/// <summary>
/// The single log the gateway writes and the control panel polls.
/// <para>
/// A windowed build has no console, so every line is also appended to a file
/// when one is configured — that file is the only diagnostic a user of the
/// packaged executable has.
/// </para>
/// </summary>
public sealed class ActivityLog
{
    private const int Capacity = 500;

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private long _sequence;
    private string? _file;

    public event Action<LogEntry>? Written;

    public string? FilePath
    {
        get
        {
            lock (_fileLock)
            {
                return _file;
            }
        }
    }

    public void UseFile(string path)
    {
        lock (_fileLock)
        {
            _file = path;
        }
    }

    public void Write(string message)
    {
        var now = DateTimeOffset.Now;
        var text = $"[gateway {now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {message}";
        var entry = new LogEntry(Interlocked.Increment(ref _sequence), now, text);

        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
            // Trim to the newest `Capacity` lines.
        }

        Console.Out.WriteLine(text);

        var file = FilePath;
        if (file is not null)
        {
            lock (_fileLock)
            {
                try
                {
                    File.AppendAllText(
                        file,
                        $"{now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Losing a log line must never break the request that produced it.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same.
                }
            }
        }

        Written?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Since(long seq) =>
        _entries.Where(e => e.Seq > seq).OrderBy(e => e.Seq).ToList();

    public long Cursor => Interlocked.Read(ref _sequence);
}
