using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Padra.Core.Networking;

namespace Padra.Core.Runtime;

public sealed class OrphanEntry
{
    [JsonPropertyName("alias")] public string Alias { get; set; } = string.Empty;
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("started")] public long Started { get; set; }
}

/// <summary>
/// Records spawned backend pids so a later run can clean up after a crash: a
/// gateway that dies without stopping its models would otherwise leave several
/// gigabytes of VRAM occupied with no way to reach the process.
/// </summary>
public sealed class OrphanRegistry(string path, ActivityLog log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();

    public string Path { get; } = path;

    public void Add(string alias, int pid, int port)
    {
        lock (_lock)
        {
            var entries = Read().Where(e => e.Pid != pid).ToList();
            entries.Add(new OrphanEntry
            {
                Alias = alias,
                Pid = pid,
                Port = port,
                Started = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            Write(entries);
        }
    }

    public void Remove(int pid)
    {
        lock (_lock)
        {
            Write(Read().Where(e => e.Pid != pid).ToList());
        }
    }

    /// <summary>Kill backends left behind by a previous run of this launcher.</summary>
    public void Sweep(IReadOnlySet<int> knownPorts)
    {
        lock (_lock)
        {
            var entries = Read();
            if (entries.Count == 0)
            {
                return;
            }

            var survivors = new List<OrphanEntry>();
            foreach (var entry in entries)
            {
                if (!knownPorts.Contains(entry.Port) || !ProcessTree.IsAlive(entry.Pid))
                {
                    continue;
                }

                if (NetworkInfo.PortIsFree("127.0.0.1", entry.Port))
                {
                    continue;   // the pid was recycled by an unrelated process
                }

                // A pid alone is not an identity: the OS reuses them, and the port can
                // be held by something the user started themselves — an `unsloth run`
                // of their own, for example. The recorded spawn time is the proof:
                // kill only the process that was actually started back then.
                if (!ProcessTree.StartedNear(entry.Pid, DateTimeOffset.FromUnixTimeSeconds(entry.Started)))
                {
                    log.Write($"skipping pid {entry.Pid} on port {entry.Port} — it is not the " +
                              "backend this launcher started, so it was left running");
                    continue;
                }

                log.Write($"cleaning up an orphaned backend from a previous run " +
                          $"(pid {entry.Pid}, port {entry.Port})");
                ProcessTree.Terminate(entry.Pid, log);
                if (ProcessTree.IsAlive(entry.Pid))
                {
                    survivors.Add(entry);
                }
            }

            Write(survivors);
        }
    }

    public List<OrphanEntry> Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new List<OrphanEntry>();
            }

            return JsonSerializer.Deserialize<List<OrphanEntry>>(File.ReadAllText(Path))
                   ?? new List<OrphanEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<OrphanEntry>();
        }
    }

    private void Write(List<OrphanEntry> entries)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Path, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"could not update {System.IO.Path.GetFileName(Path)}: {ex.Message}");
        }
    }
}

/// <summary>Stopping a process and everything it started.</summary>
public static class ProcessTree
{
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// How far the recorded spawn moment may differ from the process's own start
    /// time and still count as the same process. Generous, because the two stamps
    /// are taken by different clocks (the registry after <c>Process.Start</c>
    /// returned, the OS at creation), but far below the lifetime of a pid.
    /// </summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when the process behind <paramref name="pid"/> was created at about
    /// <paramref name="recorded"/> — the proof that a stored pid still names the
    /// process it named back then, not a recycled number or someone else's server.
    /// Unreadable start times count as "not ours": never kill what cannot be
    /// identified.
    /// </summary>
    public static bool StartedNear(int pid, DateTimeOffset recorded)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (started - recorded).Duration() <= StartTolerance;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or NotSupportedException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void Terminate(int pid, ActivityLog? log, int graceSeconds = 20)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Terminate(process, log, graceSeconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    public static void Terminate(Process process, ActivityLog? log, int graceSeconds = 20)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(TimeSpan.FromSeconds(graceSeconds)))
            {
                log?.Write($"pid {process.Id} did not exit within {graceSeconds}s");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            log?.Write($"could not stop pid {SafeId(process)}: {ex.Message}");
        }
    }

    private static string SafeId(Process process)
    {
        try
        {
            return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }
}
