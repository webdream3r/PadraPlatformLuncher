using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Padra.Core.Runtime;

/// <summary>One accelerator and how much of its memory is free right now.</summary>
public sealed record GpuDevice(int Index, string Name, string Vendor, long TotalBytes, long FreeBytes);

/// <summary>
/// What this machine can hold, as measured — never as a limit on what may run.
/// </summary>
public sealed record HardwareSnapshot(
    long TotalRamBytes,
    long AvailableRamBytes,
    IReadOnlyList<GpuDevice> Gpus,
    bool UnifiedMemory,
    IReadOnlyList<string> Notes,
    DateTimeOffset TakenAt)
{
    public static readonly HardwareSnapshot Unknown = new(
        0, 0, Array.Empty<GpuDevice>(), false, Array.Empty<string>(), DateTimeOffset.UnixEpoch);

    public bool HasGpu => Gpus.Count > 0;

    public long TotalVramBytes => Gpus.Sum(g => g.TotalBytes);

    public long FreeVramBytes => Gpus.Sum(g => g.FreeBytes);

    /// <summary>The largest single device, which is what a non-split model must fit.</summary>
    public long LargestGpuBytes => Gpus.Count == 0 ? 0 : Gpus.Max(g => g.TotalBytes);

    /// <summary>
    /// Everything a model can be spread across: VRAM plus the system RAM it can be
    /// offloaded into. On unified-memory machines the two are the same pool.
    /// </summary>
    public long TotalUsableBytes => UnifiedMemory
        ? TotalRamBytes
        : TotalVramBytes + TotalRamBytes;
}

/// <summary>
/// Reads the memory this machine actually has.
/// <para>
/// The answer is advisory: the launcher uses it to explain what a model will cost
/// and to pre-fill offload settings, never to stop a model from being added or
/// started. Every probe is bounded, cached, and returns what it managed to learn
/// rather than throwing — a machine with no vendor tool still gets its RAM figure.
/// </para>
/// </summary>
public static class HardwareProbe
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly object Gate = new();

    private static HardwareSnapshot? _cache;

    /// <summary>The current snapshot, re-measured at most every 20 seconds.</summary>
    public static HardwareSnapshot Read(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && _cache is { } cached &&
                DateTimeOffset.UtcNow - cached.TakenAt < Freshness)
            {
                return cached;
            }

            var notes = new List<string>();
            var (total, available) = ReadMemory(notes);
            var gpus = ReadGpus(notes, out var unified);

            _cache = new HardwareSnapshot(total, available, gpus, unified, notes,
                DateTimeOffset.UtcNow);
            return _cache;
        }
    }

    /// <summary>Drop the cached snapshot so the next read measures again.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    // ------------------------------------------------------------------ RAM #
    private static (long Total, long Available) ReadMemory(List<string> notes)
    {
        // The GC reports the machine's physical memory and its current load on every
        // platform, and honours a container limit where one applies.
        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes;
        var available = total - info.MemoryLoadBytes;

        if (OperatingSystem.IsLinux())
        {
            var meminfo = ReadLinuxMemInfo();
            if (meminfo.Total > 0)
            {
                total = meminfo.Total;
            }

            if (meminfo.Available > 0)
            {
                available = meminfo.Available;
            }
        }

        if (total <= 0)
        {
            notes.Add("system memory could not be measured");
            return (0, 0);
        }

        return (total, Math.Clamp(available, 0, total));
    }

    private static (long Total, long Available) ReadLinuxMemInfo()
    {
        long total = 0;
        long available = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = KilobytesFrom(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = KilobytesFrom(line);
                }

                if (total > 0 && available > 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        return (total, available);

        static long KilobytesFrom(string line)
        {
            var parts = line.Split(':', 2);
            var digits = new string(parts[1].Where(char.IsDigit).ToArray());
            return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var kilobytes)
                ? kilobytes * 1024
                : 0;
        }
    }

    // ------------------------------------------------------------------ GPU #
    private static IReadOnlyList<GpuDevice> ReadGpus(List<string> notes, out bool unifiedMemory)
    {
        unifiedMemory = false;

        var nvidia = ReadNvidia();
        if (nvidia.Count > 0)
        {
            return nvidia;
        }

        var amd = ReadAmd();
        if (amd.Count > 0)
        {
            return amd;
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is Architecture.Arm64)
        {
            // Apple silicon shares one pool between CPU and GPU, so the GPU budget is
            // the machine's memory rather than a separate device.
            unifiedMemory = true;
            var info = GC.GetGCMemoryInfo();
            return new[]
            {
                new GpuDevice(0, "Apple silicon (unified memory)", "apple",
                    info.TotalAvailableMemoryBytes,
                    Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes)),
            };
        }

        notes.Add("no GPU was detected — models will run on the CPU");
        return Array.Empty<GpuDevice>();
    }

    private static IReadOnlyList<GpuDevice> ReadNvidia()
    {
        var output = Capture("nvidia-smi",
            new[]
            {
                "--query-gpu=index,name,memory.total,memory.free",
                "--format=csv,noheader,nounits",
            });
        if (output is null)
        {
            return Array.Empty<GpuDevice>();
        }

        var devices = new List<GpuDevice>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',');
            if (fields.Length < 4)
            {
                continue;
            }

            if (!int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var index) ||
                !long.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var totalMib) ||
                !long.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var freeMib))
            {
                continue;
            }

            devices.Add(new GpuDevice(index, fields[1].Trim(), "nvidia",
                totalMib * 1024 * 1024, freeMib * 1024 * 1024));
        }

        return devices;
    }

    private static IReadOnlyList<GpuDevice> ReadAmd()
    {
        var output = Capture("rocm-smi", new[] { "--showmeminfo", "vram", "--csv" });
        if (output is null)
        {
            return Array.Empty<GpuDevice>();
        }

        var devices = new List<GpuDevice>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length < 3 ||
                !long.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var total) ||
                !long.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var used))
            {
                continue;
            }

            var index = devices.Count;
            var name = fields[0].Trim();
            devices.Add(new GpuDevice(index, name.Length > 0 ? name : $"AMD GPU {index}", "amd",
                total, Math.Max(0, total - used)));
        }

        return devices;
    }

    /// <summary>Run a vendor tool and return its output, or null if it is not usable.</summary>
    private static string? Capture(string command, IReadOnlyList<string> arguments)
    {
        if (ExecutableFinder.Find(command) is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                ProcessTree.Terminate(process, null);
                return null;
            }

            return process.ExitCode == 0 && stdout.Trim().Length > 0 ? stdout : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException or IOException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
