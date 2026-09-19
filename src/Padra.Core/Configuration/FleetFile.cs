using System.Globalization;
using Padra.Core.Networking;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Padra.Core.Discovery;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Padra.Core.Configuration;

/// <summary>
/// Reading, validating and rewriting <c>models.yaml</c>.
/// <para>
/// The control panel edits the fleet at runtime, so the file has to be written
/// back safely. Writes are atomic and always carry the explanatory header, so a
/// file the launcher produced is still a file a human can edit by hand.
/// </para>
/// </summary>
public static partial class FleetFile
{
    private static readonly IReadOnlySet<string> PlaceholderKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty, "sk-gateway-local-change-me", "changeme", "change-me",
        };

    public const string Header = """
        # Padra Platform Launcher — model fleet
        #
        # The control panel writes this file when you add, remove or reconfigure a
        # model, and it stays hand-editable. Every entry under `models:` becomes a
        # selectable model id at the gateway; the gateway starts `unsloth run` for it
        # on demand and routes by the `model` field of each request.
        #
        # gateway.host      127.0.0.1 keeps the gateway private; 0.0.0.0 serves the LAN.
        # gateway.mode      exclusive = one model in VRAM at a time, concurrent = all.
        # gateway.idleTimeout   seconds of inactivity before a model is freed (0 = off).
        # gateway.wireApi   Codex provider wire format written into ~/.codex/config.toml.
        # gateway.unknownModel  `default` serves unlisted model ids with the default
        #                   model (what Claude Code needs); `error` returns 404 instead.
        # gateway.autoImport    fill an empty fleet from the models already on this disk.
        # gateway.endpointSource  which server the coding agents are configured to use:
        #                   `gateway` = this launcher, one endpoint in front of every
        #                   model; `unsloth` = Unsloth's own server, reached directly.
        # gateway.unslothServerUrl  that server's origin (default http://127.0.0.1:8888).
        # gateway.unslothApiKey     a key for it, when it should not be discovered.
        # gateway.unslothModel      the model the agents are pointed at on that server,
        #                   as it names it (org/repo or org/repo:QUANT); empty = the
        #                   resident model.
        #
        # Per-model keys: alias, repo, quant, port, contextLength, extraArgs, env,
        #                 host, apiKey, servedId, reasoning, disableTools, runtime.
        #
        # runtime:  how a model larger than this GPU still runs. Every key maps to a
        #           flag the inference server understands, and the defaults below let
        #           Unsloth place the model itself.
        #   gpuMemoryMode  auto = Unsloth sizes context and picks devices; manual =
        #                  the values here are used as written.
        #   gpuLayers      layers kept in VRAM (-1 = all). Empty leaves it automatic.
        #   fit            auto | on | off — `on` shrinks context and offloads
        #                  tensors to system RAM until the model fits.
        #   fitContext     smallest context `fit` may fall back to.
        #   cpuMoeLayers   expert layers held in system RAM (-1 = all of them).
        #   kvCacheType    f32 f16 bf16 q8_0 q5_1 q5_0 iq4_nl q4_1 q4_0
        #   flashAttention auto | on | off
        #   threads        generation threads.
        #   noKvOffload    keep the KV cache in system RAM.
        #   swaFull        allocate the full sliding-window cache.
        #   allowOversize  true = start models this machine cannot hold in VRAM by
        #                  asking the server to offload, instead of refusing them.

        """;

    /// <summary>
    /// Longest alias accepted. Codex writes one file per alias as
    /// <c>&lt;alias&gt;.config.toml</c>, so the limit has to stay inside the 255-byte
    /// file-name budget every supported filesystem gives; 96 leaves ample room and
    /// still accepts the long descriptive names Hugging Face fine-tunes carry.
    /// </summary>
    public const int MaxAliasLength = 96;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex AliasCleanup();

    [GeneratedRegex("[-._]{2,}")]
    private static partial Regex AliasRuns();

    private static IDeserializer Deserializer => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static ISerializer Serializer => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .Build();

    // ----------------------------------------------------------------- load #
    public static FleetOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FleetFileException($"config not found: {path}");
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new FleetFileException($"{path} could not be read: {ex.Message}", ex);
        }

        return Parse(text, path);
    }

    public static FleetOptions Parse(string yaml, string origin = "models.yaml")
    {
        RawFleet? raw;
        try
        {
            raw = Deserializer.Deserialize<RawFleet>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FleetFileException($"{origin} is not valid YAML: {ex.Message}", ex);
        }

        return Normalise(raw ?? new RawFleet());
    }

    // ------------------------------------------------------------- validate #
    /// <summary>Re-validate an options object that code has edited in memory.</summary>
    public static FleetOptions Validate(FleetOptions options)
    {
        var validated = Normalise(ToRaw(options));
        validated.Gateway.ApiKey = options.Gateway.ApiKey;
        validated.Gateway.UnslothApiKey = options.Gateway.UnslothApiKey;
        validated.Gateway.UnslothModel = options.Gateway.UnslothModel.Trim();
        if (options.Gateway.DefaultModel.Length > 0 &&
            validated.Models.Any(m => m.Alias == options.Gateway.DefaultModel))
        {
            validated.Gateway.DefaultModel = options.Gateway.DefaultModel;
        }

        return validated;
    }

    public static FleetOptions Normalise(RawFleet raw)
    {
        var source = raw.Gateway ?? new RawGateway();
        var gateway = new GatewayOptions
        {
            Host = Blank(source.Host) ? "127.0.0.1" : source.Host!.Trim(),
            Port = ValidPort(source.Port, FleetOptions.DefaultGatewayPort, "gateway.port"),
            ApiKey = source.ApiKey?.Trim() ?? string.Empty,
            Mode = ParseEnum(source.Mode, FleetMode.Exclusive, "gateway.mode"),
            Preload = source.Preload ?? false,
            IdleTimeout = ValidCount(source.IdleTimeout, 0, "gateway.idleTimeout"),
            RequestTimeout = ValidCount(source.RequestTimeout, 1800, "gateway.requestTimeout"),
            StartTimeout = ValidCount(source.StartTimeout, 900, "gateway.startTimeout"),
            UnslothBin = Blank(source.UnslothBin) ? "unsloth" : source.UnslothBin!.Trim(),
            WireApi = ParseEnum(source.WireApi, CodexWireApi.Responses, "gateway.wireApi"),
            UnknownModel = ParseEnum(source.UnknownModel, UnknownModelBehaviour.Default,
                "gateway.unknownModel"),
            AutoImport = source.AutoImport ?? true,
            EndpointSource = ParseEnum(source.EndpointSource, EndpointSource.Gateway,
                "gateway.endpointSource"),
            UnslothServerUrl = source.UnslothServerUrl?.Trim() ?? string.Empty,
            UnslothApiKey = source.UnslothApiKey?.Trim() ?? string.Empty,
            UnslothModel = source.UnslothModel?.Trim() ?? string.Empty,
            ExtraModelPaths = (source.ExtraModelPaths ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList(),
        };
        gateway.AllowLan = (source.AllowLan ?? false) || NetworkHosts.IsWildcard(gateway.Host);

        var models = new List<ModelOptions>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var ports = new Dictionary<int, string>();

        var entries = raw.Models ?? new List<RawModel>();
        for (var index = 0; index < entries.Count; index++)
        {
            var model = NormaliseModel(entries[index], index);
            if (!aliases.Add(model.Alias))
            {
                throw new FleetFileException($"duplicate alias `{model.Alias}`");
            }

            if (ports.TryGetValue(model.Port, out var owner))
            {
                throw new FleetFileException(
                    $"port {model.Port} is used by both `{owner}` and `{model.Alias}`");
            }

            if (model.Port == gateway.Port)
            {
                throw new FleetFileException(
                    $"model `{model.Alias}` uses the gateway port {gateway.Port}");
            }

            ports[model.Port] = model.Alias;
            models.Add(model);
        }

        var defaultModel = source.DefaultModel?.Trim() ?? string.Empty;
        if (defaultModel.Length > 0 && !aliases.Contains(defaultModel))
        {
            throw new FleetFileException(
                $"gateway.defaultModel `{defaultModel}` is not a defined alias");
        }

        if (defaultModel.Length == 0 && models.Count > 0)
        {
            defaultModel = models[0].Alias;
        }

        gateway.DefaultModel = defaultModel;
        return new FleetOptions { Gateway = gateway, Models = models };
    }

    private static ModelOptions NormaliseModel(RawModel entry, int index)
    {
        if (Blank(entry.Alias))
        {
            throw new FleetFileException($"models[{index}] is missing `alias`");
        }

        if (Blank(entry.Repo))
        {
            throw new FleetFileException($"models[{index}] is missing `repo`");
        }

        var alias = entry.Alias!.Trim().ToLowerInvariant();
        ValidateAlias(alias);

        var reasoning = entry.Reasoning?.Trim().ToLowerInvariant() ?? string.Empty;
        if (reasoning is not ("" or "on" or "off"))
        {
            throw new FleetFileException($"models[{index}].reasoning must be `on` or `off`");
        }

        var context = ValidCount(entry.ContextLength, 0, $"models[{index}].contextLength");

        var extraArgs = (entry.ExtraArgs ?? new List<string>())
            .Select(a => a ?? string.Empty).ToList();
        UnslothArguments.Validate(extraArgs, $"models[{index}].extraArgs");

        return new ModelOptions
        {
            Alias = alias,
            Repo = entry.Repo!.Trim(),
            Quant = entry.Quant?.Trim() ?? string.Empty,
            Port = ValidPort(entry.Port, FleetOptions.FirstBackendPort + index,
                $"models[{index}].port"),
            Host = Blank(entry.Host) ? "127.0.0.1" : entry.Host!.Trim(),
            ContextLength = context > 0 ? context : null,
            ExtraArgs = extraArgs,
            Env = (entry.Env ?? new Dictionary<string, string>())
                .ToDictionary(p => p.Key, p => p.Value ?? string.Empty, StringComparer.Ordinal),
            DisableTools = entry.DisableTools ?? true,
            Reasoning = reasoning,
            ApiKey = entry.ApiKey?.Trim() ?? string.Empty,
            ServedId = entry.ServedId?.Trim() ?? string.Empty,
            Runtime = NormaliseRuntime(entry.Runtime, $"models[{index}].runtime"),
        };
    }

    private static RuntimeOptions NormaliseRuntime(RawRuntime? entry, string label)
    {
        if (entry is null)
        {
            return new RuntimeOptions();
        }

        var kvCacheType = entry.KvCacheType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (kvCacheType.Length > 0 &&
            !RuntimeOptions.KvCacheTypes.Contains(kvCacheType, StringComparer.Ordinal))
        {
            throw new FleetFileException(
                $"{label}.kvCacheType must be one of {string.Join(", ", RuntimeOptions.KvCacheTypes)}");
        }

        var gpuLayers = entry.GpuLayers;
        if (gpuLayers is { } layers && layers < RuntimeOptions.AllGpuLayers)
        {
            throw new FleetFileException($"{label}.gpuLayers cannot be below -1");
        }

        var cpuMoe = entry.CpuMoeLayers;
        if (cpuMoe is { } moe && moe < RuntimeOptions.AllCpuMoeLayers)
        {
            throw new FleetFileException($"{label}.cpuMoeLayers cannot be below -1");
        }

        return new RuntimeOptions
        {
            GpuMemoryMode = ParseEnum(entry.GpuMemoryMode, GpuMemoryMode.Auto, $"{label}.gpuMemoryMode"),
            GpuLayers = gpuLayers,
            Fit = ParseEnum(entry.Fit, Toggle.Auto, $"{label}.fit"),
            FitContext = entry.FitContext is > 0
                ? ValidCount(entry.FitContext, 0, $"{label}.fitContext")
                : null,
            CpuMoeLayers = cpuMoe,
            KvCacheType = kvCacheType,
            FlashAttention = ParseEnum(entry.FlashAttention, Toggle.Auto, $"{label}.flashAttention"),
            Threads = entry.Threads is > 0
                ? ValidCount(entry.Threads, 0, $"{label}.threads")
                : null,
            NoKvOffload = entry.NoKvOffload ?? false,
            SwaFull = entry.SwaFull ?? false,
            AllowOversize = entry.AllowOversize ?? true,
        };
    }

    private static RawRuntime? ToRawRuntime(RuntimeOptions runtime) => runtime.IsDefault
        ? null
        : new RawRuntime
        {
            GpuMemoryMode = runtime.GpuMemoryMode.ToString().ToLowerInvariant(),
            GpuLayers = runtime.GpuLayers,
            Fit = runtime.Fit.ToString().ToLowerInvariant(),
            FitContext = runtime.FitContext,
            CpuMoeLayers = runtime.CpuMoeLayers,
            KvCacheType = runtime.KvCacheType.Length > 0 ? runtime.KvCacheType : null,
            FlashAttention = runtime.FlashAttention.ToString().ToLowerInvariant(),
            Threads = runtime.Threads,
            NoKvOffload = runtime.NoKvOffload,
            SwaFull = runtime.SwaFull,
            AllowOversize = runtime.AllowOversize,
        };

    // ----------------------------------------------------------------- save #
    public static string Render(FleetOptions options)
    {
        var payload = new RawFleet
        {
            Gateway = new RawGateway
            {
                Host = options.Gateway.Host,
                Port = options.Gateway.Port,
                ApiKey = options.Gateway.ApiKey,
                Mode = options.Gateway.Mode.ToString().ToLowerInvariant(),
                DefaultModel = options.Gateway.DefaultModel,
                Preload = options.Gateway.Preload,
                IdleTimeout = options.Gateway.IdleTimeout,
                RequestTimeout = options.Gateway.RequestTimeout,
                StartTimeout = options.Gateway.StartTimeout,
                UnslothBin = options.Gateway.UnslothBin,
                WireApi = options.Gateway.WireApi.ToString().ToLowerInvariant(),
                UnknownModel = options.Gateway.UnknownModel.ToString().ToLowerInvariant(),
                AutoImport = options.Gateway.AutoImport,
                AllowLan = options.Gateway.AllowLan,
                EndpointSource = options.Gateway.EndpointSource.ToString().ToLowerInvariant(),
                UnslothServerUrl = options.Gateway.UnslothServerUrl,
                UnslothApiKey = options.Gateway.UnslothApiKey,
                UnslothModel = options.Gateway.UnslothModel,
                ExtraModelPaths = options.Gateway.ExtraModelPaths,
            },
            Models = options.Models.Select(m => new RawModel
            {
                Alias = m.Alias,
                Repo = m.Repo,
                Quant = string.IsNullOrEmpty(m.Quant) ? null : m.Quant,
                Port = m.Port,
                Host = m.Host,
                ContextLength = m.ContextLength,
                ExtraArgs = m.ExtraArgs.Count > 0 ? m.ExtraArgs : null,
                Env = m.Env.Count > 0 ? m.Env : null,
                DisableTools = m.DisableTools,
                Reasoning = string.IsNullOrEmpty(m.Reasoning) ? null : m.Reasoning,
                ApiKey = string.IsNullOrEmpty(m.ApiKey) ? null : m.ApiKey,
                ServedId = string.IsNullOrEmpty(m.ServedId) ? null : m.ServedId,
                Runtime = ToRawRuntime(m.Runtime),
            }).ToList(),
        };

        return Header + Serializer.Serialize(payload);
    }

    /// <summary>Write the file atomically: a crash mid-write cannot truncate it.</summary>
    public static void Save(string path, FleetOptions options)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temporary, Render(options), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch (IOException ex)
        {
            throw new FleetFileException($"{path} could not be written: {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // The replace already consumed it.
                }
            }
        }
    }

    // ------------------------------------------------------------ mutations #
    /// <summary>Replace a missing or placeholder key with a real random one.</summary>
    public static bool EnsureApiKey(FleetOptions options)
    {
        var current = options.Gateway.ApiKey.Trim();
        if (!PlaceholderKeys.Contains(current))
        {
            return false;
        }

        Span<byte> entropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(entropy);
        options.Gateway.ApiKey = "sk-padra-" + Convert.ToBase64String(entropy)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return true;
    }

    public static int NextFreePort(FleetOptions options)
    {
        var taken = new HashSet<int>(options.Models.Select(m => m.Port)) { options.Gateway.Port };
        for (var port = FleetOptions.FirstBackendPort; port <= FleetOptions.LastBackendPort; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        throw new FleetFileException(
            $"no free port between {FleetOptions.FirstBackendPort} and {FleetOptions.LastBackendPort}");
    }

    // ----------------------------------------------------------------- alias #
    /// <summary>
    /// Throw unless <paramref name="alias"/> is exactly what the gateway can serve.
    /// The three failure modes are reported apart, because "too long" and "bad
    /// character" need different fixes and one shared message hides which applies.
    /// </summary>
    public static void ValidateAlias(string alias)
    {
        if (alias.Length == 0)
        {
            throw new FleetFileException("an alias cannot be empty");
        }

        if (alias.Length > MaxAliasLength)
        {
            throw new FleetFileException(
                $"alias `{alias}` is {alias.Length.ToString(CultureInfo.InvariantCulture)} " +
                $"characters — the limit is {MaxAliasLength.ToString(CultureInfo.InvariantCulture)}. " +
                "Shorten it, or let the control panel shorten it for you.");
        }

        if (alias[0] is (< 'a' or > 'z') and (< '0' or > '9'))
        {
            throw new FleetFileException(
                $"alias `{alias}` must start with a lowercase letter or a digit");
        }

        if (!AliasPattern().IsMatch(alias))
        {
            throw new FleetFileException(
                $"alias `{alias}` may contain only lowercase letters, digits, dot, dash or underscore");
        }
    }

    /// <summary>
    /// Turn any text into an alias the gateway accepts: lower case, separators
    /// collapsed, trimmed to <see cref="MaxAliasLength"/> on a separator boundary
    /// where one is close enough that the name still reads.
    /// </summary>
    public static string SanitiseAlias(string raw)
    {
        var cleaned = AliasRuns()
            .Replace(AliasCleanup().Replace(raw.Trim().ToLowerInvariant(), "-"), "-")
            .Trim('-', '.', '_');

        // A leading run of characters that are legal inside an alias but not at the
        // front (`.`, `-`, `_` are trimmed above; digits and letters are fine) —
        // nothing left to strip, so only emptiness has to be handled.
        if (cleaned.Length == 0)
        {
            return "model";
        }

        return ShortenAlias(cleaned);
    }

    /// <summary>Cut an alias to the limit without leaving a dangling separator.</summary>
    private static string ShortenAlias(string alias)
    {
        if (alias.Length <= MaxAliasLength)
        {
            return alias;
        }

        var cut = alias[..MaxAliasLength];
        var boundary = cut.LastIndexOfAny(new[] { '-', '.', '_' });

        // Only honour a separator that keeps most of the name; otherwise a single
        // dash near the front would throw away everything that identifies the model.
        if (boundary >= MaxAliasLength * 3 / 4)
        {
            cut = cut[..boundary];
        }

        cut = cut.TrimEnd('-', '.', '_');
        return cut.Length > 0 ? cut : alias[..MaxAliasLength].TrimEnd('-', '.', '_');
    }

    /// <summary>A short, unique, URL-safe alias derived from the repository name.</summary>
    public static string SuggestAlias(string repo, string quant, FleetOptions options)
    {
        var basis = SanitiseAlias(ModelNaming.RepoBaseName(repo));
        var taken = options.Models.Select(m => m.Alias).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(basis))
        {
            return basis;
        }

        if (!string.IsNullOrEmpty(quant))
        {
            var slug = SanitiseAlias(quant);
            var candidate = ShortenAlias($"{basis}-{slug}");
            if (candidate.Length > 0 && !taken.Contains(candidate))
            {
                return candidate;
            }
        }

        for (var counter = 2; counter < 1000; counter++)
        {
            var suffix = "-" + counter.ToString(CultureInfo.InvariantCulture);
            var candidate = ShortenAlias(basis[..Math.Min(basis.Length, MaxAliasLength - suffix.Length)]
                .TrimEnd('-', '.', '_') + suffix);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new FleetFileException($"cannot derive a free alias for {repo}");
    }

    /// <summary>Append a model and return the stored entry. Mutates <paramref name="options"/>.</summary>
    public static ModelOptions AddModel(
        FleetOptions options,
        string repo,
        string quant = "",
        string alias = "",
        int? contextLength = null,
        int? port = null,
        string reasoning = "",
        IEnumerable<string>? extraArgs = null,
        bool makeDefault = false,
        RuntimeOptions? runtime = null)
    {
        repo = repo.Trim();
        quant = quant.Trim();
        if (repo.Length == 0)
        {
            throw new FleetFileException("a repository is required");
        }

        var clash = options.Models.FirstOrDefault(m =>
            string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Quant, quant, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            var spec = quant.Length > 0 ? $"{repo}:{quant}" : repo;
            throw new FleetFileException($"`{spec}` is already in the fleet");
        }

        var entry = new ModelOptions
        {
            // A hand-typed alias is repaired rather than rejected: the panel offers a
            // free-text field, and "Qwen3.6 27B (heretic)" is a reasonable thing to
            // type. models.yaml edited by hand is still validated strictly.
            Alias = alias.Trim().Length > 0
                ? SanitiseAlias(alias)
                : SuggestAlias(repo, quant, options),
            Repo = repo,
            Quant = quant,
            Port = port ?? NextFreePort(options),
            ContextLength = contextLength is > 0 ? contextLength : null,
            ExtraArgs = extraArgs?.ToList() ?? new List<string>(),
            DisableTools = true,
            Reasoning = reasoning.Trim().ToLowerInvariant(),
            Runtime = runtime?.Clone() ?? new RuntimeOptions(),
        };

        options.Models.Add(entry);

        // Re-validation goes through ToRaw, which blanks the default so a stale
        // name can never survive; the caller's existing choice is put back here —
        // adding model C must not silently move the default from B to A.
        var previousDefault = options.Gateway.DefaultModel;
        var validated = Normalise(ToRaw(options));
        options.Models = validated.Models;
        options.Gateway.DefaultModel =
            previousDefault.Length > 0 && options.Models.Any(m => m.Alias == previousDefault)
                ? previousDefault
                : validated.Gateway.DefaultModel;

        if (makeDefault || options.Models.Count == 1)
        {
            options.Gateway.DefaultModel = entry.Alias;
        }

        return options.Models.First(m => m.Alias == entry.Alias);
    }

    public static void RemoveModel(FleetOptions options, string alias)
    {
        alias = alias.Trim().ToLowerInvariant();
        var removed = options.Models.RemoveAll(m => m.Alias == alias);
        if (removed == 0)
        {
            throw new FleetFileException($"unknown model `{alias}`");
        }

        if (options.Gateway.DefaultModel == alias)
        {
            options.Gateway.DefaultModel = options.Models.Count > 0 ? options.Models[0].Alias : string.Empty;
        }
    }

    /// <summary>
    /// Replace one model's runtime block and return the validated entry.
    /// Mutates <paramref name="options"/>.
    /// </summary>
    public static ModelOptions SetRuntime(FleetOptions options, string alias, RuntimeOptions runtime)
    {
        alias = alias.Trim().ToLowerInvariant();
        var entry = options.Models.FirstOrDefault(m => m.Alias == alias)
                    ?? throw new FleetFileException($"unknown model `{alias}`");

        entry.Runtime = NormaliseRuntime(ToRawRuntime(runtime), $"models[{alias}].runtime");
        return entry;
    }

    /// <summary>Replace one model's context length. Mutates <paramref name="options"/>.</summary>
    public static ModelOptions SetContextLength(FleetOptions options, string alias, int? contextLength)
    {
        alias = alias.Trim().ToLowerInvariant();
        var entry = options.Models.FirstOrDefault(m => m.Alias == alias)
                    ?? throw new FleetFileException($"unknown model `{alias}`");

        if (contextLength is { } value && value < 0)
        {
            throw new FleetFileException("contextLength cannot be negative");
        }

        entry.ContextLength = contextLength is > 0 ? contextLength : null;
        return entry;
    }

    public static void SetDefaultModel(FleetOptions options, string alias)
    {
        alias = alias.Trim().ToLowerInvariant();
        if (options.Models.All(m => m.Alias != alias))
        {
            throw new FleetFileException($"unknown model `{alias}`");
        }

        options.Gateway.DefaultModel = alias;
    }

    // ---------------------------------------------------------------- plumb #
    private static RawFleet ToRaw(FleetOptions options) => new()
    {
        Gateway = new RawGateway
        {
            Host = options.Gateway.Host,
            Port = options.Gateway.Port,
            ApiKey = options.Gateway.ApiKey,
            Mode = options.Gateway.Mode.ToString().ToLowerInvariant(),
            DefaultModel = string.Empty,
            Preload = options.Gateway.Preload,
            IdleTimeout = options.Gateway.IdleTimeout,
            RequestTimeout = options.Gateway.RequestTimeout,
            StartTimeout = options.Gateway.StartTimeout,
            UnslothBin = options.Gateway.UnslothBin,
            WireApi = options.Gateway.WireApi.ToString().ToLowerInvariant(),
            UnknownModel = options.Gateway.UnknownModel.ToString().ToLowerInvariant(),
            AutoImport = options.Gateway.AutoImport,
            AllowLan = options.Gateway.AllowLan,
            EndpointSource = options.Gateway.EndpointSource.ToString().ToLowerInvariant(),
            UnslothServerUrl = options.Gateway.UnslothServerUrl,
            UnslothApiKey = options.Gateway.UnslothApiKey,
            UnslothModel = options.Gateway.UnslothModel,
            ExtraModelPaths = options.Gateway.ExtraModelPaths,
        },
        Models = options.Models.Select(m => new RawModel
        {
            Alias = m.Alias,
            Repo = m.Repo,
            Quant = m.Quant,
            Port = m.Port,
            Host = m.Host,
            ContextLength = m.ContextLength,
            ExtraArgs = m.ExtraArgs,
            Env = m.Env,
            DisableTools = m.DisableTools,
            Reasoning = m.Reasoning,
            ApiKey = m.ApiKey,
            ServedId = m.ServedId,
            Runtime = ToRawRuntime(m.Runtime),
        }).ToList(),
    };

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static int ValidPort(int? value, int fallback, string label)
    {
        var port = value ?? fallback;
        if (port is < 1 or > 65535)
        {
            throw new FleetFileException($"{label} must be between 1 and 65535");
        }

        return port;
    }

    private static int ValidCount(int? value, int fallback, string label)
    {
        var count = value ?? fallback;
        if (count < 0)
        {
            throw new FleetFileException($"{label} cannot be negative");
        }

        return count;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string label)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        var allowed = string.Join(" or ", Enum.GetNames<TEnum>().Select(n => $"`{n.ToLowerInvariant()}`"));
        throw new FleetFileException($"{label} must be {allowed}");
    }
}

/// <summary>The literal YAML shape, before validation.</summary>
public sealed class RawFleet
{
    public RawGateway? Gateway { get; set; }

    public List<RawModel>? Models { get; set; }
}

public sealed class RawGateway
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? ApiKey { get; set; }
    public string? Mode { get; set; }
    public string? DefaultModel { get; set; }
    public bool? Preload { get; set; }
    public int? IdleTimeout { get; set; }
    public int? RequestTimeout { get; set; }
    public int? StartTimeout { get; set; }
    public string? UnslothBin { get; set; }
    public string? WireApi { get; set; }
    public string? UnknownModel { get; set; }
    public bool? AutoImport { get; set; }
    public bool? AllowLan { get; set; }
    public string? EndpointSource { get; set; }
    public string? UnslothServerUrl { get; set; }
    public string? UnslothApiKey { get; set; }
    public string? UnslothModel { get; set; }
    public List<string>? ExtraModelPaths { get; set; }
}

public sealed class RawModel
{
    public string? Alias { get; set; }
    public string? Repo { get; set; }
    public string? Quant { get; set; }
    public int? Port { get; set; }
    public string? Host { get; set; }
    public int? ContextLength { get; set; }
    public List<string>? ExtraArgs { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public bool? DisableTools { get; set; }
    public string? Reasoning { get; set; }
    public string? ApiKey { get; set; }
    public string? ServedId { get; set; }
    public RawRuntime? Runtime { get; set; }
}

public sealed class RawRuntime
{
    public string? GpuMemoryMode { get; set; }
    public int? GpuLayers { get; set; }
    public string? Fit { get; set; }
    public int? FitContext { get; set; }
    public int? CpuMoeLayers { get; set; }
    public string? KvCacheType { get; set; }
    public string? FlashAttention { get; set; }
    public int? Threads { get; set; }
    public bool? NoKvOffload { get; set; }
    public bool? SwaFull { get; set; }
    public bool? AllowOversize { get; set; }
}
