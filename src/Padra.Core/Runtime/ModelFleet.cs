using System.Text.RegularExpressions;
using Padra.Core.Configuration;
using Padra.Core.Discovery;
using Padra.Core.Networking;

namespace Padra.Core.Runtime;

/// <summary>Raised when a request names a model the fleet does not serve.</summary>
public sealed class UnknownModelException(string requested, IReadOnlyList<string> available)
    : Exception($"Unknown model `{requested}`.")
{
    public string Requested { get; } = requested;

    public IReadOnlyList<string> Available { get; } = available;
}

/// <summary>Raised when the fleet is empty and something asks it to serve a request.</summary>
public sealed class NoModelsConfiguredException()
    : Exception("No models are configured. Add one in the control panel or in models.yaml.");

/// <summary>
/// Owns every backend process and the configuration behind them: resolution,
/// exclusive/concurrent loading, persistence and idle unloading.
/// </summary>
public sealed partial class ModelFleet : IAsyncDisposable
{
    // Model names the Anthropic clients send before they are told otherwise.
    [GeneratedRegex(@"^(claude[-.].*|sonnet|opus|haiku|fable)$", RegexOptions.IgnoreCase)]
    private static partial Regex AnthropicFamily();

    private readonly ActivityLog _log;
    private readonly HttpClient _probeClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _reportedUnknown = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, ModelBackend> _backends = new(StringComparer.Ordinal);

    public ModelFleet(FleetOptions options, string configPath, ActivityLog log,
        HttpClient? probeClient = null)
    {
        _log = log;
        _probeClient = probeClient ?? DirectHttp.CreateClient(TimeSpan.FromSeconds(15));
        ConfigPath = System.IO.Path.GetFullPath(configPath);
        Registry = new OrphanRegistry(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(ConfigPath) ?? ".", ".padra-backends.json"),
            log);

        Options = options;
        Apply(options);
        Registry.Sweep(_backends.Values.Select(b => b.Port).ToHashSet());
    }

    public string ConfigPath { get; }

    public OrphanRegistry Registry { get; }

    public FleetOptions Options { get; private set; }

    public GatewayOptions Gateway => Options.Gateway;

    public IReadOnlyCollection<ModelBackend> Backends => _backends.Values;

    public string DefaultAlias => Options.Gateway.DefaultModel;

    public string ApiKey =>
        Environment.GetEnvironmentVariable("GATEWAY_API_KEY") is { Length: > 0 } fromEnv
            ? fromEnv
            : Options.Gateway.ApiKey;

    /// <summary>
    /// Where the fleet finds a model's size on disk and its GGUF header, so it can
    /// tell before starting whether the model fits. Left unset, models start
    /// exactly as configured and nothing is adapted.
    /// </summary>
    public Func<ModelOptions, ModelWeights?>? WeightsSource { get; set; }

    // ---------------------------------------------------------- configuration #
    /// <summary>Adopt a validated configuration. Returns aliases whose process was stopped.</summary>
    public IReadOnlyList<string> Apply(FleetOptions options)
    {
        var stopped = new List<string>();
        var rebuilt = new Dictionary<string, ModelBackend>(StringComparer.Ordinal);

        foreach (var model in options.Models)
        {
            if (_backends.TryGetValue(model.Alias, out var existing) && existing.Matches(model))
            {
                existing.StartTimeout = TimeSpan.FromSeconds(Math.Max(5, options.Gateway.StartTimeout));
                existing.UnslothBin = Environment.GetEnvironmentVariable("UNSLOTH_BIN")
                                      ?? options.Gateway.UnslothBin;
                rebuilt[model.Alias] = existing;
                continue;
            }

            if (existing is not null && existing.IsRunning)
            {
                existing.Stop();
                stopped.Add(existing.Alias);
            }

            existing?.Dispose();
            rebuilt[model.Alias] = new ModelBackend(model, options.Gateway, _log, Registry, _probeClient);
        }

        foreach (var (alias, backend) in _backends)
        {
            if (rebuilt.ContainsKey(alias))
            {
                continue;
            }

            if (backend.IsRunning)
            {
                backend.Stop();
                stopped.Add(alias);
            }

            backend.Dispose();
        }

        _backends = rebuilt;
        Options = options;
        _reportedUnknown.Clear();
        return stopped;
    }

    public void Persist() => FleetFile.Save(ConfigPath, Options);

    public IReadOnlyList<string> Reload()
    {
        var options = FleetFile.Load(ConfigPath);
        var changed = FleetFile.EnsureApiKey(options);
        var stopped = Apply(options);
        if (changed)
        {
            Persist();
        }

        _log.Write($"configuration reloaded from {System.IO.Path.GetFileName(ConfigPath)}");
        return stopped;
    }

    public async Task<FleetMode> SetModeAsync(FleetMode mode, CancellationToken token = default)
    {
        Options.Gateway.Mode = mode;
        if (mode == FleetMode.Exclusive)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var running = _backends.Values
                    .Where(b => b.IsRunning)
                    .OrderByDescending(b => b.LastUsed ?? DateTimeOffset.MinValue)
                    .Skip(1)
                    .ToList();
                foreach (var extra in running)
                {
                    extra.Stop();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        Persist();
        _log.Write($"mode set to {mode.ToString().ToLowerInvariant()}");
        return mode;
    }

    public string SetDefault(string alias)
    {
        var backend = Resolve(alias, fallback: false);
        FleetFile.SetDefaultModel(Options, backend.Alias);
        Persist();
        _log.Write($"default model set to {backend.Alias}");
        return backend.Alias;
    }

    // ------------------------------------------------------------ resolution #
    public ModelBackend Resolve(string? requested, bool fallback = true)
    {
        if (_backends.Count == 0)
        {
            throw new NoModelsConfiguredException();
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            return DefaultBackend();
        }

        var name = requested.Trim();
        if (_backends.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var backend in _backends.Values)
        {
            var candidates = new[]
            {
                backend.Alias, backend.ServedId, backend.Options.Repo,
                backend.Options.ModelSpec, backend.Options.Repo.Split('/')[^1],
            };
            if (candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return backend;
            }
        }

        // Codex and Qwen sometimes prefix with a provider, e.g. "openai:alias".
        if (name.Contains(':', StringComparison.Ordinal))
        {
            var tail = name[(name.LastIndexOf(':') + 1)..];
            if (_backends.TryGetValue(tail, out var suffixed))
            {
                return suffixed;
            }
        }

        var allowed = fallback &&
                      (Options.Gateway.UnknownModel == UnknownModelBehaviour.Default ||
                       AnthropicFamily().IsMatch(name));
        if (allowed)
        {
            var backend = DefaultBackend();
            lock (_reportedUnknown)
            {
                if (_reportedUnknown.Add(name))
                {
                    _log.Write($"model `{name}` is not in the fleet — serving it with `{backend.Alias}`");
                }
            }

            return backend;
        }

        throw new UnknownModelException(name, _backends.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    public ModelBackend DefaultBackend() =>
        _backends.TryGetValue(DefaultAlias, out var backend) ? backend : _backends.Values.First();

    public ModelBackend? Find(string alias) =>
        _backends.TryGetValue(alias, out var backend) ? backend : null;

    // ------------------------------------------------------------- lifecycle #
    public async Task<ModelBackend> EnsureAsync(ModelBackend backend, CancellationToken token = default)
    {
        if (backend.IsRunning)
        {
            backend.LastUsed = DateTimeOffset.UtcNow;
            return backend;
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (backend.IsRunning)
            {
                backend.LastUsed = DateTimeOffset.UtcNow;
                return backend;
            }

            if (Options.Gateway.Mode == FleetMode.Exclusive)
            {
                foreach (var other in _backends.Values.Where(b => b != backend && b.IsRunning))
                {
                    // A backend mid-answer is not swapped out from under its caller:
                    // give the in-flight requests a bounded chance to finish first.
                    await DrainAsync(other, token).ConfigureAwait(false);
                    other.Stop();
                }
            }

            PlanForAvailableMemory(backend);

            try
            {
                await backend.StartAsync(token).ConfigureAwait(false);
            }
            catch (BackendStartException ex)
            {
                backend.RecordError(ex.Message);
                _log.Write($"{backend.Alias}: {ex.Message}");
                throw;
            }

            backend.ClearError();
            backend.LastUsed = DateTimeOffset.UtcNow;
            return backend;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadAsync(ModelBackend backend, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            backend.Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var backend in _backends.Values.Where(b => b.IsRunning))
            {
                backend.Stop();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How long an exclusive-mode swap waits for in-flight requests to finish.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wait, bounded, until nothing is streaming through <paramref name="backend"/>.
    /// </summary>
    private static async Task DrainAsync(ModelBackend backend, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + DrainTimeout;
        while (backend.ActiveRequests > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
    }

    /// <summary>Free VRAM held by models nobody has used for <c>idleTimeout</c> seconds.</summary>
    public async Task ReapIdleAsync(CancellationToken token = default)
    {
        var seconds = Options.Gateway.IdleTimeout;
        if (seconds <= 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-seconds);
        foreach (var backend in _backends.Values.ToList())
        {
            // ActiveRequests guards the long agentic turn: LastUsed is stamped when a
            // request begins and ends, so a 20-minute generation would otherwise look
            // idle from the outside and be killed mid-stream.
            if (backend.IsRunning && !backend.External && backend.ActiveRequests == 0 &&
                backend.LastUsed is { } used && used < cutoff)
            {
                _log.Write($"{backend.Alias}: idle for {seconds}s — unloading");
                await UnloadAsync(backend, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The estimate for one model against the memory this machine reports, or
    /// <see cref="ModelFootprint.Unknown"/> when the weights cannot be found.
    /// </summary>
    public ModelFootprint Footprint(ModelOptions model, bool refreshHardware = false)
    {
        if (WeightsSource?.Invoke(model) is not { } weights)
        {
            return ModelFootprint.Unknown;
        }

        return ModelFootprintCalculator.Estimate(model, weights.Bytes, weights.Header,
            HardwareProbe.Read(refreshHardware));
    }

    /// <summary>
    /// Ask the inference server to offload when the model is larger than the GPU.
    /// <para>
    /// Without this the server is handed an optimistic placement, crashes on load
    /// and is retried — minutes of a user's time to reach the arrangement that was
    /// predictable before the process started. It only applies when the model has
    /// not pinned <c>fit</c> itself, so an explicit setting is never overridden.
    /// </para>
    /// </summary>
    private void PlanForAvailableMemory(ModelBackend backend)
    {
        backend.AdaptiveArguments = Array.Empty<string>();

        var runtime = backend.Options.Runtime;
        if (!runtime.AllowOversize || runtime.Fit != Toggle.Auto || runtime.GpuLayers is not null)
        {
            return;
        }

        var footprint = Footprint(backend.Options);
        if (footprint.Verdict is not (FitVerdict.Offload or FitVerdict.Disk))
        {
            return;
        }

        backend.AdaptiveArguments = new[] { "--fit", "on" };
        _log.Write(
            $"{backend.Alias}: needs about {Gigabytes(footprint.TotalBytes)} GB and the largest " +
            $"GPU holds {Gigabytes(footprint.LargestGpuBytes)} GB — starting with offload enabled");
    }

    private static string Gigabytes(long bytes) =>
        (bytes / (double)(1L << 30)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Seed an empty fleet from the GGUF models already on this machine.</summary>
    public static bool AutoImport(FleetOptions options, ActivityLog log, int limit = 12)
    {
        IReadOnlyList<LocalModel> found;
        try
        {
            found = ModelScanner.Scan(options.Gateway.ExtraModelPaths);
        }
        catch (IOException ex)
        {
            log.Write($"could not scan for downloaded models: {ex.Message}");
            return false;
        }

        if (found.Count == 0)
        {
            log.Write("no downloaded models were found — add one from the control panel");
            return false;
        }

        var added = 0;
        foreach (var model in found.Take(limit))
        {
            try
            {
                var entry = FleetFile.AddModel(options, model.Repo, model.Quant,
                    contextLength: model.ContextLength);
                added++;
                log.Write($"imported `{entry.Alias}` from " +
                          $"{model.Source.ToString().ToLowerInvariant()} ({model.Spec})");
            }
            catch (FleetFileException ex)
            {
                log.Write($"skipped {model.Spec}: {ex.Message}");
            }
        }

        return added > 0;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        foreach (var backend in _backends.Values)
        {
            backend.Dispose();
        }

        _gate.Dispose();
    }
}
