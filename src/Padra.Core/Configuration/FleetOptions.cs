using Padra.Core.Networking;

namespace Padra.Core.Configuration;

/// <summary>Everything <c>models.yaml</c> can express, already validated.</summary>
public sealed class FleetOptions
{
    public const int DefaultGatewayPort = 8900;
    public const int FirstBackendPort = 8801;
    public const int LastBackendPort = 8999;

    public GatewayOptions Gateway { get; set; } = new();

    public List<ModelOptions> Models { get; set; } = new();

    public FleetOptions Clone() => new()
    {
        Gateway = Gateway.Clone(),
        Models = Models.Select(m => m.Clone()).ToList(),
    };
}

public enum FleetMode
{
    /// <summary>One backend in VRAM at a time; the gateway swaps on demand.</summary>
    Exclusive,

    /// <summary>Every requested backend stays loaded.</summary>
    Concurrent,
}

public enum UnknownModelBehaviour
{
    /// <summary>Serve unlisted model ids with the default model (Claude Code needs this).</summary>
    Default,

    /// <summary>Answer 404 for unlisted model ids.</summary>
    Error,
}

public enum CodexWireApi
{
    Responses,
    Chat,
}

/// <summary>Which server the coding agents are pointed at.</summary>
public enum EndpointSource
{
    /// <summary>
    /// This launcher's gateway. One endpoint in front of several models, with a
    /// model chosen per request and swapped on demand.
    /// </summary>
    Gateway,

    /// <summary>
    /// Unsloth's own server, reached directly. It already answers the OpenAI Chat
    /// Completions, OpenAI Responses and Anthropic Messages APIs on one port, so
    /// nothing needs to sit in front of it; the launcher only configures the agents
    /// and stays out of the request path.
    /// </summary>
    Unsloth,
}

/// <summary>
/// How much of the placement decision Unsloth makes for itself
/// (<c>--gpu-memory-mode</c>).
/// </summary>
public enum GpuMemoryMode
{
    /// <summary>Unsloth sizes the context and picks the devices.</summary>
    Auto,

    /// <summary>The values below are handed straight to the inference server.</summary>
    Manual,
}

/// <summary>A three-way switch whose third state means "leave it to the backend".</summary>
public enum Toggle
{
    Auto,
    On,
    Off,
}

/// <summary>
/// The knobs that decide whether a model larger than this machine's GPU still
/// runs.
/// <para>
/// Every value maps to a flag <c>unsloth run</c> forwards to its inference
/// server; none of them is on Unsloth's managed denylist. Left at their defaults
/// the whole block is absent from <c>models.yaml</c> and Unsloth's own automatic
/// placement applies, which is what most machines want.
/// </para>
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>Keep all Mixture-of-Experts weights in system RAM.</summary>
    public const int AllCpuMoeLayers = -1;

    /// <summary>Keep every layer on the GPU.</summary>
    public const int AllGpuLayers = -1;

    /// <summary>Cache types the inference server accepts for <c>-ctk</c>/<c>-ctv</c>.</summary>
    public static readonly IReadOnlyList<string> KvCacheTypes = new[]
    {
        "f32", "f16", "bf16", "q8_0", "q5_1", "q5_0", "iq4_nl", "q4_1", "q4_0",
    };

    public GpuMemoryMode GpuMemoryMode { get; set; } = GpuMemoryMode.Auto;

    /// <summary>Layers to keep in VRAM (<c>-ngl</c>). Null leaves it automatic.</summary>
    public int? GpuLayers { get; set; }

    /// <summary>Let the server shrink unset arguments until the model fits (<c>--fit</c>).</summary>
    public Toggle Fit { get; set; } = Toggle.Auto;

    /// <summary>Smallest context <c>--fit</c> may fall back to (<c>-fitc</c>).</summary>
    public int? FitContext { get; set; }

    /// <summary>Expert layers to hold in system RAM (<c>-ncmoe</c>, or <c>-cmoe</c> for all).</summary>
    public int? CpuMoeLayers { get; set; }

    /// <summary>KV cache quantisation applied to both halves (<c>-ctk</c>/<c>-ctv</c>).</summary>
    public string KvCacheType { get; set; } = string.Empty;

    public Toggle FlashAttention { get; set; } = Toggle.Auto;

    /// <summary>Generation threads (<c>-t</c>). Null leaves the server's own choice.</summary>
    public int? Threads { get; set; }

    /// <summary>Keep the KV cache in system RAM (<c>-nkvo</c>).</summary>
    public bool NoKvOffload { get; set; }

    /// <summary>Allocate the full sliding-window cache (<c>--swa-full</c>).</summary>
    public bool SwaFull { get; set; }

    /// <summary>
    /// Start models this machine cannot hold in VRAM instead of refusing them.
    /// When the fit estimate falls short and nothing above is pinned, the gateway
    /// asks the inference server to offload rather than letting an optimistic
    /// placement crash on load.
    /// </summary>
    public bool AllowOversize { get; set; } = true;

    /// <summary>True when nothing here differs from the defaults.</summary>
    public bool IsDefault =>
        GpuMemoryMode == GpuMemoryMode.Auto &&
        GpuLayers is null &&
        Fit == Toggle.Auto &&
        FitContext is null &&
        CpuMoeLayers is null &&
        KvCacheType.Length == 0 &&
        FlashAttention == Toggle.Auto &&
        Threads is null &&
        !NoKvOffload &&
        !SwaFull &&
        AllowOversize;

    public RuntimeOptions Clone() => new()
    {
        GpuMemoryMode = GpuMemoryMode,
        GpuLayers = GpuLayers,
        Fit = Fit,
        FitContext = FitContext,
        CpuMoeLayers = CpuMoeLayers,
        KvCacheType = KvCacheType,
        FlashAttention = FlashAttention,
        Threads = Threads,
        NoKvOffload = NoKvOffload,
        SwaFull = SwaFull,
        AllowOversize = AllowOversize,
    };

    public bool SameAs(RuntimeOptions other) =>
        GpuMemoryMode == other.GpuMemoryMode &&
        GpuLayers == other.GpuLayers &&
        Fit == other.Fit &&
        FitContext == other.FitContext &&
        CpuMoeLayers == other.CpuMoeLayers &&
        string.Equals(KvCacheType, other.KvCacheType, StringComparison.Ordinal) &&
        FlashAttention == other.FlashAttention &&
        Threads == other.Threads &&
        NoKvOffload == other.NoKvOffload &&
        SwaFull == other.SwaFull &&
        AllowOversize == other.AllowOversize;
}

public sealed class GatewayOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = FleetOptions.DefaultGatewayPort;

    public string ApiKey { get; set; } = string.Empty;

    public FleetMode Mode { get; set; } = FleetMode.Exclusive;

    public string DefaultModel { get; set; } = string.Empty;

    public bool Preload { get; set; }

    /// <summary>Seconds of inactivity before a loaded model is freed. 0 disables it.</summary>
    public int IdleTimeout { get; set; }

    /// <summary>Seconds allowed for one proxied request. Agentic turns are long.</summary>
    public int RequestTimeout { get; set; } = 1800;

    /// <summary>Seconds allowed for <c>unsloth run</c> to load a model.</summary>
    public int StartTimeout { get; set; } = 900;

    public string UnslothBin { get; set; } = "unsloth";

    public CodexWireApi WireApi { get; set; } = CodexWireApi.Responses;

    public UnknownModelBehaviour UnknownModel { get; set; } = UnknownModelBehaviour.Default;

    public bool AutoImport { get; set; } = true;

    public bool AllowLan { get; set; }

    /// <summary>Whether client configuration points at this gateway or at Unsloth.</summary>
    public EndpointSource EndpointSource { get; set; } = EndpointSource.Gateway;

    /// <summary>
    /// The Unsloth server's origin, when it is not the one Unsloth uses by default.
    /// Empty means <c>http://127.0.0.1:8888</c>, or whatever
    /// <c>UNSLOTH_STUDIO_URL</c> names.
    /// </summary>
    public string UnslothServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// A key for that server, when it should not be discovered. Unsloth stores its
    /// keys hashed, so the launcher normally reads the one
    /// <c>unsloth start</c> cached; pin it here to use a different one.
    /// </summary>
    public string UnslothApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The model the agents are configured with when they talk to Unsloth's server
    /// directly, as that server names it (<c>org/repo</c> or <c>org/repo:QUANT</c>).
    /// Empty means "whatever is resident", falling back to the first model listed.
    /// </summary>
    public string UnslothModel { get; set; } = string.Empty;

    public List<string> ExtraModelPaths { get; set; } = new();

    public bool IsShared => NetworkHosts.IsWildcard(Host);

    public GatewayOptions Clone() => new()
    {
        Host = Host,
        Port = Port,
        ApiKey = ApiKey,
        Mode = Mode,
        DefaultModel = DefaultModel,
        Preload = Preload,
        IdleTimeout = IdleTimeout,
        RequestTimeout = RequestTimeout,
        StartTimeout = StartTimeout,
        UnslothBin = UnslothBin,
        WireApi = WireApi,
        UnknownModel = UnknownModel,
        AutoImport = AutoImport,
        AllowLan = AllowLan,
        EndpointSource = EndpointSource,
        UnslothServerUrl = UnslothServerUrl,
        UnslothApiKey = UnslothApiKey,
        UnslothModel = UnslothModel,
        ExtraModelPaths = new List<string>(ExtraModelPaths),
    };
}

public sealed class ModelOptions
{
    public string Alias { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    public string Quant { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int? ContextLength { get; set; }

    /// <summary>Passed straight to <c>unsloth run</c>.</summary>
    public List<string> ExtraArgs { get; set; } = new();

    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Required when an external coding agent runs the tools itself.</summary>
    public bool DisableTools { get; set; } = true;

    /// <summary>"on", "off" or empty to leave the model's own default.</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>Set to skip capturing the backend key from <c>unsloth run</c> output.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Set to skip <c>/v1/models</c> discovery.</summary>
    public string ServedId { get; set; } = string.Empty;

    /// <summary>GPU placement, offload and cache settings for this model.</summary>
    public RuntimeOptions Runtime { get; set; } = new();

    public string ModelSpec => string.IsNullOrEmpty(Quant) ? Repo : $"{Repo}:{Quant}";

    public ModelOptions Clone() => new()
    {
        Alias = Alias,
        Repo = Repo,
        Quant = Quant,
        Port = Port,
        Host = Host,
        ContextLength = ContextLength,
        ExtraArgs = new List<string>(ExtraArgs),
        Env = new Dictionary<string, string>(Env, StringComparer.Ordinal),
        DisableTools = DisableTools,
        Reasoning = Reasoning,
        ApiKey = ApiKey,
        ServedId = ServedId,
        Runtime = Runtime.Clone(),
    };
}

/// <summary>Raised when a fleet file cannot be used as it stands.</summary>
public sealed class FleetFileException : Exception
{
    public FleetFileException(string message) : base(message)
    {
    }

    public FleetFileException(string message, Exception inner) : base(message, inner)
    {
    }
}
