using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Padra.Core.Configuration;
using Padra.Core.Networking;

namespace Padra.Core.Runtime;

public sealed record BackendStatus(
    string Alias,
    string Repo,
    string Quant,
    string ModelSpec,
    string ServedId,
    string BaseUrl,
    int Port,
    bool Running,
    bool External,
    int? Pid,
    bool HaveApiKey,
    int? ContextLength,
    DateTimeOffset? LastUsed,
    string LastError);

/// <summary>Raised when a backend cannot be started; the message reaches the client.</summary>
public sealed class BackendStartException(string message) : Exception(message);

/// <summary>A single <c>unsloth run</c> process serving exactly one model.</summary>
public sealed partial class ModelBackend : IDisposable
{
    private const int LogTail = 200;

    [GeneratedRegex(@"sk-unsloth-[A-Za-z0-9._\-]{8,}")]
    private static partial Regex UnslothKey();

    private readonly ActivityLog _log;
    private readonly OrphanRegistry? _registry;
    private readonly HttpClient _probeClient;
    private readonly Queue<string> _tail = new();
    private readonly object _tailLock = new();
    private readonly string? _configuredKey;

    private Process? _process;

    public ModelBackend(
        ModelOptions options,
        GatewayOptions gateway,
        ActivityLog log,
        OrphanRegistry? registry = null,
        HttpClient? probeClient = null)
    {
        Options = options.Clone();
        _log = log;
        _registry = registry;
        _probeClient = probeClient ?? DirectHttp.CreateClient(TimeSpan.FromSeconds(10));

        UnslothBin = Environment.GetEnvironmentVariable("UNSLOTH_BIN") ?? gateway.UnslothBin;
        StartTimeout = TimeSpan.FromSeconds(Math.Max(5, gateway.StartTimeout));

        _configuredKey = FirstNonEmpty(
            options.ApiKey,
            Environment.GetEnvironmentVariable(
                $"UNSLOTH_API_KEY_{options.Alias.ToUpperInvariant().Replace('-', '_')}"),
            Environment.GetEnvironmentVariable("UNSLOTH_API_KEY"));
        ApiKey = _configuredKey;

        ServedId = string.IsNullOrEmpty(options.ServedId) ? options.ModelSpec : options.ServedId;
    }

    public ModelOptions Options { get; }

    public string Alias => Options.Alias;

    public int Port => Options.Port;

    public string UnslothBin { get; set; }

    public TimeSpan StartTimeout { get; set; }

    public string ServedId { get; private set; }

    public string? ApiKey { get; private set; }

    public bool External { get; private set; }

    public DateTimeOffset? LastUsed { get; set; }

    private int _activeRequests;

    /// <summary>Requests currently streaming through this backend.</summary>
    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    /// <summary>
    /// Mark one request in flight. While the count is above zero the idle reaper
    /// and the exclusive-mode swap leave this backend alone, so a long agentic
    /// turn is never cut off mid-stream by housekeeping.
    /// </summary>
    public void BeginRequest() => Interlocked.Increment(ref _activeRequests);

    /// <summary>The matching end of <see cref="BeginRequest"/>; stamps the idle clock.</summary>
    public void EndRequest()
    {
        Interlocked.Decrement(ref _activeRequests);
        LastUsed = DateTimeOffset.UtcNow;
    }

    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Flags the fleet adds for this start because the model does not fit in VRAM.
    /// They are not persisted: the machine, not the model, decides them, and they
    /// are recomputed every time the model is loaded.
    /// </summary>
    public IReadOnlyList<string> AdaptiveArguments { get; set; } = Array.Empty<string>();

    public string BaseUrl => $"http://{NetworkHosts.ForUrl(Options.Host)}:{Options.Port}";

    public bool IsRunning => External || (_process is { HasExited: false });

    /// <summary>True when a reloaded entry describes the same running process.</summary>
    public bool Matches(ModelOptions other) =>
        Options.Repo == other.Repo &&
        Options.Quant == other.Quant &&
        Options.Port == other.Port &&
        Options.Host == other.Host &&
        Options.DisableTools == other.DisableTools &&
        Options.Reasoning == other.Reasoning &&
        Options.ContextLength == other.ContextLength &&
        Options.ExtraArgs.SequenceEqual(other.ExtraArgs) &&
        Options.Runtime.SameAs(other.Runtime) &&
        Options.ServedId == other.ServedId &&
        Options.ApiKey == other.ApiKey &&
        Options.Env.Count == other.Env.Count &&
        Options.Env.All(pair =>
            other.Env.TryGetValue(pair.Key, out var value) && value == pair.Value);

    // ------------------------------------------------------------- command #
    /// <summary>
    /// The full <c>unsloth run</c> command line for this model.
    /// <para>
    /// Order matters: Unsloth forwards anything it does not own to the inference
    /// server, and that server takes the last occurrence of a repeated flag. The
    /// runtime block therefore comes before <c>extraArgs</c>, so a hand-written
    /// argument always wins over a panel setting.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> BuildArguments()
    {
        var arguments = new List<string>
        {
            "run",
            "--model", Options.ModelSpec,
            "-H", Options.Host,
            "-p", Options.Port.ToString(CultureInfo.InvariantCulture),
        };

        if (Options.ContextLength is > 0)
        {
            arguments.Add("-c");
            arguments.Add(Options.ContextLength.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (Options.DisableTools && !Options.ExtraArgs.Contains("--disable-tools"))
        {
            arguments.Add("--disable-tools");
        }

        if (Options.Reasoning.Length > 0 && !Options.ExtraArgs.Contains("--reasoning"))
        {
            arguments.Add("--reasoning");
            arguments.Add(Options.Reasoning);
        }

        // Never block on the confirmation shown for non-local binds.
        if (!Options.ExtraArgs.Contains("-y") && !Options.ExtraArgs.Contains("--yes"))
        {
            arguments.Add("--yes");
        }

        arguments.AddRange(RuntimeArguments());
        arguments.AddRange(AdaptiveArguments);
        arguments.AddRange(Options.ExtraArgs);
        return arguments;
    }

    /// <summary>
    /// The offload and placement flags, in the form <c>unsloth run</c> forwards.
    /// </summary>
    public IReadOnlyList<string> RuntimeArguments()
    {
        var runtime = Options.Runtime;
        var arguments = new List<string>();

        void Add(string flag, string value)
        {
            arguments.Add(flag);
            arguments.Add(value);
        }

        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

        if (runtime.GpuMemoryMode == GpuMemoryMode.Manual)
        {
            Add("--gpu-memory-mode", "manual");
        }

        if (runtime.GpuLayers is { } layers)
        {
            // The server takes an exact count, `all`, or `auto`; -1 is the fleet
            // file's way of writing "all", which is what a user pinning layers to
            // the GPU means.
            Add("-ngl", layers == RuntimeOptions.AllGpuLayers ? "all" : Count(layers));
        }

        if (runtime.Fit != Toggle.Auto)
        {
            Add("--fit", runtime.Fit == Toggle.On ? "on" : "off");
        }

        if (runtime.FitContext is { } fitContext and > 0)
        {
            Add("-fitc", Count(fitContext));
        }

        if (runtime.CpuMoeLayers is { } moe)
        {
            if (moe == RuntimeOptions.AllCpuMoeLayers)
            {
                arguments.Add("-cmoe");
            }
            else if (moe > 0)
            {
                Add("-ncmoe", Count(moe));
            }
        }

        if (runtime.KvCacheType.Length > 0)
        {
            Add("-ctk", runtime.KvCacheType);
            Add("-ctv", runtime.KvCacheType);
        }

        if (runtime.FlashAttention != Toggle.Auto)
        {
            Add("-fa", runtime.FlashAttention == Toggle.On ? "on" : "off");
        }

        if (runtime.Threads is { } threads and > 0)
        {
            Add("-t", Count(threads));
        }

        if (runtime.NoKvOffload)
        {
            arguments.Add("-nkvo");
        }

        if (runtime.SwaFull)
        {
            arguments.Add("--swa-full");
        }

        return arguments;
    }

    public string CommandLine() => $"{UnslothBin} {string.Join(' ', BuildArguments())}";

    // ------------------------------------------------------------ lifecycle #
    /// <summary>Attach to an existing server or spawn one, then wait until it answers.</summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        if (IsRunning)
        {
            return;
        }

        LastError = string.Empty;

        var (reachable, ids) = await ProbeAsync(TimeSpan.FromSeconds(2.5), token).ConfigureAwait(false);
        if (reachable && ids is { Count: > 0 })
        {
            External = true;
            AdoptServedId(ids);
            _log.Write($"{Alias}: attached to the server already on :{Port} (served id: {ServedId})");
            return;
        }

        if (reachable)
        {
            // Something is already listening on this port and it could not be used
            // as-is. Spawning `unsloth run` on top of it would either fail to bind
            // or fight the running server — including one the user started by hand —
            // so this is always an error, never a second process.
            var reason = string.IsNullOrEmpty(ApiKey)
                ? "that needs an API key. Set `apiKey` for this model in models.yaml, " +
                  "give this model a free port, or stop the other server"
                : ids is null
                    ? "that refused the configured API key. Fix `apiKey` for this model, " +
                      "give it a free port, or stop the other server"
                    : "that is serving no models yet. Give this model a free port, " +
                      "or stop the other server";
            throw new BackendStartException(
                $"{Alias}: port {Port} is already in use by another server {reason}. " +
                "Nothing was started, so the running server was left untouched.");
        }

        if (ResolveBinary() is not { } executable)
        {
            throw new BackendStartException(
                $"the `{UnslothBin}` command was not found. Install the Unsloth CLI first — " +
                "Windows: irm https://unsloth.ai/install.ps1 | iex, " +
                "macOS/Linux: curl -fsSL https://unsloth.ai/install.sh | sh");
        }

        var start = ProcessLauncher.Build(executable, BuildArguments());
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = Environment.CurrentDirectory;

        foreach (var (key, value) in Options.Env)
        {
            start.Environment[key] = value;
        }

        _log.Write($"{Alias}: starting -> {CommandLine()}");

        try
        {
            _process = Process.Start(start)
                       ?? throw new BackendStartException($"{Alias}: `{UnslothBin}` did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BackendStartException($"{Alias}: could not start `{UnslothBin}`: {ex.Message}");
        }

        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process.StandardInput.Close();
        _registry?.Add(Alias, _process.Id, Port);

        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        var sawServer = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            // A configuration change can stop this backend from another thread while
            // it is still coming up; the snapshot keeps that from becoming a crash.
            var process = _process;
            if (process is null)
            {
                throw new BackendStartException(
                    $"{Alias}: the start was interrupted because the configuration changed");
            }

            if (process.HasExited)
            {
                var code = process.ExitCode;
                Forget();
                throw new BackendStartException(
                    $"{Alias}: `unsloth run` exited with code {code}{Environment.NewLine}{Tail()}");
            }

            var (up, served) = await ProbeAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            sawServer |= up;
            if (served is { Count: > 0 })
            {
                AdoptServedId(served);
                _log.Write($"{Alias}: ready on :{Port} (served id: {ServedId})");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
        }

        Stop();
        var detail = sawServer && string.IsNullOrEmpty(ApiKey)
            ? "the backend answered but rejected the API key"
            : $"it did not answer on {BaseUrl}";
        throw new BackendStartException(
            $"{Alias}: not ready after {StartTimeout.TotalSeconds:F0}s — {detail}." +
            $"{Environment.NewLine}{Tail()}");
    }

    public void Stop()
    {
        if (External)
        {
            _log.Write($"{Alias}: detaching (the server was not started here)");
            External = false;
            return;
        }

        var process = _process;
        if (process is null)
        {
            return;
        }

        if (!process.HasExited)
        {
            _log.Write($"{Alias}: stopping pid {process.Id}");
            ProcessTree.Terminate(process, _log);
        }

        Forget();
    }

    public BackendStatus Status()
    {
        int? pid = null;
        try
        {
            if (_process is { HasExited: false })
            {
                pid = _process.Id;
            }
        }
        catch (InvalidOperationException)
        {
            pid = null;
        }

        return new BackendStatus(
            Alias,
            Options.Repo,
            Options.Quant,
            Options.ModelSpec,
            ServedId,
            BaseUrl,
            Port,
            IsRunning,
            External,
            pid,
            !string.IsNullOrEmpty(ApiKey),
            Options.ContextLength,
            LastUsed,
            LastError);
    }

    public void RecordError(string message) => LastError = message;

    public void ClearError() => LastError = string.Empty;

    public void Dispose()
    {
        _process?.Dispose();
        _process = null;
    }

    // --------------------------------------------------------------- plumb #
    /// <summary>`(reachable, served ids)`. Ids are null when the answer was rejected.</summary>
    private async Task<(bool Reachable, IReadOnlyList<string>? Ids)> ProbeAsync(
        TimeSpan timeout, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrEmpty(ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(timeout);

        try
        {
            using var response = await _probeClient.SendAsync(request, timer.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timer.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timer.Token)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return (true, null);
            }

            var ids = data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
            return (true, ids);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return (false, null);
        }
    }

    private void AdoptServedId(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        foreach (var candidate in new[]
                 {
                     ServedId, Options.ModelSpec, Options.Repo, Options.Repo.Split('/')[^1],
                 })
        {
            if (ids.Contains(candidate, StringComparer.Ordinal))
            {
                ServedId = candidate;
                return;
            }
        }

        ServedId = ids[0];
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is null)
        {
            return;
        }

        lock (_tailLock)
        {
            _tail.Enqueue(args.Data);
            while (_tail.Count > LogTail)
            {
                _tail.Dequeue();
            }
        }

        if (string.IsNullOrEmpty(ApiKey))
        {
            var match = UnslothKey().Match(args.Data);
            if (match.Success)
            {
                ApiKey = match.Value;
                _log.Write($"{Alias}: captured the backend API key");
            }
        }
    }

    private string Tail(int lines = 30)
    {
        lock (_tailLock)
        {
            return string.Join(Environment.NewLine, _tail.TakeLast(lines));
        }
    }

    private void Forget()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            try
            {
                _registry?.Remove(process.Id);
            }
            catch (InvalidOperationException)
            {
                // The process object was never started.
            }

            process.Dispose();
        }

        ApiKey = _configuredKey;
    }

    private string? ResolveBinary()
    {
        if (File.Exists(UnslothBin))
        {
            return UnslothBin;
        }

        return ExecutableFinder.Find(UnslothBin);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>
/// Starting a program that may be a shell script rather than a real executable.
/// <para>
/// npm and several Python installers publish their CLI entry points on Windows as
/// <c>.cmd</c> or <c>.bat</c> shims. <c>CreateProcess</c> — which is what
/// <c>UseShellExecute = false</c> uses — only ever appends <c>.exe</c> and cannot
/// run those, so the launch fails with "the system cannot find the file
/// specified" even though the file is right there on PATH. They have to be run
/// through the command interpreter instead.
/// </para>
/// </summary>
public static class ProcessLauncher
{
    public static ProcessStartInfo Build(string executable, IReadOnlyList<string> arguments)
    {
        var extension = System.IO.Path.GetExtension(executable);
        var needsShell = ExecutableFinder.IsWindows &&
                         (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

        if (!needsShell)
        {
            var direct = new ProcessStartInfo(executable);
            foreach (var argument in arguments)
            {
                direct.ArgumentList.Add(argument);
            }

            return direct;
        }

        var start = new ProcessStartInfo("cmd.exe");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}

/// <summary>PATH lookup that also understands Windows' executable extensions.</summary>
public static class ExecutableFinder
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string? Find(string command)
    {
        if (command.Contains(System.IO.Path.DirectorySeparatorChar) ||
            command.Contains(System.IO.Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? System.IO.Path.GetFullPath(command) : null;
        }

        var extensions = IsWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = System.IO.Path.Combine(directory.Trim(), command + extension);
                }
                catch (ArgumentException)
                {
                    continue;   // the PATH entry contains invalid characters
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
