using Padra.Core.Clients;
using Padra.Core.Configuration;
using Padra.Core.Networking;
using Padra.Core.Runtime;

namespace Padra.Web.Services;

/// <summary>One load or unload the launcher has asked Unsloth's server for.</summary>
public sealed class UnslothOperation
{
    public UnslothOperation(string kind, string spec, Task<UnslothOperationResult> task)
    {
        Kind = kind;
        Spec = spec;
        StartedAt = DateTimeOffset.UtcNow;
        Task = task;
    }

    /// <summary><c>load</c> or <c>unload</c>.</summary>
    public string Kind { get; }

    public string Spec { get; }

    public DateTimeOffset StartedAt { get; }

    public Task<UnslothOperationResult> Task { get; }

    public bool InFlight => !Task.IsCompleted;

    /// <summary>The outcome, once there is one. A fault is reported as a failed result.</summary>
    public UnslothOperationResult? Result => Task.Status switch
    {
        TaskStatus.RanToCompletion => Task.Result,
        TaskStatus.Faulted => UnslothOperationResult.Failed(
            Task.Exception?.GetBaseException().Message ?? "the operation failed"),
        TaskStatus.Canceled => UnslothOperationResult.Failed("the operation was cancelled"),
        _ => null,
    };
}

/// <summary>
/// Decides which server the coding agents are configured against, and builds the
/// settings for it.
/// <para>
/// Two answers are possible. The gateway puts one endpoint in front of every
/// model in <c>models.yaml</c> and swaps them on demand. Unsloth's own server
/// already speaks the OpenAI Chat Completions, OpenAI Responses and Anthropic
/// Messages APIs on a single port, so pointing the agents straight at it removes
/// this launcher from the request path entirely — at the cost of one resident
/// model at a time, which is what that server supports. That server may well be
/// on another machine, so the model it has resident is chosen and loaded through
/// its own API rather than by running <c>unsloth</c> here.
/// </para>
/// </summary>
public sealed class EndpointResolver(ModelFleet fleet, IHttpClientFactory factory, ActivityLog log)
{
    /// <summary>Named client, so the probe gets its own short timeout.</summary>
    public const string ClientName = "unsloth-server";

    /// <summary>
    /// Named client for loads and unloads: the server answers those only once the
    /// weights are in (or out of) memory, which takes minutes for a large model.
    /// </summary>
    public const string LongClientName = "unsloth-server-long";

    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _operationLock = new();

    private UnslothServerStatus? _cache;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private UnslothOperation? _operation;

    public EndpointSource Source => fleet.Gateway.EndpointSource;

    /// <summary>The Unsloth server's origin, as configured or as Unsloth defaults it.</summary>
    public string UnslothOrigin =>
        fleet.Gateway.UnslothServerUrl is { Length: > 0 } configured
            ? UnslothServer.Normalise(configured)
            : UnslothServer.DefaultBaseUrl();

    /// <summary>True when the server runs on this machine, so a local <c>unsloth</c> command could start it.</summary>
    public bool UnslothIsLocal => UnslothServer.IsLocalOrigin(UnslothOrigin);

    /// <summary>The model the user picked for direct mode, as stored in <c>models.yaml</c>.</summary>
    public string SelectedModel => fleet.Gateway.UnslothModel;

    /// <summary>The load or unload most recently asked for, finished or not.</summary>
    public UnslothOperation? Operation
    {
        get
        {
            lock (_operationLock)
            {
                return _operation;
            }
        }
    }

    /// <summary>Ask Unsloth's server what it is serving, at most once every few seconds.</summary>
    public async Task<UnslothServerStatus> UnslothStatusAsync(bool refresh = false,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache is { } cached &&
                DateTimeOffset.UtcNow - _cachedAt < Freshness)
            {
                return cached;
            }

            using var http = factory.CreateClient(ClientName);
            _cache = await UnslothServer
                .ProbeAsync(http, UnslothOrigin, fleet.Gateway.UnslothApiKey, token)
                .ConfigureAwait(false);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _cache = null;
        _cachedAt = DateTimeOffset.MinValue;
    }

    /// <summary>How far the load in flight has got, or nothing when none is.</summary>
    public async Task<UnslothLoadProgress> LoadProgressAsync(CancellationToken token = default)
    {
        var key = UnslothServer.ResolveKey(UnslothOrigin, fleet.Gateway.UnslothApiKey).Key;
        if (key.Length == 0)
        {
            return UnslothLoadProgress.None;
        }

        using var http = factory.CreateClient(ClientName);
        return await UnslothServer.LoadProgressAsync(http, UnslothOrigin, key, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The settings to write for the current source.
    /// <para>
    /// In direct mode the models come from the server itself, because Unsloth
    /// resolves its own repository ids and knows nothing about this launcher's
    /// aliases. If that server cannot be reached the caller is told so rather than
    /// handed a configuration pointing at nothing.
    /// </para>
    /// </summary>
    public async Task<ClientContext> ContextAsync(string? scope, CancellationToken token = default)
    {
        var projectDirectory = Path.GetDirectoryName(fleet.ConfigPath);

        if (Source == EndpointSource.Gateway)
        {
            var baseUrl = string.Equals(scope, "lan", StringComparison.OrdinalIgnoreCase)
                ? NetworkInfo.PreferredLanUrl(fleet.Gateway.Host, fleet.Gateway.Port)
                : ClientContext.DefaultBaseUrl(fleet.Options);
            return ClientContext.FromFleet(fleet.Options, baseUrl, projectDirectory);
        }

        var status = await UnslothStatusAsync(refresh: true, token).ConfigureAwait(false);
        if (!status.Reachable)
        {
            throw new EndpointUnavailableException(
                $"Unsloth's server is not answering on {status.BaseUrl}. " +
                (UnslothServer.IsLocalOrigin(status.BaseUrl)
                    ? "Start it with `unsloth run --model <repo> --disable-tools`, "
                    : "Start Unsloth Studio on that machine and allow LAN access, ") +
                "or switch the endpoint back to this gateway.");
        }

        if (!status.Authorised)
        {
            throw new EndpointUnavailableException(KeyProblem(status));
        }

        var served = ServedModels(status);
        if (served.Count == 0)
        {
            throw new EndpointUnavailableException(status.Models.Count == 0
                ? $"Unsloth's server on {status.BaseUrl} lists no model yet — download one in Unsloth Studio first."
                : $"Unsloth's server on {status.BaseUrl} lists no chat model — its library holds only image, speech or embedding models, which a coding agent cannot talk to.");
        }

        // A LAN scope has to name a reachable address, and the Unsloth server may be
        // bound to loopback; the origin it was probed on is the honest answer.
        var origin = status.BaseUrl;
        if (string.Equals(scope, "lan", StringComparison.OrdinalIgnoreCase))
        {
            origin = LanOrigin(origin);
        }

        return ClientContext.ForUnsloth(origin, ResolvedKey(status), served,
            PreferredModel(status), projectDirectory);
    }

    /// <summary>
    /// The server's models as the agents should see them.
    /// <para>
    /// Only the chat models: that server's list is its entire library, image,
    /// speech and embedding models included, and none of those answer a chat
    /// request. The resident model then carries the context the runtime actually
    /// allocated, which a fit-to-VRAM load makes smaller than the header's figure —
    /// and that is the window Codex and Qwen Code have to plan around.
    /// </para>
    /// </summary>
    private static IReadOnlyList<UnslothModel> ServedModels(UnslothServerStatus status)
    {
        var runtime = status.Runtime;
        var chat = status.Models.Where(m => m.IsChat).ToList();

        if (runtime.ContextLength is not { } context || context <= 0 || runtime.ActiveSpec.Length == 0)
        {
            return chat;
        }

        return chat
            .Select(m => m.IsNamed(runtime.ActiveSpec)
                ? m with { ContextLength = context }
                : m)
            .ToList();
    }

    /// <summary>The key the agents will be given, resolved the same way the probe did.</summary>
    public string ResolvedKey(UnslothServerStatus status) =>
        UnslothServer.ResolveKey(status.BaseUrl, fleet.Gateway.UnslothApiKey).Key;

    /// <summary>
    /// Which model the agents are pointed at: the one chosen in the panel when the
    /// server lists it, else whatever the runtime has resident, else the first
    /// model the server lists.
    /// </summary>
    public string? PreferredModel(UnslothServerStatus status)
    {
        if (status.Find(SelectedModel) is { } chosen)
        {
            return chosen.Spec;
        }

        if (status.Runtime.ActiveSpec.Length > 0 && status.Find(status.Runtime.ActiveSpec) is { } active)
        {
            return active.Spec;
        }

        // Falling back, prefer something an agent can actually converse with: the
        // library may well have an embedding or image model sitting first.
        var loaded = status.LoadedModels.FirstOrDefault(m => m.IsChat)
                     ?? status.LoadedModels.FirstOrDefault();
        if (loaded is not null)
        {
            return loaded.Spec;
        }

        return (status.Models.FirstOrDefault(m => m.IsChat)
                ?? status.Models.FirstOrDefault())?.Spec;
    }

    /// <summary>The listed model the agents would be configured with right now.</summary>
    public UnslothModel? EffectiveModel(UnslothServerStatus status) =>
        status.Find(PreferredModel(status));

    // ------------------------------------------------------------ selection #
    /// <summary>
    /// Make <paramref name="name"/> the model the agents are configured with and
    /// remember the choice in <c>models.yaml</c>. The server must list it: a name it
    /// does not know would be written into every agent and fail on first use.
    /// </summary>
    public async Task<UnslothModel> SelectModelAsync(string name, CancellationToken token = default)
    {
        var status = await UnslothStatusAsync(refresh: true, token).ConfigureAwait(false);
        var model = Known(status, name);

        fleet.Options.Gateway.UnslothModel = model.Spec;
        fleet.Persist();
        log.Write($"the agents will be configured with `{model.Spec}` on {status.BaseUrl}");
        return model;
    }

    /// <summary>
    /// Ask the server to make <paramref name="name"/> resident, in the background.
    /// Returns the operation to watch; a load already in flight is returned as is
    /// rather than stacked behind it.
    /// </summary>
    public async Task<UnslothOperation> StartLoadAsync(string name, bool select = true,
        CancellationToken token = default)
    {
        lock (_operationLock)
        {
            // One thing at a time: a second request while a load runs changes
            // nothing — not even the choice — and reports what is in flight.
            if (_operation is { InFlight: true } busy)
            {
                return busy;
            }
        }

        var status = await UnslothStatusAsync(refresh: true, token).ConfigureAwait(false);
        RequireUsable(status);
        var model = Known(status, name);

        if (select && !string.Equals(fleet.Options.Gateway.UnslothModel, model.Spec, StringComparison.Ordinal))
        {
            fleet.Options.Gateway.UnslothModel = model.Spec;
            fleet.Persist();
        }

        lock (_operationLock)
        {
            if (_operation is { InFlight: true } busy)
            {
                return busy;
            }

            var key = ResolvedKey(status);
            var request = new UnslothLoadRequest(model.Id, model.Quant);
            log.Write($"asking {status.BaseUrl} to load `{model.Spec}` — a large model takes minutes");
            _operation = new UnslothOperation("load", model.Spec,
                RunAsync((http, deadline) =>
                        UnslothServer.LoadAsync(http, status.BaseUrl, key, request, deadline),
                    $"`{model.Spec}` is resident on {status.BaseUrl}"));
            return _operation;
        }
    }

    /// <summary>Ask the server to free <paramref name="name"/>, in the background.</summary>
    public async Task<UnslothOperation> StartUnloadAsync(string name, CancellationToken token = default)
    {
        lock (_operationLock)
        {
            if (_operation is { InFlight: true } busy)
            {
                return busy;
            }
        }

        var status = await UnslothStatusAsync(refresh: true, token).ConfigureAwait(false);
        RequireUsable(status);
        var model = Known(status, name);

        lock (_operationLock)
        {
            if (_operation is { InFlight: true } busy)
            {
                return busy;
            }

            var key = ResolvedKey(status);
            log.Write($"asking {status.BaseUrl} to unload `{model.Spec}`");
            _operation = new UnslothOperation("unload", model.Spec,
                RunAsync((http, deadline) =>
                        UnslothServer.UnloadAsync(http, status.BaseUrl, key, model.Spec, token: deadline),
                    $"`{model.Spec}` was unloaded from {status.BaseUrl}"));
            return _operation;
        }
    }

    /// <summary>Wait for the operation in flight, if any, and return its outcome.</summary>
    public async Task<UnslothOperationResult?> AwaitOperationAsync(CancellationToken token = default)
    {
        var operation = Operation;
        if (operation is null)
        {
            return null;
        }

        await operation.Task.WaitAsync(token).ConfigureAwait(false);
        return operation.Result;
    }

    /// <summary>
    /// Run one long server operation to completion, detached from the request
    /// that asked for it: closing the panel must not abandon a load half way. The
    /// only clock is the fleet's own start timeout, the same budget a local
    /// <c>unsloth run</c> gets.
    /// </summary>
    private async Task<UnslothOperationResult> RunAsync(
        Func<HttpClient, CancellationToken, Task<UnslothOperationResult>> action, string success)
    {
        await System.Threading.Tasks.Task.Yield();
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(120, fleet.Gateway.StartTimeout)));
        try
        {
            using var http = factory.CreateClient(LongClientName);
            var result = await action(http, deadline.Token).ConfigureAwait(false);
            log.Write(result.Ok ? success : $"Unsloth's server did not do it: {result.Detail}");
            return result;
        }
        catch (OperationCanceledException)
        {
            var message = "the server did not finish within the start timeout — it may still be loading";
            log.Write($"Unsloth's server did not do it: {message}");
            return UnslothOperationResult.Failed(message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or System.Text.Json.JsonException or IOException)
        {
            log.Write($"Unsloth's server did not do it: {ex.Message}");
            return UnslothOperationResult.Failed(ex.Message);
        }
        finally
        {
            Invalidate();
        }
    }

    // ----------------------------------------------------------------- keys #
    /// <summary>
    /// Sign in to Unsloth Studio and create an API key for the agents, then pin it
    /// in <c>models.yaml</c> so every later probe and install uses it.
    /// </summary>
    /// <exception cref="UnslothServerException">The server refused or could not be reached.</exception>
    public async Task<string> MintKeyAsync(string username, string password,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new UnslothServerException("a username and password are needed to sign in");
        }

        using var http = factory.CreateClient(ClientName);
        var key = await UnslothServer.MintKeyAsync(http, UnslothOrigin, username.Trim(), password,
            token: token).ConfigureAwait(false);

        fleet.Options.Gateway.UnslothApiKey = key;
        fleet.Persist();
        Invalidate();
        log.Write($"created an API key on {UnslothOrigin} and saved it to models.yaml");
        return key;
    }

    // ---------------------------------------------------------------- plumb #
    private static void RequireUsable(UnslothServerStatus status)
    {
        if (!status.Reachable)
        {
            throw new EndpointUnavailableException(
                $"Unsloth's server is not answering on {status.BaseUrl}.");
        }

        if (!status.Authorised)
        {
            throw new EndpointUnavailableException(KeyProblem(status));
        }
    }

    /// <summary>Why the server cannot be used: no key at all, or one it refuses.</summary>
    private static string KeyProblem(UnslothServerStatus status) => !status.HaveKey
        ? $"No API key is known for {status.BaseUrl} — sign in from the panel to create one, or paste one in."
        : status.Detail.Length > 0
            ? $"Unsloth's server on {status.BaseUrl} refused the key: {status.Detail}"
            : $"Unsloth's server on {status.BaseUrl} refused the key.";

    private static UnslothModel Known(UnslothServerStatus status, string name)
    {
        if (!status.Reachable || !status.Authorised)
        {
            RequireUsable(status);
        }

        return status.Find(name)
               ?? throw new EndpointUnavailableException(
                   $"Unsloth's server on {status.BaseUrl} does not list a model named `{name.Trim()}`.");
    }

    private string LanOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
        {
            return origin;
        }

        if (!NetworkHosts.IsLoopback(parsed.Host))
        {
            return origin;
        }

        var lan = NetworkInfo.EndpointUrls("0.0.0.0", parsed.Port).Lan
            .FirstOrDefault(e => e.Primary) ?? NetworkInfo.EndpointUrls("0.0.0.0", parsed.Port)
            .Lan.FirstOrDefault();
        if (lan is null)
        {
            return origin;
        }

        log.Write(
            "the Unsloth server is bound to loopback; a LAN bundle needs it started with -H 0.0.0.0");
        return $"{parsed.Scheme}://{lan.Address}:{parsed.Port}";
    }
}

/// <summary>Raised when the chosen endpoint cannot be configured against right now.</summary>
public sealed class EndpointUnavailableException(string message) : Exception(message);
