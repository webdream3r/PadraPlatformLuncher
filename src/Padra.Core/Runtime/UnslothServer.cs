using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Padra.Core.Configuration;

namespace Padra.Core.Runtime;

/// <summary>One model as Unsloth's own server reports it.</summary>
/// <param name="Task">
/// The Hugging Face pipeline tag Unsloth reports for models that are not
/// conversational — <c>text-to-image</c>, <c>automatic-speech-recognition</c> and
/// the like. It is absent for text models, which is the overwhelming majority.
/// </param>
public sealed record UnslothModel(
    string Id, string Quant, int? ContextLength, bool Loaded, string Task = "")
{
    /// <summary>
    /// What a client should send as <c>model</c>. Unsloth keeps the id bare for
    /// OpenAI compatibility and lets a caller pin one quantisation by appending it.
    /// </summary>
    public string Spec => Quant.Length > 0 ? $"{Id}:{Quant}" : Id;

    /// <summary>
    /// Whether a coding agent can hold a conversation with this model.
    /// <para>
    /// Unsloth's model list is its whole library, not just the chat models: image
    /// and speech models carry a <c>task</c> that says so, and embedding and
    /// reranking models carry none but answer <c>/v1/embeddings</c> rather than
    /// <c>/v1/chat/completions</c>. Offering any of them to Codex, Qwen Code,
    /// Claude Code or Continue produces a model the user can select and cannot use,
    /// so only chat models are written into an agent's configuration.
    /// </para>
    /// </summary>
    public bool IsChat =>
        (Task.Length == 0 || Task is "text-generation" or "text2text-generation") &&
        !Id.Contains("embedding", StringComparison.OrdinalIgnoreCase) &&
        !Id.Contains("-embed", StringComparison.OrdinalIgnoreCase) &&
        !Id.Contains("reranker", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="name"/> is this model's spec or its bare id.</summary>
    public bool IsNamed(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (string.Equals(Spec, name.Trim(), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Id, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What the inference runtime behind Unsloth's server is doing, from
/// <c>GET /v1/status</c>: the resident model, anything still loading, and the
/// context the runtime actually has.
/// </summary>
public sealed record UnslothRuntime(
    string ActiveModel,
    string ModelIdentifier,
    string GgufVariant,
    IReadOnlyList<string> Loaded,
    IReadOnlyList<string> Loading,
    int? ContextLength,
    int? MaxContextLength,
    int? NativeContextLength,
    bool SupportsTools,
    bool SupportsReasoning)
{
    public static readonly UnslothRuntime Empty = new(
        string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(),
        null, null, null, false, false);

    /// <summary>The resident model as a client should name it.</summary>
    public string ActiveSpec
    {
        get
        {
            var id = ModelIdentifier.Length > 0 ? ModelIdentifier : ActiveModel;
            if (id.Length == 0)
            {
                return string.Empty;
            }

            return GgufVariant.Length > 0 && !id.Contains(':', StringComparison.Ordinal)
                ? $"{id}:{GgufVariant}"
                : id;
        }
    }

    public bool IsLoading => Loading.Count > 0;
}

/// <summary>Progress of a model load in flight, from <c>GET /v1/load-progress</c>.</summary>
public sealed record UnslothLoadProgress(string Phase, long BytesLoaded, long BytesTotal, double Fraction)
{
    public static readonly UnslothLoadProgress None = new(string.Empty, 0, 0, 0);

    public bool InFlight => Phase.Length > 0 && Phase != "ready";
}

/// <summary>What Unsloth's own server is doing right now.</summary>
public sealed record UnslothServerStatus(
    string BaseUrl,
    bool Reachable,
    bool Authorised,
    bool HaveKey,
    string KeySource,
    IReadOnlyList<UnslothModel> Models,
    string Detail,
    UnslothRuntime Runtime,
    string ServerName = "",
    string ServerVersion = "")
{
    public UnslothServerStatus(
        string baseUrl, bool reachable, bool authorised, bool haveKey, string keySource,
        IReadOnlyList<UnslothModel> models, string detail)
        : this(baseUrl, reachable, authorised, haveKey, keySource, models, detail, UnslothRuntime.Empty)
    {
    }

    public static UnslothServerStatus Unreachable(string baseUrl, string detail) =>
        new(baseUrl, false, false, false, string.Empty, Array.Empty<UnslothModel>(), detail);

    /// <summary>Models actually resident, which is what an agent can talk to now.</summary>
    public IReadOnlyList<UnslothModel> LoadedModels => Models.Where(m => m.Loaded).ToList();

    public bool Usable => Reachable && Authorised && Models.Count > 0;

    /// <summary>The listed model named by <paramref name="name"/>, or null.</summary>
    public UnslothModel? Find(string? name) => Models.FirstOrDefault(m => m.IsNamed(name));

    /// <summary>True when the runtime is still bringing <paramref name="name"/> up.</summary>
    public bool IsLoading(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Runtime.Loading.Any(l => Names(name).Contains(l, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> Names(string name)
    {
        var trimmed = name.Trim();
        yield return trimmed;
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0)
        {
            yield return trimmed[..colon];
        }
    }
}

/// <summary>A request to make one model resident on Unsloth's server.</summary>
public sealed record UnslothLoadRequest(
    string ModelPath,
    string Quant = "",
    int? ContextLength = null,
    string KvCacheType = "",
    GpuMemoryMode GpuMemoryMode = GpuMemoryMode.Auto,
    int? GpuLayers = null,
    int? CpuMoeLayers = null,
    IReadOnlyList<string>? ExtraArguments = null,
    bool ForceReload = false,
    bool ForceCancelActive = false)
{
    /// <summary>Parse <c>org/repo:QUANT</c> into a request.</summary>
    public static UnslothLoadRequest ForSpec(string spec)
    {
        var trimmed = spec.Trim();
        var colon = trimmed.LastIndexOf(':');
        return colon > 0 && !trimmed[(colon + 1)..].Contains('/')
            ? new UnslothLoadRequest(trimmed[..colon], trimmed[(colon + 1)..])
            : new UnslothLoadRequest(trimmed);
    }

    public string Spec => Quant.Length > 0 ? $"{ModelPath}:{Quant}" : ModelPath;
}

/// <summary>The outcome of asking Unsloth's server to load or unload a model.</summary>
public sealed record UnslothOperationResult(
    bool Ok,
    string Status,
    string Model,
    string DisplayName,
    int? ContextLength,
    string Detail)
{
    public static UnslothOperationResult Failed(string detail) =>
        new(false, "error", string.Empty, string.Empty, null, detail);
}

/// <summary>Raised when Unsloth's server refuses a request or cannot be reached.</summary>
public sealed class UnslothServerException(string message, HttpStatusCode? status = null)
    : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Unsloth's own inference server, talked to directly.
/// <para>
/// It already serves everything a coding agent needs on one port: the OpenAI
/// Chat Completions and Responses APIs, and the Anthropic Messages API. Nothing
/// has to sit in front of it, so the launcher can simply point Codex, Qwen Code
/// and Claude Code at it and stay out of the request path entirely. This class is
/// what finds it, authenticates to it, lists what it is serving, and — because the
/// server may be on another machine where no <c>unsloth</c> command can be run from
/// here — asks it to load and unload models through its own API.
/// </para>
/// </summary>
public static class UnslothServer
{
    /// <summary>Where <c>unsloth run</c> and <c>unsloth studio</c> listen unless told otherwise.</summary>
    public const string DefaultOrigin = "http://127.0.0.1:8888";

    /// <summary>The prefix every Unsloth-issued key carries.</summary>
    public const string KeyPrefix = "sk-unsloth-";

    /// <summary>
    /// The variable Unsloth's own Codex recipe names in <c>env_key</c>. Matching it
    /// means a config this launcher writes and one written by
    /// <c>unsloth start codex</c> look for the same variable.
    /// </summary>
    public const string CodexKeyVariable = "UNSLOTH_STUDIO_AUTH_TOKEN";

    /// <summary>Unauthenticated liveness probe. Note the prefix: it is not <c>/health</c>.</summary>
    public const string HealthPath = "/api/health";

    /// <summary>
    /// The budget for one probe call. <c>/v1/models</c> walks that server's model
    /// library, which on a cold cache takes seconds even on the local network.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The budget for liveness alone. It only separates "nothing is listening" from
    /// "listening but the key is wrong", so it must answer quickly and must not
    /// inherit the longer catalog budget — a panel waiting on it is a panel that
    /// looks hung.
    /// </summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The label a key minted by this launcher carries in Unsloth's key list.</summary>
    public const string MintedKeyName = "Padra Platform Launcher";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The origin to use, honouring Unsloth's own environment override.</summary>
    public static string DefaultBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("UNSLOTH_STUDIO_URL");
        return string.IsNullOrWhiteSpace(configured) ? DefaultOrigin : Normalise(configured);
    }

    /// <summary>Strip a trailing slash or <c>/v1</c> so one origin is stored, not three.</summary>
    public static string Normalise(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        if (trimmed.Length > 0 && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            // `192.168.1.20:8888` typed without a scheme is a server, not a mistake.
            trimmed = "http://" + trimmed;
        }

        return trimmed.Length > 0 ? trimmed : DefaultOrigin;
    }

    /// <summary>The OpenAI-compatible base a client is given.</summary>
    public static string OpenAiBase(string baseUrl) => Normalise(baseUrl) + "/v1";

    /// <summary>True when the origin names this machine, so a local <c>unsloth</c> command can serve it.</summary>
    public static bool IsLocalOrigin(string baseUrl) =>
        Uri.TryCreate(Normalise(baseUrl), UriKind.Absolute, out var uri) &&
        (Networking.NetworkHosts.IsLoopback(uri.Host) || uri.IsLoopback);

    /// <summary>Unsloth Studio's data directory, following its own resolution order.</summary>
    public static string StudioHome()
    {
        foreach (var name in new[] { "UNSLOTH_STUDIO_HOME", "STUDIO_HOME" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } configured)
            {
                return configured;
            }
        }

        return Path.Combine(Discovery.ModelScanner.UserHome(), ".unsloth", "studio");
    }

    /// <summary>Where <c>unsloth start</c> caches the keys it minted, in the clear.</summary>
    public static string CachedKeyPath() =>
        Path.Combine(StudioHome(), "auth", "agent_api_key.json");

    /// <summary>
    /// The key to authenticate with, and where it came from.
    /// <para>
    /// A value pinned in <c>models.yaml</c> wins, then the two environment
    /// variables Unsloth's own tooling reads, then the cache
    /// <c>unsloth start</c> writes when it mints a key for a coding agent. Keys are
    /// stored hashed in Unsloth's database, so that cache is the only place a
    /// usable key exists on disk.
    /// </para>
    /// </summary>
    public static (string Key, string Source) ResolveKey(string baseUrl, string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return (configured!.Trim(), "models.yaml");
        }

        foreach (var name in new[] { "UNSLOTH_API_KEY", CodexKeyVariable })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                return (value.Trim(), name);
            }
        }

        if (ReadCachedKey(baseUrl) is { Length: > 0 } cached)
        {
            return (cached, Path.GetFileName(CachedKeyPath()));
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// The newest usable key <c>unsloth start</c> cached for this server.
    /// <para>
    /// The file is keyed by base URL and holds two buckets — keys the user supplied
    /// and keys Unsloth minted. Entries have been written both as bare strings and
    /// as objects, so both are accepted rather than assuming one shape.
    /// </para>
    /// </summary>
    public static string? ReadCachedKey(string baseUrl)
    {
        var path = CachedKeyPath();
        JsonNode? document;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            document = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        if (document?["servers"] is not JsonObject servers)
        {
            return null;
        }

        var origin = Normalise(baseUrl);
        var candidates = new List<string>();

        foreach (var (server, buckets) in servers)
        {
            if (!string.Equals(Normalise(server), origin, StringComparison.OrdinalIgnoreCase) ||
                buckets is not JsonObject bucketed)
            {
                continue;
            }

            // A key the user supplied is preferred: it is the one they expect to see.
            foreach (var bucket in new[] { "saved", "minted" })
            {
                if (bucketed[bucket] is JsonArray entries)
                {
                    candidates.AddRange(entries.Select(KeyOf).Where(k => k.Length > 0));
                }
            }
        }

        return candidates.LastOrDefault(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal))
               ?? candidates.LastOrDefault();
    }

    private static string KeyOf(JsonNode? entry)
    {
        if (entry is JsonValue value)
        {
            return value.TryGetValue<string>(out var text) ? text : string.Empty;
        }

        if (entry is JsonObject item)
        {
            foreach (var field in new[] { "key", "api_key", "apiKey", "value", "token" })
            {
                if (item[field] is JsonValue candidate &&
                    candidate.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- probe #
    /// <summary>
    /// Ask the server what it is and what it is serving. Never throws: an
    /// unreachable or unauthenticated server is a status, not an error.
    /// </summary>
    /// <param name="healthTimeout">
    /// The liveness budget, when it should not be <see cref="HealthTimeout"/>.
    /// </param>
    public static async Task<UnslothServerStatus> ProbeAsync(
        HttpClient http, string baseUrl, string? configuredKey = null,
        CancellationToken token = default, TimeSpan? healthTimeout = null)
    {
        var livenessBudget = healthTimeout ?? HealthTimeout;
        var origin = Normalise(baseUrl);
        var (key, source) = ResolveKey(origin, configuredKey);

        // The health endpoint needs no credentials, which separates "nothing is
        // listening" from "listening but the key is wrong" — two problems with
        // completely different fixes.
        string serverName;
        string serverVersion;
        using (var liveness = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            liveness.CancelAfter(livenessBudget);
            try
            {
                using var health = await http.GetAsync(origin + HealthPath, liveness.Token)
                    .ConfigureAwait(false);
                if (!health.IsSuccessStatusCode)
                {
                    return UnslothServerStatus.Unreachable(origin,
                        $"{HealthPath} answered {(int)health.StatusCode}");
                }

                var body = await health.Content.ReadAsStringAsync(liveness.Token)
                    .ConfigureAwait(false);
                (serverName, serverVersion) = ParseHealth(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return UnslothServerStatus.Unreachable(origin,
                    liveness.IsCancellationRequested && !token.IsCancellationRequested
                        ? $"{HealthPath} did not answer within {livenessBudget.TotalSeconds:0.##} seconds"
                        : ex.Message);
            }
        }

        if (key.Length == 0)
        {
            return new UnslothServerStatus(origin, true, false, false, string.Empty,
                Array.Empty<UnslothModel>(),
                "no API key was found — sign in to the server to create one, or paste one here",
                UnslothRuntime.Empty, serverName, serverVersion);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiBase(origin) + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new UnslothServerStatus(origin, true, false, true, source,
                    Array.Empty<UnslothModel>(),
                    $"the server refused the key from {source}",
                    UnslothRuntime.Empty, serverName, serverVersion);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UnslothServerStatus(origin, true, false, true, source,
                    Array.Empty<UnslothModel>(),
                    $"/v1/models answered {(int)response.StatusCode}",
                    UnslothRuntime.Empty, serverName, serverVersion);
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var models = ParseModels(body);

            // The runtime view is optional: `unsloth run` without Studio may not
            // answer it, and the model list already says what is resident.
            var runtime = await RuntimeAsync(http, origin, key, token).ConfigureAwait(false);
            models = ReconcileLoaded(models, runtime);

            return new UnslothServerStatus(origin, true, true, true, source, models, string.Empty,
                runtime, serverName, serverVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       or JsonException)
        {
            return new UnslothServerStatus(origin, true, false, key.Length > 0, source,
                Array.Empty<UnslothModel>(), ex.Message, UnslothRuntime.Empty, serverName,
                serverVersion);
        }
    }

    private static (string Name, string Version) ParseHealth(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject health)
            {
                return (Text(health, "service"), Text(health, "version"));
            }
        }
        catch (JsonException)
        {
            // A plain-text health answer is still a healthy server.
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// A model list that agrees with the runtime: a model the runtime reports as
    /// resident is marked loaded even when the list omits the flag.
    /// </summary>
    private static IReadOnlyList<UnslothModel> ReconcileLoaded(
        IReadOnlyList<UnslothModel> models, UnslothRuntime runtime)
    {
        if (runtime.Loaded.Count == 0 && runtime.ActiveSpec.Length == 0)
        {
            return models;
        }

        var resident = new HashSet<string>(runtime.Loaded, StringComparer.OrdinalIgnoreCase);
        if (runtime.ActiveSpec.Length > 0)
        {
            resident.Add(runtime.ActiveSpec);
            resident.Add(runtime.ModelIdentifier);
            resident.Add(runtime.ActiveModel);
        }

        return models
            .Select(m => m.Loaded || resident.Contains(m.Spec) || resident.Contains(m.Id)
                ? m with { Loaded = true }
                : m)
            .ToList();
    }

    /// <summary>Read an OpenAI model list, keeping the extra fields Unsloth adds.</summary>
    public static IReadOnlyList<UnslothModel> ParseModels(string body)
    {
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Array.Empty<UnslothModel>();
        }

        if (document?["data"] is not JsonArray data)
        {
            return Array.Empty<UnslothModel>();
        }

        var models = new List<UnslothModel>();
        foreach (var entry in data)
        {
            if (entry is not JsonObject item ||
                item["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<string>(out var id) ||
                string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            id = id.Trim();
            var quant = Text(item, "quant");
            if (quant.Length == 0)
            {
                quant = Text(item, "gguf_variant");
            }

            // An id may already carry its quantisation as `org/repo:QUANT`.
            var colon = id.LastIndexOf(':');
            if (quant.Length == 0 && colon > 0 && !id[(colon + 1)..].Contains('/'))
            {
                quant = id[(colon + 1)..];
                id = id[..colon];
            }

            var model = new UnslothModel(
                id,
                quant,
                Number(item, "context_length") ?? Number(item, "max_context_length")
                ?? Number(item, "native_context_length"),
                // A model in the list but not resident is one Unsloth found on disk.
                item["loaded"] is not JsonValue loaded ||
                !loaded.TryGetValue<bool>(out var isLoaded) || isLoaded,
                Text(item, "task"));

            if (models.Any(m => string.Equals(m.Spec, model.Spec, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            models.Add(model);
        }

        return models;
    }

    /// <summary>The runtime's own view, or <see cref="UnslothRuntime.Empty"/> when it has none.</summary>
    public static async Task<UnslothRuntime> RuntimeAsync(
        HttpClient http, string baseUrl, string key, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiBase(baseUrl) + "/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UnslothRuntime.Empty;
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return ParseRuntime(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return UnslothRuntime.Empty;
        }
    }

    public static UnslothRuntime ParseRuntime(string body)
    {
        JsonObject? item;
        try
        {
            item = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return UnslothRuntime.Empty;
        }

        if (item is null)
        {
            return UnslothRuntime.Empty;
        }

        return new UnslothRuntime(
            Text(item, "active_model"),
            Text(item, "model_identifier"),
            Text(item, "gguf_variant"),
            Strings(item, "loaded"),
            Strings(item, "loading"),
            Number(item, "context_length"),
            Number(item, "max_context_length"),
            Number(item, "native_context_length"),
            Flag(item, "supports_tools"),
            Flag(item, "supports_reasoning"));
    }

    /// <summary>How far a load in flight has got, or <see cref="UnslothLoadProgress.None"/>.</summary>
    public static async Task<UnslothLoadProgress> LoadProgressAsync(
        HttpClient http, string baseUrl, string key, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            OpenAiBase(baseUrl) + "/load-progress");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UnslothLoadProgress.None;
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (JsonNode.Parse(body) is not JsonObject item)
            {
                return UnslothLoadProgress.None;
            }

            var phase = Text(item, "phase");
            var loaded = Long(item, "bytes_loaded");
            var total = Long(item, "bytes_total");
            var fraction = item["fraction"] is JsonValue value && value.TryGetValue<double>(out var f)
                ? Math.Clamp(f, 0, 1)
                : total > 0 ? Math.Clamp((double)loaded / total, 0, 1) : 0;
            return new UnslothLoadProgress(phase, loaded, total, fraction);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return UnslothLoadProgress.None;
        }
    }

    // ------------------------------------------------------------ load/unload #
    /// <summary>
    /// Ask the server to make a model resident. This is a long call: the server
    /// answers only once the weights are in memory, padding the response to keep
    /// proxies alive, so <paramref name="http"/> must allow minutes, not seconds.
    /// </summary>
    public static async Task<UnslothOperationResult> LoadAsync(
        HttpClient http, string baseUrl, string key, UnslothLoadRequest load,
        CancellationToken token = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model_path"] = load.ModelPath,
            ["max_seq_length"] = load.ContextLength is > 0 ? load.ContextLength : 0,
            ["gpu_memory_mode"] = load.GpuMemoryMode.ToString().ToLowerInvariant(),
            ["force_reload"] = load.ForceReload,
            ["force_cancel_active"] = load.ForceCancelActive,
        };

        if (load.Quant.Length > 0)
        {
            payload["gguf_variant"] = load.Quant;
        }

        if (load.KvCacheType.Length > 0)
        {
            payload["cache_type_kv"] = load.KvCacheType;
        }

        if (load.GpuMemoryMode == GpuMemoryMode.Manual)
        {
            payload["gpu_layers"] = load.GpuLayers ?? RuntimeOptions.AllGpuLayers;
            payload["n_cpu_moe"] = load.CpuMoeLayers is > 0 ? load.CpuMoeLayers : 0;
        }

        if (load.ExtraArguments is { Count: > 0 } extra)
        {
            payload["llama_extra_args"] = extra;
        }

        return await PostAsync(http, baseUrl, key, "/load", payload, token).ConfigureAwait(false);
    }

    /// <summary>Ask the server to free the memory a model holds.</summary>
    public static async Task<UnslothOperationResult> UnloadAsync(
        HttpClient http, string baseUrl, string key, string modelPath,
        bool forceCancelActive = false, CancellationToken token = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model_path"] = UnslothLoadRequest.ForSpec(modelPath).ModelPath,
            ["force_cancel_active"] = forceCancelActive,
        };

        return await PostAsync(http, baseUrl, key, "/unload", payload, token).ConfigureAwait(false);
    }

    private static async Task<UnslothOperationResult> PostAsync(
        HttpClient http, string baseUrl, string key, string route,
        Dictionary<string, object?> payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiBase(baseUrl) + route)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return UnslothOperationResult.Failed($"the server could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return UnslothOperationResult.Failed(
                "the server did not finish in time — it may still be loading; check again in a moment");
        }

        using (response)
        {
            // The server pads a slow answer with whitespace so proxies keep the
            // connection; the JSON is whatever is left once it is trimmed.
            string body;
            try
            {
                body = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return UnslothOperationResult.Failed($"the answer was cut short: {ex.Message}");
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                return UnslothOperationResult.Failed(
                    "the server did not finish in time — it may still be loading; check again in a moment");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return UnslothOperationResult.Failed("the server refused the API key");
            }

            JsonObject? item = null;
            try
            {
                item = body.Length > 0 ? JsonNode.Parse(body) as JsonObject : null;
            }
            catch (JsonException)
            {
                item = null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = item is not null ? ErrorDetail(item) : body;
                return UnslothOperationResult.Failed(detail.Length > 0
                    ? $"{(int)response.StatusCode}: {detail}"
                    : $"the server answered {(int)response.StatusCode}");
            }

            if (item is null)
            {
                return UnslothOperationResult.Failed("the server answered without a result");
            }

            var status = Text(item, "status");
            if (status.Length > 0 && status.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return UnslothOperationResult.Failed(ErrorDetail(item) is { Length: > 0 } why ? why : status);
            }

            return new UnslothOperationResult(true, status.Length > 0 ? status : "ok",
                Text(item, "model"), Text(item, "display_name"),
                Number(item, "context_length"), ErrorDetail(item));
        }
    }

    /// <summary>The human-readable reason a FastAPI answer carries, whatever shape it took.</summary>
    private static string ErrorDetail(JsonObject item)
    {
        foreach (var field in new[] { "detail", "message", "error" })
        {
            switch (item[field])
            {
                case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 0:
                    return text;
                case JsonObject nested when ErrorDetail(nested) is { Length: > 0 } inner:
                    return inner;
                case JsonArray list:
                {
                    var parts = list.Select(e => e is JsonObject o ? ErrorDetail(o) : e?.ToString() ?? string.Empty)
                        .Where(p => p.Length > 0).ToList();
                    if (parts.Count > 0)
                    {
                        return string.Join("; ", parts);
                    }

                    break;
                }
            }
        }

        return Text(item, "msg");
    }

    // ---------------------------------------------------------------- keys #
    /// <summary>
    /// Sign in to Unsloth Studio with its username and password and create an
    /// API key for the agents. Studio stores keys hashed, so a key can only be
    /// read at the moment it is created — which is why this is done here rather
    /// than by hunting for one on disk.
    /// </summary>
    /// <exception cref="UnslothServerException">The server refused or could not be reached.</exception>
    public static async Task<string> MintKeyAsync(
        HttpClient http, string baseUrl, string username, string password,
        string name = MintedKeyName, CancellationToken token = default)
    {
        var origin = Normalise(baseUrl);
        string session;

        using (var login = new HttpRequestMessage(HttpMethod.Post, origin + "/api/auth/login")
               {
                   Content = JsonContent.Create(
                       new Dictionary<string, string> { ["username"] = username, ["password"] = password }),
               })
        {
            using var response = await SendOrThrowAsync(http, login, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Studio's own message says how to reset the password; keep it.
                var reason = Reason(body);
                throw new UnslothServerException(
                    reason.Length > 0
                        ? $"the server refused the sign-in: {reason}"
                        : "the server refused that username or password",
                    response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new UnslothServerException(
                    "too many sign-in attempts — wait a minute and try again", response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UnslothServerException(
                    $"sign-in answered {(int)response.StatusCode}: {Reason(body)}", response.StatusCode);
            }

            var item = JsonNode.Parse(body) as JsonObject;
            session = item is null ? string.Empty : Text(item, "access_token");
            if (session.Length == 0)
            {
                throw new UnslothServerException("sign-in succeeded but no session credential came back");
            }

            if (item is not null && Flag(item, "must_change_password"))
            {
                throw new UnslothServerException(
                    "this account must still change its first-run password — open Unsloth Studio, " +
                    "set a password, then try again");
            }
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, origin + "/api/auth/api-keys")
        {
            Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["expires_in_days"] = null,
            }, options: Json),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        using var created = await SendOrThrowAsync(http, create, token).ConfigureAwait(false);
        var text = await created.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!created.IsSuccessStatusCode)
        {
            throw new UnslothServerException(
                $"creating the key answered {(int)created.StatusCode}: {Reason(text)}", created.StatusCode);
        }

        var key = JsonNode.Parse(text) is JsonObject result ? Text(result, "key") : string.Empty;
        if (key.Length == 0)
        {
            throw new UnslothServerException("the server created a key but did not return it");
        }

        return key;
    }

    private static async Task<HttpResponseMessage> SendOrThrowAsync(
        HttpClient http, HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            return await http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UnslothServerException($"the server could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new UnslothServerException("the server did not answer in time");
        }
    }

    private static string Reason(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject item ? ErrorDetail(item) : body.Trim();
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    // --------------------------------------------------------------- plumb #
    private static string Text(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;

    private static bool Flag(JsonObject item, string field) =>
        item[field] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static IReadOnlyList<string> Strings(JsonObject item, string field) =>
        item[field] is JsonArray array
            ? array.Select(e => e is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty)
                .Where(s => s.Length > 0)
                .ToList()
            : Array.Empty<string>();

    private static long Long(JsonObject item, string field)
    {
        if (item[field] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<double>(out var real))
            {
                return (long)real;
            }
        }

        return 0;
    }

    private static int? Number(JsonObject item, string field)
    {
        if (item[field] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number) && number > 0)
        {
            return number;
        }

        if (value.TryGetValue<long>(out var wide) && wide > 0)
        {
            return wide > int.MaxValue ? int.MaxValue : (int)wide;
        }

        return value.TryGetValue<string>(out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : null;
    }

    /// <summary>
    /// The command that starts the server for one model, ready to show or run.
    /// <c>--disable-tools</c> is not optional: Unsloth runs its own server-side
    /// tools by default, and those swallow a coding agent's tool calls, so the agent
    /// answers but never edits a file.
    /// </summary>
    public static IReadOnlyList<string> StartCommand(string modelSpec, string baseUrl)
    {
        var uri = Uri.TryCreate(Normalise(baseUrl), UriKind.Absolute, out var parsed)
            ? parsed
            : new Uri(DefaultOrigin);

        return new[]
        {
            "run",
            "--model", modelSpec,
            "-H", uri.Host,
            "-p", uri.Port.ToString(CultureInfo.InvariantCulture),
            "--disable-tools",
        };
    }
}
