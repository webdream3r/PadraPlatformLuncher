using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Padra.Desktop;

/// <summary>
/// Starts, watches and stops the gateway process.
/// <para>
/// Shutdown is a request first (<c>POST /api/shutdown</c>), so the gateway can
/// unload every model and free VRAM, and only then a kill of the whole process
/// tree. That ordering is the difference between a clean exit and several
/// gigabytes stranded on the GPU.
/// </para>
/// </summary>
public sealed class GatewaySupervisor : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(90);

    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly object _logLock = new();

    private Process? _process;

    public GatewaySupervisor(string directory, string configPath, string logPath, int port)
    {
        _directory = directory;
        _configPath = configPath;
        _logPath = logPath;
        Origin = $"http://127.0.0.1:{port}";
    }

    public string Origin { get; }

    public bool IsRunning => _process is { HasExited: false };

    public event Action? Exited;

    public string ExecutablePath =>
        Path.Combine(_directory, OperatingSystem.IsWindows() ? "Padra.Server.exe" : "Padra.Server");

    public void Start()
    {
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException(
                $"Padra.Server was not found next to this application:\n\n{ExecutablePath}\n\n" +
                "Publish both projects into the same folder.");
        }

        var start = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(_configPath);

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.Exited += (_, _) => Exited?.Invoke();

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task<bool> WaitUntilReadyAsync()
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                return false;
            }

            try
            {
                using var response = await _http.GetAsync($"{Origin}/healthz").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
        }

        return false;
    }

    public void Stop()
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            _process = null;
            return;
        }

        if (RequestShutdown() && process.WaitForExit((int)GracefulTimeout.TotalMilliseconds))
        {
            _process = null;
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(30_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            Append($"the gateway could not be stopped: {ex.Message}");
        }

        _process = null;
    }

    private bool RequestShutdown()
    {
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = _http.PostAsync($"{Origin}/api/shutdown", content)
                .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or AggregateException)
        {
            return false;
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            Append(args.Data);
        }
    }

    /// <summary>
    /// Append one line to the shared log.
    /// <para>
    /// The gateway's own output arrives here through the redirected pipes, and this
    /// is the only writer: the child is deliberately not given <c>--log-file</c> for
    /// the same path, because two processes appending to one file with
    /// <c>FileShare.Read</c> throw on every overlap and duplicate every line that
    /// does get through. The lock covers the two pipe callbacks, which are raised on
    /// separate threads.
    /// </para>
    /// </summary>
    private void Append(string line)
    {
        lock (_logLock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is a convenience, never a dependency.
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _process?.Dispose();
    }
}
