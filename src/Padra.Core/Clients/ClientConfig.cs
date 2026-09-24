using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Padra.Core.Configuration;

namespace Padra.Core.Clients;

/// <summary>The coding agents this launcher can configure.</summary>
public enum ClientTarget
{
    Codex,
    Qwen,
    Claude,
    Continue,
    RooCode,
    VsCode,
}

/// <summary>One model, as the client configuration files see it.</summary>
public sealed record ClientModel(string Alias, string Repo, string Quant, int ContextLength)
{
    public string Spec => string.IsNullOrEmpty(Quant) ? Repo : $"{Repo}:{Quant}";
}

/// <summary>What an install is allowed to change beyond the provider definition.</summary>
public sealed record InstallOptions(bool MakeDefault = true, bool PersistEnvironment = true,
    bool FullToolAccess = false)
{
    public static readonly InstallOptions Default = new();

    /// <summary>Write the provider definition only, touching nothing global.</summary>
    public static readonly InstallOptions ProviderOnly = new(false, false);
}

/// <summary>Everything the generators need, independent of where it came from.</summary>
public sealed class ClientContext
{
    public const int DefaultContext = 131072;

    /// <summary>The Codex provider id used when the gateway is in front.</summary>
    public const string GatewayProviderId = "unsloth_gw";

    /// <summary>
    /// The Codex provider id Unsloth's own documentation uses. Matching it means a
    /// config this launcher writes and one written by `unsloth start codex` name the
    /// same provider instead of quietly competing.
    /// </summary>
    public const string UnslothProviderId = "unsloth_api";

    public required string BaseUrl { get; init; }

    public required string ApiKey { get; init; }

    public required IReadOnlyList<ClientModel> Models { get; init; }

    public required string DefaultAlias { get; init; }

    public CodexWireApi WireApi { get; init; } = CodexWireApi.Responses;

    public string ProjectDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Which server these settings point at.</summary>
    public EndpointSource Source { get; init; } = EndpointSource.Gateway;

    public bool Direct => Source == EndpointSource.Unsloth;

    /// <summary>The Codex `[model_providers.Ã¢â‚¬Â¦]` id.</summary>
    public string ProviderId => Direct ? UnslothProviderId : GatewayProviderId;

    /// <summary>The environment variable Codex reads the key from (`env_key`).</summary>
    public string KeyEnvName => Direct
        ? Runtime.UnslothServer.CodexKeyVariable
        : ClientConfigWriter.EnvKeyName;

    /// <summary>
    /// Whether one Codex profile file covers every model.
    /// <para>
    /// Against the gateway each model has a short alias and gets its own
    /// `&lt;alias&gt;.config.toml`. Against Unsloth the model id is a repository path
    /// with a slash in it, which cannot be a file name, so a single
    /// `unsloth_api.config.toml` is written Ã¢â‚¬â€ which is also what Unsloth's own
    /// documentation publishes.
    /// </para>
    /// </summary>
    public bool SingleProfile => Direct;

    /// <summary>Claude Code appends <c>/v1/...</c> itself, so it wants the bare origin.</summary>
    public string AnthropicBase =>
        BaseUrl.EndsWith("/v1", StringComparison.Ordinal) ? BaseUrl[..^3] : BaseUrl;

    /// <summary>The file name of the Codex profile for one model.</summary>
    public string ProfileFileName(ClientModel model) =>
        SingleProfile ? $"{ProviderId}.config.toml" : $"{model.Alias}.config.toml";

    /// <summary>Returns the same endpoint and model list with a chosen default model.</summary>
    public ClientContext WithDefaultModel(string? alias)
    {
        var requested = alias?.Trim() ?? string.Empty;
        var model = Models.FirstOrDefault(m =>
            string.Equals(m.Alias, requested, StringComparison.OrdinalIgnoreCase))
            ?? Models.FirstOrDefault(m =>
                Direct && string.Equals(m.Repo, requested, StringComparison.OrdinalIgnoreCase));

        return model is null || model.Alias == DefaultAlias
            ? this
            : new ClientContext
            {
                BaseUrl = BaseUrl,
                ApiKey = ApiKey,
                Models = Models,
                DefaultAlias = model.Alias,
                WireApi = WireApi,
                ProjectDirectory = ProjectDirectory,
             Source = Source,
             };
    }

    /// <summary>Returns this endpoint with selected models hidden from client configurations.</summary>
    public ClientContext WithoutModels(IReadOnlyCollection<string>? hiddenAliases)
    {
        if (hiddenAliases is null || hiddenAliases.Count == 0)
        {
            return this;
        }

        var hidden = hiddenAliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = Models.Where(model => !hidden.Contains(model.Alias)).ToList();
        if (visible.Count == Models.Count)
        {
            return this;
        }

        var defaultAlias = visible.FirstOrDefault(model =>
                string.Equals(model.Alias, DefaultAlias, StringComparison.OrdinalIgnoreCase))?.Alias
            ?? visible.FirstOrDefault()?.Alias
            ?? string.Empty;

        return new ClientContext
        {
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Models = visible,
            DefaultAlias = defaultAlias,
            WireApi = WireApi,
            ProjectDirectory = ProjectDirectory,
            Source = Source,
        };
    }

    public static ClientContext FromFleet(FleetOptions options, string? baseUrl = null,
        string? projectDirectory = null)
    {
        var models = options.Models
            .Select(m => new ClientModel(m.Alias, m.Repo, m.Quant, m.ContextLength ?? DefaultContext))
            .ToList();

        return new ClientContext
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl(options) : baseUrl!,
            ApiKey = Environment.GetEnvironmentVariable("GATEWAY_API_KEY") is { Length: > 0 } key
                ? key
                : options.Gateway.ApiKey,
            Models = models,
            DefaultAlias = options.Gateway.DefaultModel.Length > 0
                ? options.Gateway.DefaultModel
                : models.FirstOrDefault()?.Alias ?? string.Empty,
            WireApi = options.Gateway.WireApi,
            ProjectDirectory = projectDirectory ?? Environment.CurrentDirectory,
            Source = EndpointSource.Gateway,
        };
    }

    /// <summary>
    /// Settings that point straight at Unsloth's own server.
    /// <para>
    /// The model ids are the ones that server reports, not this launcher's aliases:
    /// Unsloth resolves `org/repo` and `org/repo:QUANT`, and nothing else.
    /// </para>
    /// </summary>
    public static ClientContext ForUnsloth(
        string origin,
        string apiKey,
        IReadOnlyList<Runtime.UnslothModel> models,
        string? defaultModel = null,
        string? projectDirectory = null)
    {
        var resolved = models
            .Select(m => new ClientModel(m.Spec, m.Id, m.Quant, m.ContextLength ?? DefaultContext))
            .ToList();

        // The choice may have been made by spec (`org/repo:QUANT`) or by the bare
        // repository id; either names the same model.
        var wanted = defaultModel?.Trim() ?? string.Empty;
        var chosen = resolved.FirstOrDefault(m =>
                         string.Equals(m.Alias, wanted, StringComparison.OrdinalIgnoreCase))
                     ?? resolved.FirstOrDefault(m =>
                         string.Equals(m.Repo, wanted, StringComparison.OrdinalIgnoreCase));

        return new ClientContext
        {
            BaseUrl = Runtime.UnslothServer.OpenAiBase(origin),
            ApiKey = apiKey,
            Models = resolved,
            DefaultAlias = chosen?.Alias ?? resolved.FirstOrDefault()?.Alias ?? string.Empty,
            // Unsloth only streams /v1/responses from its llama.cpp backend, which is
            // the wire format its own Codex recipe pins.
            WireApi = CodexWireApi.Responses,
            ProjectDirectory = projectDirectory ?? Environment.CurrentDirectory,
            Source = EndpointSource.Unsloth,
        };
    }

    public static string DefaultBaseUrl(FleetOptions options)
    {
        var host = options.Gateway.Host;
        if (Networking.NetworkHosts.IsWildcard(host))
        {
            host = "127.0.0.1";
        }

        return $"http://{Networking.NetworkHosts.ForUrl(host)}:{options.Gateway.Port}/v1";
    }
}

/// <summary>Exactly what an install touched, so the report can be honest.</summary>
public sealed class InstallReport
{
    public List<string> Lines { get; } = new();

    public List<string> Written { get; } = new();

    public List<string> Restored { get; } = new();

    public List<string> Failed { get; } = new();

    public bool Ok => Failed.Count == 0;

    public void Wrote(string path)
    {
        Written.Add(path);
        Lines.Add($"wrote {path}");
    }

    public void Unchanged(string path) => Lines.Add($"already current {path}");

    public void Removed(string path) => Lines.Add($"removed {path}");

    public void RestoredFile(string path)
    {
        Restored.Add(path);
        Lines.Add($"restored {path}");
    }

    public void BackedUp(string? path)
    {
        if (path is not null)
        {
            Lines.Add($"backed up {path}");
        }
    }

    public void Note(string message) => Lines.Add(message);

    public void Failure(ClientTarget target, Exception error)
    {
        Failed.Add(target.ToString().ToLowerInvariant());
        Lines.Add($"{target.ToString().ToLowerInvariant()} failed: {error.Message}");
    }
}

/// <summary>
/// Writes the provider settings each coding agent actually reads, and can put
/// every one of them back.
/// <para>
/// Everything is merged rather than overwritten: unrelated keys survive, the
/// previous file is copied to <c>.bak</c>, and running twice changes nothing. The
/// pristine content is also captured once into
/// <see cref="ClientInstallState"/>, which is what makes the restore a restore.
/// </para>
/// </summary>
public static class ClientConfigWriter
{
    public const string CodexProviderId = "unsloth_gw";
    public const string EnvKeyName = "UNSLOTH_GATEWAY_KEY";
    public const string BlockBegin = "# >>> padra-platform-launcher >>>";
    public const string BlockEnd = "# <<< padra-platform-launcher <<<";

    /// <summary>Markers around the two root keys that make this the Codex default.</summary>
    public const string DefaultsBegin = "# >>> padra-platform-launcher defaults >>>";
    public const string DefaultsEnd = "# <<< padra-platform-launcher defaults <<<";

    /// <summary>Prefix put in front of a root key this launcher had to move aside.</summary>
    public const string DisabledPrefix = "#padra-disabled# ";

    /// <summary>The Copilot Chat provider entry this launcher owns.</summary>
    public const string CopilotProviderName = "Padra Platform Launcher";

    public const string RooCodeImportSetting = "roo-cline.autoImportSettingsPath";

    private const string RooCodeSettingsFileName = "roo-code-settings.json";

    public static readonly IReadOnlyList<ClientTarget> AllTargets =
        Enum.GetValues<ClientTarget>().ToList();

    private static readonly JsonDocumentOptions LenientDocument = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // JsonNode values created from CLR types need a resolver to serialise.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string Slug(ClientTarget target) => target switch
    {
        ClientTarget.VsCode => "vscode",
        ClientTarget.RooCode => "roo-code",
        _ => target.ToString().ToLowerInvariant(),
    };

    public static bool TryParseTarget(string value, out ClientTarget target)
    {
        target = ClientTarget.Codex;
        foreach (var candidate in AllTargets)
        {
            if (string.Equals(Slug(candidate), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The VS Code key that carries environment variables to new terminals.</summary>
    public static string TerminalEnvKey =>
        IsWindows ? "terminal.integrated.env.windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "terminal.integrated.env.osx"
        : "terminal.integrated.env.linux";

    /// <summary>Every <c>terminal.integrated.env.*</c> key, for merging and removal.</summary>
    public static readonly IReadOnlyList<string> TerminalEnvKeys = new[]
    {
        "terminal.integrated.env.windows",
        "terminal.integrated.env.linux",
        "terminal.integrated.env.osx",
    };

    // ================================================================= Codex #
    /// <summary>
    /// The <c>[model_providers.Ã¢â‚¬Â¦]</c> table for <c>~/.codex/config.toml</c>.
    /// Only a table is emitted: in TOML every key after a table header belongs to
    /// that table, so appending bare keys to an existing file would silently move
    /// them into whatever section came last.
    /// </summary>
    public static string CodexProviderBlock(ClientContext context) =>
        $"[model_providers.{context.ProviderId}]{Environment.NewLine}" +
        $"name                 = {Toml(context.Direct ? "Unsloth Studio" : "Padra Platform Launcher (Unsloth)")}{Environment.NewLine}" +
        $"base_url             = {Toml(context.BaseUrl)}{Environment.NewLine}" +
        $"env_key              = {Toml(context.KeyEnvName)}{Environment.NewLine}" +
        $"wire_api             = {Toml(context.WireApi.ToString().ToLowerInvariant())}{Environment.NewLine}" +
        $"requires_openai_auth = false{Environment.NewLine}" +
        $"stream_idle_timeout_ms = 600000{Environment.NewLine}";

    /// <summary>
    /// The two root keys that make the gateway what Codex uses when no profile is
    /// named Ã¢â‚¬â€ which is the only way the VS Code extension can be pointed at it,
    /// because it exposes no provider or profile setting of its own.
    /// </summary>
    public static string CodexDefaultsBlock(ClientContext context, bool fullToolAccess = false,
        bool includeProviderDefaults = true)
    {
        var alias = context.DefaultAlias.Length > 0
            ? context.DefaultAlias
            : context.Models.FirstOrDefault()?.Alias ?? string.Empty;

        var builder = new StringBuilder()
            .Append(DefaultsBegin).Append(Environment.NewLine)
            .Append("# Codex and its VS Code extension use these unless a profile overrides")
            .Append(Environment.NewLine)
            .Append("# them. \"Restore original configuration\" in the launcher removes the block.")
            .Append(Environment.NewLine)
            .Append(includeProviderDefaults
                // `oss_provider` is what `codex --oss` selects. Unsloth's own recipe
                // sets it, so `--oss` reaches this provider instead of Ollama or LM Studio.
                ? $"oss_provider   = {Toml(context.ProviderId)}{Environment.NewLine}" +
                  $"model_provider = {Toml(context.ProviderId)}{Environment.NewLine}"
                : string.Empty);

        if (includeProviderDefaults && alias.Length > 0)
        {
            builder.Append($"model          = {Toml(alias)}").Append(Environment.NewLine);
        }

        if (fullToolAccess)
        {
            builder.Append("approval_policy = \"never\"").Append(Environment.NewLine)
                .Append("sandbox_mode    = \"danger-full-access\"").Append(Environment.NewLine);
        }

        return builder.Append(DefaultsEnd).Append(Environment.NewLine).ToString();
    }

    public static string CodexRootConfig(ClientContext context, bool fullToolAccess = false)
    {
        var header = new StringBuilder()
            .AppendLine("# Add this to ~/.codex/config.toml")
            .AppendLine(@"#   Windows: %USERPROFILE%\.codex\config.toml")
            .AppendLine("# The first block makes this the default for Codex and for the")
            .AppendLine("# `openai.chatgpt` VS Code extension, which has no provider setting of")
            .AppendLine("# its own. Root keys must stay above every [table].")
            .AppendLine("# Select one model per run instead with `codex --profile <alias>`.")
            .AppendLine("# Keep both marker comments: the launcher replaces what is between")
            .AppendLine("# them, and without them a later install would append a second")
            .AppendLine("# [model_providers] table, which TOML forbids.")
            .AppendLine();

        // The same markers the installer writes. A file assembled by hand from this
        // text is therefore still a file the installer can update in place.
        return header + CodexDefaultsBlock(context, fullToolAccess) + Environment.NewLine +
               $"{BlockBegin}{Environment.NewLine}" +
               CodexProviderBlock(context).Trim() + Environment.NewLine +
               $"{BlockEnd}{Environment.NewLine}";
    }

    /// <summary>
    /// A Codex profile file: <c>~/.codex/&lt;alias&gt;.config.toml</c>. Profiles are separate
    /// files whose keys are top-level; <c>[profiles.x]</c> inside config.toml stopped
    /// working in Codex 0.134.0.
    /// </summary>
    public static string CodexProfile(ClientModel model, ClientContext? context = null,
        bool fullToolAccess = false)
    {
        var provider = context?.ProviderId ?? GatewayProviderIdConstant;
        var name = context is { SingleProfile: true }
            ? provider
            : model.Alias;

        var access = fullToolAccess
            ? $"approval_policy       = \"never\"{Environment.NewLine}" +
              $"sandbox_mode          = \"danger-full-access\"{Environment.NewLine}"
            : string.Empty;

        return $"# {model.Alias} Ã¢â‚¬â€ run with:  codex --profile {name}{Environment.NewLine}" +
               $"model_provider       = {Toml(provider)}{Environment.NewLine}" +
               $"model                = {Toml(model.Alias)}{Environment.NewLine}" +
               access +
               $"model_context_window = {model.ContextLength.ToString(CultureInfo.InvariantCulture)}" +
               Environment.NewLine;
    }

    private const string GatewayProviderIdConstant = CodexProviderId;

    /// <summary>Where the Codex CLI and the VS Code extension both look.</summary>
    public static string CodexHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(UserHome(), ".codex");

    /// <summary>
    /// Add, refresh or drop the root-defaults block, moving a conflicting root key
    /// aside rather than leaving TOML with a duplicate it refuses to parse.
    /// </summary>
    public static string ApplyCodexDefaults(string existing, ClientContext context, bool makeDefault,
        bool fullToolAccess = false)
    {
        var stripped = StripBlock(existing, DefaultsBegin, DefaultsEnd);
        stripped = makeDefault
            ? DisableRootKeys(stripped)
            : EnableRootKeys(stripped);

        if (!makeDefault && !fullToolAccess)
        {
            return stripped;
        }

        var body = CodexDefaultsBlock(context, fullToolAccess, makeDefault);
        var gap = stripped.Trim().Length > 0 ? Environment.NewLine : string.Empty;
        return body + gap + stripped.TrimStart('\r', '\n');
    }

    /// <summary>Comment out root <c>model</c>/<c>model_provider</c> keys above the first table.</summary>
    private static string DisableRootKeys(string text) => MapRootKeys(text, line =>
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            return line;
        }

        var name = trimmed.Split('=', 2)[0].TrimEnd();
        return name is "model" or "model_provider" ? DisabledPrefix + line : line;
    });

    /// <summary>Undo <see cref="DisableRootKeys"/>.</summary>
    private static string EnableRootKeys(string text) => MapRootKeys(text, line =>
        line.StartsWith(DisabledPrefix, StringComparison.Ordinal)
            ? line[DisabledPrefix.Length..]
            : line);

    /// <summary>
    /// Apply <paramref name="map"/> to every line above the first table header.
    /// TOML puts a key after <c>[table]</c> inside that table, so only the leading
    /// region holds root keys.
    /// </summary>
    private static string MapRootKeys(string text, Func<string, string> map)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith('[') &&
                !trimmed.StartsWith(DisabledPrefix, StringComparison.Ordinal))
            {
                break;
            }

            lines[index] = map(lines[index]);
        }

        return string.Join(newline, lines);
    }

    private static void InstallCodex(Session session)
    {
        var home = CodexHome();
        var root = Path.Combine(home, "config.toml");

        var existing = session.Read(root);
        var updated = ApplyCodexDefaults(
            ReplaceManagedBlock(existing, CodexProviderBlock(session.Context)),
            session.Context,
            session.Options.MakeDefault,
            session.Options.FullToolAccess);
        session.WriteText(ClientTarget.Codex, root, updated, existing);

        var profiles = CodexProfileModels(session.Context);

        var keep = profiles
            .Select(m => session.Context.ProfileFileName(m))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var model in profiles)
        {
            var target = Path.Combine(home, session.Context.ProfileFileName(model));
            session.WriteText(ClientTarget.Codex, target,
                CodexProfile(model, session.Context, session.Options.FullToolAccess),
                session.Read(target));
        }

        // Remove profiles this launcher wrote for models that no longer exist.
        if (Directory.Exists(home))
        {
            foreach (var stale in Directory.GetFiles(home, "*.config.toml"))
            {
                if (keep.Contains(Path.GetFileName(stale)) || !IsOurCodexProfile(stale))
                {
                    continue;
                }

                session.Report.BackedUp(Backup(stale));
                File.Delete(stale);
                session.Report.Removed(stale);
            }
        }

        if (session.Options.PersistEnvironment)
        {
            // Codex Ã¢â‚¬â€ CLI and extension alike Ã¢â‚¬â€ reads the key from the process
            // environment named by `env_key`, which only a per-user variable reaches.
            var changed = PersistentEnvironment.Apply(Slug(ClientTarget.Codex),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [session.Context.KeyEnvName] = session.Context.ApiKey,
                },
                session.State);
            foreach (var where in changed)
            {
                session.Report.Note($"set {session.Context.KeyEnvName} in {where}");
            }
        }
    }

    /// <summary>
    /// The models that get a Codex profile file. Against the gateway that is every
    /// model; against Unsloth every model shares one profile Ã¢â‚¬â€ a model id is a
    /// repository path and cannot be a file name Ã¢â‚¬â€ so only the chosen model is
    /// written, and the rest are selected with <c>--model</c>.
    /// </summary>
    public static IReadOnlyList<ClientModel> CodexProfileModels(ClientContext context)
    {
        if (!context.SingleProfile)
        {
            return context.Models;
        }

        var chosen = context.Models.FirstOrDefault(m => m.Alias == context.DefaultAlias)
                     ?? context.Models.FirstOrDefault();
        return chosen is null ? Array.Empty<ClientModel>() : new[] { chosen };
    }

    /// <summary>
    /// True when this launcher wrote the profile Ã¢â‚¬â€ either provider id counts, so
    /// switching between the gateway and Unsloth still prunes the other's leftovers.
    /// </summary>
    private static bool IsOurCodexProfile(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return text.Contains($"model_provider       = \"{ClientContext.GatewayProviderId}\"",
                       StringComparison.Ordinal) ||
                   text.Contains($"model_provider       = \"{ClientContext.UnslothProviderId}\"",
                       StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ============================================================= Qwen Code #
    /// <summary>Suffix that marks a <c>modelProviders.openai[]</c> entry as this launcher's.</summary>
    public const string QwenProviderSuffix = " Ã¢â‚¬â€ Padra (Unsloth)";

    /// <summary>The provider entries this launcher owns, one per model.</summary>
    public static JsonArray QwenProviders(ClientContext context)
    {
        var providers = new JsonArray();
        foreach (var model in context.Models)
        {
            providers.Add(new JsonObject
            {
                ["id"] = model.Alias,
                ["name"] = model.Alias + QwenProviderSuffix,
                ["description"] = model.Spec.Length > 0 ? model.Spec : model.Alias,
                ["baseUrl"] = context.BaseUrl,
                ["envKey"] = context.KeyEnvName,
                // Qwen Code sizes its context window from here; without it the
                // agent assumes a default far below what the model offers.
                ["generationConfig"] = new JsonObject
                {
                    ["contextWindowSize"] = model.ContextLength,
                },
            });
        }

        return providers;
    }

    /// <summary>True when this launcher wrote the provider entry.</summary>
    public static bool IsOurQwenProvider(JsonObject entry) =>
        entry["name"] is JsonValue name && name.TryGetValue<string>(out var text) &&
        text.EndsWith(QwenProviderSuffix, StringComparison.Ordinal);

    /// <summary>
    /// The user's own <c>openai</c> provider entries, plus ours. A user's entries Ã¢â‚¬â€
    /// a cloud account, another local server Ã¢â‚¬â€ are kept in place; only entries this
    /// launcher wrote before, and entries whose id a model here now takes, are
    /// replaced.
    /// </summary>
    public static JsonArray MergeQwenProviders(JsonNode? existing, ClientContext context)
    {
        var ours = QwenProviders(context);
        var taken = ours.Select(p => p!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new JsonArray();
        if (existing is JsonArray current)
        {
            foreach (var entry in current)
            {
                if (entry is not JsonObject item || IsOurQwenProvider(item))
                {
                    continue;
                }

                var id = item["id"] is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : string.Empty;
                if (taken.Contains(id))
                {
                    continue;
                }

                merged.Add(item.DeepClone());
            }
        }

        foreach (var entry in ours)
        {
            merged.Add(entry!.DeepClone());
        }

        return merged;
    }

    public static JsonObject QwenSettings(ClientContext context, bool fullToolAccess = false)
    {
        var settings = new JsonObject
        {
            ["modelProviders"] = new JsonObject { ["openai"] = QwenProviders(context) },
            ["env"] = new JsonObject
            {
                [context.KeyEnvName] = context.ApiKey,
                ["OPENAI_API_KEY"] = context.ApiKey,
                ["OPENAI_BASE_URL"] = context.BaseUrl,
                ["OPENAI_MODEL"] = context.DefaultAlias,
            },
            ["security"] = new JsonObject
            {
                ["auth"] = new JsonObject { ["selectedType"] = "openai" },
            },
            // `model.name` is the key Qwen Code itself persists when /model runs;
            // it selects the provider entry above. Nothing undocumented goes here.
            ["model"] = new JsonObject { ["name"] = context.DefaultAlias },
        };

        if (fullToolAccess)
        {
            settings["tools"] = new JsonObject { ["approvalMode"] = "yolo" };
        }

        return settings;
    }

    /// <summary>
    /// The managed block for <c>~/.qwen/.env</c>. Qwen Code loads <c>.qwen/.env</c>
    /// before it falls back to the <c>env</c> section of settings.json, and only for
    /// variables absent from the process environment Ã¢â‚¬â€ which makes this the reliable
    /// way to hand it the key and endpoint without touching the user's shell.
    /// </summary>
    public static string QwenDotEnv(ClientContext context)
    {
        var lines = new List<string>
        {
            $"{context.KeyEnvName}={context.ApiKey}",
            $"OPENAI_API_KEY={context.ApiKey}",
            $"OPENAI_BASE_URL={context.BaseUrl}",
        };

        if (context.DefaultAlias.Length > 0)
        {
            lines.Add($"OPENAI_MODEL={context.DefaultAlias}");
        }

        return string.Join('\n', lines) + '\n';
    }

    /// <summary>Insert, replace or drop the marker-delimited block of a dotenv file.</summary>
    public static string ApplyDotEnvBlock(string existing, string body)
    {
        var stripped = StripBlock(existing, BlockBegin, BlockEnd);
        if (body.Trim().Length == 0)
        {
            return stripped;
        }

        var block = $"{BlockBegin}\n{body.TrimEnd('\n')}\n{BlockEnd}\n";
        var head = stripped.TrimEnd('\r', '\n');
        return head.Length > 0 ? head + "\n\n" + block : block;
    }

    public static string QwenHome() =>
        Environment.GetEnvironmentVariable("QWEN_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(UserHome(), ".qwen");

    private static void InstallQwen(Session session)
    {
        var target = Path.Combine(QwenHome(), "settings.json");

        var merged = session.LoadJsonObject(ClientTarget.Qwen, target);
        var fresh = QwenSettings(session.Context, session.Options.FullToolAccess);

        // Merged, never replaced: a user who already has providers of their own Ã¢â‚¬â€
        // a cloud account, another local server Ã¢â‚¬â€ keeps every one of them.
        var providers = Section(merged, "modelProviders");
        providers["openai"] = MergeQwenProviders(providers["openai"], session.Context);

        var env = Section(merged, "env");
        foreach (var (key, value) in (JsonObject)fresh["env"]!)
        {
            env[key] = value?.DeepClone();
        }

        Section(Section(merged, "security"), "auth")["selectedType"] = "openai";
        if (session.Options.FullToolAccess)
        {
            Section(merged, "tools")["approvalMode"] = "yolo";
        }
        else if (merged["tools"] is JsonObject tools &&
                 tools["approvalMode"]?.GetValue<string>() == "yolo")
        {
            tools.Remove("approvalMode");
            if (tools.Count == 0)
            {
                merged.Remove("tools");
            }
        }

        if (session.Context.DefaultAlias.Length > 0)
        {
            Section(merged, "model")["name"] = session.Context.DefaultAlias;
        }

        // An older install wrote `model.baseUrl`, which Qwen Code never read;
        // reinstalling cleans it up rather than leaving an unknown key behind.
        if (merged["model"] is JsonObject modelSection)
        {
            modelSection.Remove("baseUrl");
        }

        session.WriteJson(ClientTarget.Qwen, target, merged);

        // `.qwen/.env` is Qwen Code's own dotenv: it beats the settings `env`
        // section and reaches the VS Code companion, which shares the same home.
        var dotEnv = Path.Combine(QwenHome(), ".env");
        var existing = session.Read(dotEnv);
        session.WriteText(ClientTarget.Qwen, dotEnv,
            ApplyDotEnvBlock(existing, QwenDotEnv(session.Context)), existing);
    }

    // =========================================================== Claude Code #
    /// <summary>Claude Code talks the Anthropic Messages API to <c>ANTHROPIC_BASE_URL</c>.</summary>
    public static Dictionary<string, string> ClaudeEnvironment(ClientContext context)
    {
        var alias = context.DefaultAlias.Length > 0
            ? context.DefaultAlias
            : context.Models.FirstOrDefault()?.Alias ?? string.Empty;

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANTHROPIC_BASE_URL"] = context.AnthropicBase,
            ["ANTHROPIC_AUTH_TOKEN"] = context.ApiKey,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            // Claude Code prepends an attribution header that changes every turn,
            // which invalidates the prompt cache a local model depends on and costs
            // most of its speed. Unsloth's own guide turns it off for this reason.
            ["CLAUDE_CODE_ATTRIBUTION_HEADER"] = "0",
            ["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"] = "1",
            ["API_TIMEOUT_MS"] = "600000",
        };

        if (alias.Length > 0)
        {
            // Every alias family Claude Code resolves has to land on the local
            // model, including `fable` and the haiku alias its background tasks and
            // subagents use Ã¢â‚¬â€ one missing entry and that traffic goes to Anthropic.
            env["ANTHROPIC_MODEL"] = alias;
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = alias;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = alias;
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = alias;
            env["ANTHROPIC_DEFAULT_FABLE_MODEL"] = alias;
        }

        return env;
    }

    public static JsonObject ClaudeSettings(ClientContext context, bool fullToolAccess = false)
    {
        var env = new JsonObject();
        foreach (var (key, value) in ClaudeEnvironment(context))
        {
            env[key] = value;
        }

        var settings = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/claude-code-settings.json",
            ["env"] = env,
        };

        if (context.DefaultAlias.Length > 0)
        {
            settings["model"] = context.DefaultAlias;
        }

        if (fullToolAccess)
        {
            settings["permissions"] = new JsonObject { ["defaultMode"] = "bypassPermissions" };
        }

        return settings;
    }

    private static void InstallClaude(Session session)
    {
        var target = Path.Combine(UserHome(), ".claude", "settings.json");
        var merged = session.LoadJsonObject(ClientTarget.Claude, target);

        var env = Section(merged, "env");
        foreach (var (key, value) in ClaudeEnvironment(session.Context))
        {
            env[key] = value;
        }

        // A subscription key would win over the gateway token, so clear it.
        env.Remove("ANTHROPIC_API_KEY");

        if (session.Context.DefaultAlias.Length > 0)
        {
            merged["model"] = session.Context.DefaultAlias;
        }

        var permissions = merged["permissions"] as JsonObject;
        if (session.Options.FullToolAccess)
        {
            (permissions ??= Section(merged, "permissions"))["defaultMode"] = "bypassPermissions";
        }
        else if (permissions?["defaultMode"]?.GetValue<string>() == "bypassPermissions")
        {
            permissions.Remove("defaultMode");
            if (permissions.Count == 0)
            {
                merged.Remove("permissions");
            }
        }

        session.WriteJson(ClientTarget.Claude, target, merged);
    }

    // ============================================================== Continue #
    public static string ContinueConfig(ClientContext context)
    {
        var lines = new List<string>
        {
            "# ~/.continue/config.yaml Ã¢â‚¬â€ Continue extension for VS Code and JetBrains",
            "name: padra-unsloth",
            "version: 0.0.1",
            "schema: v1",
            "models:",
        };

        if (context.Models.Count == 0)
        {
            lines.Add("  []");
        }

        foreach (var model in context.Models)
        {
            lines.AddRange(new[]
            {
                $"  - name: {YamlScalar(model.Alias)}",
                "    provider: openai",
                $"    model: {YamlScalar(model.Alias)}",
                $"    apiBase: {YamlScalar(context.BaseUrl)}",
                $"    apiKey: {YamlScalar(context.ApiKey)}",
                "    defaultCompletionOptions:",
                $"      contextLength: {model.ContextLength.ToString(CultureInfo.InvariantCulture)}",
                "    capabilities: [tool_use]",
                $"    useResponsesApi: {(context.Direct ? "true" : "false")}",
                "    roles: [chat, edit, apply]",
            });
        }

        return string.Join('\n', lines) + '\n';
    }

    private static void InstallContinue(Session session)
    {
        var target = Path.Combine(UserHome(), ".continue", "config.yaml");
        session.WriteText(ClientTarget.Continue, target, ContinueConfig(session.Context),
            session.Read(target));
    }

    /// <summary>
    /// Roo Code imports provider profiles from a JSON export. Keeping this file
    /// outside VS Code's extension storage makes it portable and restorable.
    /// </summary>
    public static JsonObject RooCodeSettings(ClientContext context)
    {
        var profiles = new JsonObject();
        foreach (var model in context.Models)
        {
            var profileName = $"Padra - {model.Alias}";
            profiles[profileName] = new JsonObject
            {
                ["id"] = $"padra-{StableSlug(model.Alias)}",
                ["apiProvider"] = "openai",
                ["openAiBaseUrl"] = context.BaseUrl,
                ["openAiApiKey"] = context.ApiKey,
                ["openAiModelId"] = model.Alias,
                ["openAiStreamingEnabled"] = true,
                ["openAiCustomModelInfo"] = new JsonObject
                {
                    ["contextWindow"] = model.ContextLength,
                    ["supportsPromptCache"] = false,
                    ["supportsTemperature"] = true,
                },
                ["rateLimitSeconds"] = 0,
                ["consecutiveMistakeLimit"] = 3,
                ["todoListEnabled"] = true,
            };
        }

        var defaultProfile = context.Models.FirstOrDefault(m =>
            string.Equals(m.Alias, context.DefaultAlias, StringComparison.OrdinalIgnoreCase))
            ?? context.Models.FirstOrDefault();
        var defaultName = defaultProfile is null ? "Padra" : $"Padra - {defaultProfile.Alias}";

        return new JsonObject
        {
            ["providerProfiles"] = new JsonObject
            {
                ["currentApiConfigName"] = defaultName,
                ["apiConfigs"] = profiles,
                ["migrations"] = new JsonObject
                {
                    ["rateLimitSecondsMigrated"] = true,
                    ["openAiHeadersMigrated"] = true,
                    ["consecutiveMistakeLimitMigrated"] = true,
                    ["todoListEnabledMigrated"] = true,
                    ["claudeCodeLegacySettingsMigrated"] = true,
                },
            },
            ["globalSettings"] = new JsonObject
            {
                ["diffEnabled"] = true,
                ["fuzzyMatchThreshold"] = 1,
            },
        };
    }

    private static string StableSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "model" : slug;
    }

    private static string RooCodeSettingsPath() =>
        Path.Combine(UserHome(), ".padra", RooCodeSettingsFileName);

    private static void InstallRooCode(Session session)
    {
        var configPath = RooCodeSettingsPath();
        session.WriteJson(ClientTarget.RooCode, configPath, RooCodeSettings(session.Context));

        foreach (var directory in VsCodeUserDirectories())
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var settings = session.LoadJsonObject(ClientTarget.RooCode, settingsPath);
            settings[RooCodeImportSetting] = configPath;
            session.WriteJson(ClientTarget.RooCode, settingsPath, settings);
        }
    }

    // =============================================================== VS Code #
    /// <summary>Every VS Code family user-settings folder present on this machine.</summary>
    public static IReadOnlyList<string> VsCodeUserDirectories()
    {
        string root;
        if (IsWindows)
        {
            root = Environment.GetEnvironmentVariable("APPDATA")
                   ?? Path.Combine(UserHome(), "AppData", "Roaming");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            root = Path.Combine(UserHome(), "Library", "Application Support");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                   ?? Path.Combine(UserHome(), ".config");
        }

        return new[] { "Code", "Code - Insiders", "VSCodium" }
            .Select(flavour => Path.Combine(root, flavour, "User"))
            .Where(Directory.Exists)
            .ToList();
    }

    /// <summary>The environment every agent needs, as VS Code terminals see it.</summary>
    public static Dictionary<string, string> TerminalEnvironment(ClientContext context)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [context.KeyEnvName] = context.ApiKey,
            ["OPENAI_API_KEY"] = context.ApiKey,
            ["OPENAI_BASE_URL"] = context.BaseUrl,
        };

        if (context.DefaultAlias.Length > 0)
        {
            env["OPENAI_MODEL"] = context.DefaultAlias;
        }

        foreach (var (key, value) in ClaudeEnvironment(context))
        {
            env[key] = value;
        }

        return env;
    }

    /// <summary>
    /// The user-settings keys the three extensions read.
    /// <para>
    /// Claude Code takes its environment from a machine-scoped array of
    /// name/value pairs. Qwen Code has no endpoint setting Ã¢â‚¬â€ it is switched off the
    /// Qwen sign-in and onto the providers written into <c>~/.qwen/settings.json</c>.
    /// The Codex extension exposes no provider setting at all, so it is steered
    /// through <c>~/.codex/config.toml</c> and the per-user environment instead; the
    /// terminal block below is what makes the three CLIs work in the editor's own
    /// terminal.
    /// </para>
    /// </summary>
    public static JsonObject VsCodeSettings(ClientContext context, bool fullToolAccess = false)
    {
        var variables = new JsonArray();
        foreach (var (key, value) in ClaudeEnvironment(context))
        {
            variables.Add(new JsonObject { ["name"] = key, ["value"] = value });
        }

        var terminal = new JsonObject();
        foreach (var (key, value) in TerminalEnvironment(context))
        {
            terminal[key] = value;
        }

        var settings = new JsonObject
        {
            ["claudeCode.environmentVariables"] = variables,
            ["claudeCode.disableLoginPrompt"] = true,
            ["qwen-code.provider"] = "api-key",
            [TerminalEnvKey] = terminal,
        };

        if (fullToolAccess)
        {
            settings["chat.tools.global.autoApprove"] = true;
        }

        return settings;
    }

    /// <summary>
    /// The <c>chatLanguageModels.json</c> provider Copilot Chat reads for
    /// bring-your-own-key endpoints: one provider object carrying every alias.
    /// </summary>
    public static JsonArray CopilotProviders(ClientContext context)
    {
        var models = new JsonArray();
        foreach (var model in context.Models)
        {
            models.Add(new JsonObject
            {
                ["id"] = model.Alias,
                ["name"] = $"{model.Alias} (Padra)",
                ["url"] = $"{context.BaseUrl}/chat/completions",
                ["toolCalling"] = true,
                ["maxInputTokens"] = model.ContextLength,
                ["maxOutputTokens"] = 8192,
            });
        }

        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = CopilotProviderName,
                ["vendor"] = "customendpoint",
                ["apiKey"] = context.ApiKey,
                ["apiType"] = "chat-completions",
                ["models"] = models,
            },
        };
    }

    public static JsonObject VsCodeTasks(ClientContext context)
    {
        var origin = context.BaseUrl.EndsWith("/v1", StringComparison.Ordinal)
            ? context.BaseUrl[..^3]
            : context.BaseUrl;

        var input = new JsonObject
        {
            ["id"] = "padraModel",
            ["type"] = context.Models.Count > 0 ? "pickString" : "promptString",
            ["description"] = "Model alias served by the gateway",
            ["default"] = context.DefaultAlias.Length > 0 ? context.DefaultAlias : "model",
        };

        if (context.Models.Count > 0)
        {
            var options = new JsonArray();
            foreach (var model in context.Models)
            {
                options.Add(model.Alias);
            }

            input["options"] = options;
        }

        return new JsonObject
        {
            ["version"] = "2.0.0",
            ["tasks"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = "Padra: start the gateway",
                    ["type"] = "shell",
                    ["command"] = "Padra.Server --config models.yaml",
                    ["options"] = new JsonObject { ["cwd"] = context.ProjectDirectory },
                    ["isBackground"] = true,
                    ["problemMatcher"] = new JsonArray(),
                    ["presentation"] = new JsonObject
                    {
                        ["panel"] = "dedicated",
                        ["reveal"] = "always",
                    },
                },
                new JsonObject
                {
                    ["label"] = "Padra: gateway status",
                    ["type"] = "shell",
                    ["command"] = $"curl -s -H \"Authorization: Bearer {context.ApiKey}\" " +
                                  $"{origin}{(context.Direct ? "/api/health" : "/admin/status")}",
                    ["problemMatcher"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["label"] = "Padra: Codex against a local model",
                    ["type"] = "shell",
                    ["command"] = context.SingleProfile
                        ? $"codex --profile {context.ProviderId} --model ${{input:padraModel}}"
                        : "codex --profile ${input:padraModel}",
                    ["problemMatcher"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["label"] = "Padra: Qwen Code against a local model",
                    ["type"] = "shell",
                    ["command"] = "qwen --model ${input:padraModel}",
                    ["problemMatcher"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["label"] = "Padra: Claude Code against a local model",
                    ["type"] = "shell",
                    ["command"] = "claude --model ${input:padraModel}",
                    ["problemMatcher"] = new JsonArray(),
                },
            },
            ["inputs"] = new JsonArray { input },
        };
    }

    private static void InstallVsCode(Session session)
    {
        var directories = VsCodeUserDirectories();
        if (directories.Count == 0)
        {
            session.Report.Note("no VS Code user folder was found on this machine");
            return;
        }

        foreach (var directory in directories)
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var settings = session.LoadJsonObject(ClientTarget.VsCode, settingsPath);
            foreach (var (key, value) in VsCodeSettings(
                         session.Context, session.Options.FullToolAccess))
            {
                if (TerminalEnvKeys.Contains(key, StringComparer.Ordinal))
                {
                    // Other people's terminal variables have to survive.
                    var block = Section(settings, key);
                    foreach (var (name, entry) in (JsonObject)value!)
                    {
                        block[name] = entry?.DeepClone();
                    }

                    continue;
                }

                if (key == "chat.tools.global.autoApprove" && !session.Options.FullToolAccess &&
                    settings[key]?.GetValue<bool>() == true)
                {
                    settings.Remove(key);
                    continue;
                }

                settings[key] = value?.DeepClone();
            }

            if (!session.Options.FullToolAccess &&
                settings["chat.tools.global.autoApprove"]?.GetValue<bool>() == true)
            {
                settings.Remove("chat.tools.global.autoApprove");
            }

            session.WriteJson(ClientTarget.VsCode, settingsPath, settings);

            var modelsPath = Path.Combine(directory, "chatLanguageModels.json");
            var kept = new JsonArray();
            foreach (var entry in session.LoadJsonArray(ClientTarget.VsCode, modelsPath))
            {
                if (entry is JsonObject item && !IsOurCopilotProvider(item))
                {
                    kept.Add(item.DeepClone());
                }
            }

            foreach (var entry in CopilotProviders(session.Context))
            {
                kept.Add(entry!.DeepClone());
            }

            session.WriteJson(ClientTarget.VsCode, modelsPath, kept);
        }
    }

    private static bool IsOurCopilotProvider(JsonObject item) =>
        string.Equals(item["name"]?.GetValue<string>(), CopilotProviderName, StringComparison.Ordinal);

    // ========================================================= shell exports #
    public static string EnvSh(ClientContext context)
    {
        var env = ClaudeEnvironment(context);
        return $"""
            # source this file:  . ./env.sh
            export {context.KeyEnvName}="{context.ApiKey}"

            # Qwen Code and any OpenAI-compatible client
            export OPENAI_API_KEY="{context.ApiKey}"
            export OPENAI_BASE_URL="{context.BaseUrl}"
            export OPENAI_MODEL="{context.DefaultAlias}"

            # Claude Code (Anthropic Messages API)
            export ANTHROPIC_BASE_URL="{env["ANTHROPIC_BASE_URL"]}"
            export ANTHROPIC_AUTH_TOKEN="{context.ApiKey}"
            export ANTHROPIC_MODEL="{context.DefaultAlias}"

            # Codex reads {context.KeyEnvName} through env_key in ~/.codex/config.toml

            """;
    }

    public static string EnvPs1(ClientContext context)
    {
        var env = ClaudeEnvironment(context);
        return $"""
            # dot-source this file:  . .\env.ps1
            $env:{context.KeyEnvName} = "{context.ApiKey}"
            $env:OPENAI_API_KEY = "{context.ApiKey}"
            $env:OPENAI_BASE_URL = "{context.BaseUrl}"
            $env:OPENAI_MODEL = "{context.DefaultAlias}"
            $env:ANTHROPIC_BASE_URL = "{env["ANTHROPIC_BASE_URL"]}"
            $env:ANTHROPIC_AUTH_TOKEN = "{context.ApiKey}"
            $env:ANTHROPIC_MODEL = "{context.DefaultAlias}"

            # Make the key permanent for the Codex VS Code extension, which reads it
            # from the environment of the editor process:
            #   [Environment]::SetEnvironmentVariable("{context.KeyEnvName}", "{context.ApiKey}", "User")

            """;
    }

    public static string BundleReadme(ClientContext context, IEnumerable<ClientTarget> targets)
    {
        var aliases = context.Models.Count > 0
            ? string.Join(", ", context.Models.Select(m => m.Alias))
            : "(none configured yet)";

        return $"""
            Padra Platform Launcher Ã¢â‚¬â€ client configuration
            ==============================================

            Endpoint : {context.BaseUrl}
            Key      : {context.ApiKey}
            Models   : {aliases}
            Serving  : {(context.Direct ? "Unsloth's own server, reached directly" : "the Padra gateway")}
            Included : {string.Join(", ", targets.Select(Slug))}

            Codex        copy codex/config.toml into ~/.codex/config.toml and each
                         <alias>.config.toml into ~/.codex/, then run
                         `codex --profile <alias>`. Export {context.KeyEnvName} first Ã¢â‚¬â€ the
                         VS Code extension reads it from the editor's own
                         environment, so set it as a permanent user variable.
            Qwen Code    copy qwen/settings.json to ~/.qwen/settings.json and
                         qwen/.env to ~/.qwen/.env, then run `qwen --model <alias>`.
                         In VS Code set `qwen-code.provider` to `api-key` so it
                         stops asking for the Qwen sign-in.
            Claude Code  copy claude/settings.json to ~/.claude/settings.json, then run
                         `claude`. It uses the Anthropic Messages API of this gateway.
            Continue     copy continue/config.yaml to ~/.continue/config.yaml.
            Roo Code    import roo-code/settings.json through VS Code's
                        roo-cline.autoImportSettingsPath setting.
            VS Code      merge vscode/settings.json into your user settings.json and
                         vscode/chatLanguageModels.json into the file Chat: Manage
                         Language Models opens; tasks.json belongs in .vscode/ of a
                         project.
            Shell        `. ./env.sh` (bash/zsh) or `. .\env.ps1` (PowerShell).

            Every machine on the network uses the same key. Keep it private.

            """;
    }

    // =============================================================== compose #
    /// <summary>Relative path -> file content for everything <paramref name="targets"/> covers.</summary>
    public static IReadOnlyDictionary<string, string> GeneratedFiles(
        ClientContext context, IEnumerable<ClientTarget>? targets = null,
        bool fullToolAccess = false)
    {
        var chosen = (targets ?? AllTargets).Distinct().ToList();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        if (chosen.Contains(ClientTarget.Codex))
        {
            files["codex/config.toml"] = CodexRootConfig(context, fullToolAccess);
            foreach (var model in CodexProfileModels(context))
            {
                files[$"codex/{context.ProfileFileName(model)}"] =
                    CodexProfile(model, context, fullToolAccess);
            }
        }

        if (chosen.Contains(ClientTarget.Qwen))
        {
            files["qwen/settings.json"] = Serialise(QwenSettings(context, fullToolAccess));
            files["qwen/.env"] = QwenDotEnv(context);
        }

        if (chosen.Contains(ClientTarget.Claude))
        {
            files["claude/settings.json"] = Serialise(ClaudeSettings(context, fullToolAccess));
        }

        if (chosen.Contains(ClientTarget.Continue))
        {
            files["continue/config.yaml"] = ContinueConfig(context);
        }

        if (chosen.Contains(ClientTarget.RooCode))
        {
            files["roo-code/settings.json"] = Serialise(RooCodeSettings(context));
        }

        if (chosen.Contains(ClientTarget.VsCode))
        {
            files["vscode/settings.json"] =
                Serialise(VsCodeSettings(context, fullToolAccess));
            files["vscode/chatLanguageModels.json"] = Serialise(CopilotProviders(context));
            files["vscode/tasks.json"] = Serialise(VsCodeTasks(context));
        }

        files["env.sh"] = EnvSh(context);
        files["env.ps1"] = EnvPs1(context);
        files["README.txt"] = BundleReadme(context, chosen);
        return files;
    }

    public static IReadOnlyList<string> WriteGenerated(
        ClientContext context, string outputDirectory, IEnumerable<ClientTarget>? targets = null,
        bool fullToolAccess = false)
    {
        var written = new List<string>();
        foreach (var (relative, content) in GeneratedFiles(context, targets, fullToolAccess))
        {
            var path = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            WriteText(path, content);
            written.Add(path);
        }

        return written;
    }

    public static InstallReport Install(ClientContext context, IEnumerable<ClientTarget>? targets = null,
        InstallOptions? options = null, ClientInstallState? state = null)
    {
        var report = new InstallReport();
        var session = new Session(context, report, state ?? ClientInstallState.Open(),
            options ?? InstallOptions.Default);

        foreach (var target in (targets ?? AllTargets).Distinct())
        {
            try
            {
                switch (target)
                {
                    case ClientTarget.Codex: InstallCodex(session); break;
                    case ClientTarget.Qwen: InstallQwen(session); break;
                    case ClientTarget.Claude: InstallClaude(session); break;
                    case ClientTarget.Continue: InstallContinue(session); break;
                    case ClientTarget.RooCode: InstallRooCode(session); break;
                    case ClientTarget.VsCode: InstallVsCode(session); break;
                    default: report.Note($"unknown target `{target}` Ã¢â‚¬â€ skipped"); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                report.Failure(target, ex);
            }
        }

        return report;
    }

    // ============================================================= uninstall #
    /// <summary>
    /// Put every chosen agent back the way it was.
    /// <para>
    /// A file captured before the first install is written back byte for byte, and
    /// one the launcher created is deleted. Anything with no record Ã¢â‚¬â€ an agent
    /// configured by an older build, or a file the user has since moved Ã¢â‚¬â€ falls
    /// back to removing exactly the keys and blocks this launcher writes, which
    /// leaves the rest of that file untouched.
    /// </para>
    /// </summary>
    public static InstallReport Uninstall(IEnumerable<ClientTarget>? targets = null,
        ClientInstallState? state = null)
    {
        var report = new InstallReport();
        var store = state ?? ClientInstallState.Open();
        var chosen = (targets ?? AllTargets).Distinct().ToList();
        var slugs = chosen.Select(Slug).ToList();

        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in store.Take(slugs))
        {
            try
            {
                if (!record.Existed)
                {
                    if (File.Exists(record.Path))
                    {
                        File.Delete(record.Path);
                        report.Removed(record.Path);
                    }

                    restored.Add(record.Path);
                }
                else if (store.OriginalBytes(record) is { } bytes)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(record.Path)!);
                    File.WriteAllBytes(record.Path, bytes);
                    report.RestoredFile(record.Path);
                    restored.Add(record.Path);
                }
                else
                {
                    report.Note($"no saved copy of {record.Path} Ã¢â‚¬â€ removing the launcher's own settings");
                }

                DeleteBackup(record.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Failed.Add(record.Target);
                report.Note($"{record.Path} could not be restored: {ex.Message}");
            }
        }

        var environment = store.TakeEnvironment(slugs);
        foreach (var where in PersistentEnvironment.Remove(environment, store))
        {
            report.Note($"cleared the launcher's variables from {where}");
        }

        foreach (var target in chosen)
        {
            try
            {
                // Only a target this launcher is recorded as having installed may have
                // its settings stripped. Without that guard a restore reaches into
                // `~/.claude/settings.json` or `~/.qwen/settings.json` that the user
                // wrote themselves, deletes the keys those files happen to share with
                // ours, and reports it as a restore.
                StripTarget(target, restored, report, store.HasRecordFor(new[] { Slug(target) }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Failure(target, ex);
            }
        }

        foreach (var directory in store.CreatedDirectories())
        {
            RemoveIfEmpty(directory, report);
        }

        store.Forget(slugs);
        return report;
    }

    /// <summary>
    /// Remove only what this launcher writes, leaving everything else alone.
    /// </summary>
    /// <param name="installed">
    /// True when the manifest shows this launcher installed this target. The
    /// merged-JSON agents are only touched when it does: their settings files hold
    /// generic keys Ã¢â‚¬â€ <c>ANTHROPIC_BASE_URL</c>, <c>OPENAI_API_KEY</c> Ã¢â‚¬â€ that a user
    /// may have set for a completely different endpoint, and deleting those because
    /// they share a name would be data loss, not a restore. Codex and Continue are
    /// exempt because their files are identified by content this launcher writes.
    /// </param>
    private static void StripTarget(ClientTarget target, ISet<string> alreadyRestored,
        InstallReport report, bool installed)
    {
        switch (target)
        {
            case ClientTarget.Codex:
                StripCodex(alreadyRestored, report);
                break;

            case ClientTarget.Qwen:
                if (!installed)
                {
                    NothingRecorded(target, report);
                    break;
                }

                StripJson(Path.Combine(QwenHome(), "settings.json"), alreadyRestored, report,
                    settings =>
                    {
                        if (settings["modelProviders"] is JsonObject providers)
                        {
                            // Only the entries this launcher wrote go; the user's own
                            // providers in the same list are theirs to keep.
                            var kept = new JsonArray();
                            if (providers["openai"] is JsonArray openai)
                            {
                                foreach (var entry in openai)
                                {
                                    if (entry is JsonObject item && !IsOurQwenProvider(item))
                                    {
                                        kept.Add(item.DeepClone());
                                    }
                                }
                            }

                            if (kept.Count > 0)
                            {
                                providers["openai"] = kept;
                            }
                            else
                            {
                                providers.Remove("openai");
                            }

                            if (providers.Count == 0)
                            {
                                settings.Remove("modelProviders");
                            }
                        }

                        if (settings["env"] is JsonObject env)
                        {
                            env.Remove(EnvKeyName);
                            env.Remove(Runtime.UnslothServer.CodexKeyVariable);
                            env.Remove("OPENAI_API_KEY");
                            env.Remove("OPENAI_BASE_URL");
                            env.Remove("OPENAI_MODEL");
                            if (env.Count == 0)
                            {
                                settings.Remove("env");
                            }
                        }

                        if (settings["model"] is JsonObject model)
                        {
                            model.Remove("baseUrl");
                        }

                        if (settings["tools"] is JsonObject tools &&
                            tools["approvalMode"]?.GetValue<string>() == "yolo")
                        {
                            tools.Remove("approvalMode");
                            if (tools.Count == 0)
                            {
                                settings.Remove("tools");
                            }
                        }
                    });
                StripDotEnv(Path.Combine(QwenHome(), ".env"), alreadyRestored, report);
                break;

            case ClientTarget.Claude:
                if (!installed)
                {
                    NothingRecorded(target, report);
                    break;
                }

                StripJson(Path.Combine(UserHome(), ".claude", "settings.json"), alreadyRestored,
                    report, settings =>
                    {
                        if (settings["env"] is JsonObject env)
                        {
                            foreach (var key in ClaudeEnvironmentKeys)
                            {
                                env.Remove(key);
                            }

                            if (env.Count == 0)
                            {
                                settings.Remove("env");
                            }
                        }

                        if (settings["permissions"] is JsonObject permissions &&
                            permissions["defaultMode"]?.GetValue<string>() == "bypassPermissions")
                        {
                            permissions.Remove("defaultMode");
                            if (permissions.Count == 0)
                            {
                                settings.Remove("permissions");
                            }
                        }
                    });
                break;

            case ClientTarget.Continue:
                StripContinue(alreadyRestored, report);
                break;

            case ClientTarget.RooCode:
                if (!installed)
                {
                    NothingRecorded(target, report);
                    break;
                }

                StripRooCode(alreadyRestored, report);
                break;

            case ClientTarget.VsCode:
                if (!installed)
                {
                    NothingRecorded(target, report);
                    break;
                }

                StripVsCode(alreadyRestored, report);
                break;

            default:
                report.Note($"unknown target `{target}` Ã¢â‚¬â€ skipped");
                break;
        }
    }

    private static void NothingRecorded(ClientTarget target, InstallReport report) =>
        report.Note($"nothing recorded for {Slug(target)} Ã¢â‚¬â€ its settings were left untouched");

    private static readonly IReadOnlyList<string> ClaudeEnvironmentKeys = new[]
    {
        "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL",
        "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC",
        "CLAUDE_CODE_ATTRIBUTION_HEADER", "CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS",
        "API_TIMEOUT_MS",
    };

    private static void StripCodex(ISet<string> alreadyRestored, InstallReport report)
    {
        var home = CodexHome();
        var root = Path.Combine(home, "config.toml");

        if (!alreadyRestored.Contains(root) && File.Exists(root))
        {
            var text = File.ReadAllText(root, Encoding.UTF8);
            var cleaned = EnableRootKeys(
                StripBlock(StripBlock(text, DefaultsBegin, DefaultsEnd), BlockBegin, BlockEnd));
            if (cleaned != text)
            {
                WriteText(root, cleaned);
                report.RestoredFile(root);
            }
        }

        if (!Directory.Exists(home))
        {
            return;
        }

        foreach (var profile in Directory.GetFiles(home, "*.config.toml"))
        {
            if (alreadyRestored.Contains(profile) || !IsOurCodexProfile(profile))
            {
                continue;
            }

            File.Delete(profile);
            report.Removed(profile);
        }
    }

    /// <summary>Remove the launcher's block from a dotenv file, deleting it when empty.</summary>
    private static void StripDotEnv(string path, ISet<string> alreadyRestored, InstallReport report)
    {
        if (alreadyRestored.Contains(path) || !File.Exists(path))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return;
        }

        var cleaned = ApplyDotEnvBlock(text, string.Empty);
        if (cleaned == text)
        {
            return;
        }

        if (cleaned.Trim().Length == 0)
        {
            File.Delete(path);
            report.Removed(path);
            return;
        }

        WriteText(path, cleaned);
        report.RestoredFile(path);
    }

    private static void StripContinue(ISet<string> alreadyRestored, InstallReport report)
    {
        var path = Path.Combine(UserHome(), ".continue", "config.yaml");
        if (alreadyRestored.Contains(path) || !File.Exists(path))
        {
            return;
        }

        if (File.ReadAllText(path, Encoding.UTF8)
            .Contains("name: padra-unsloth", StringComparison.Ordinal))
        {
            File.Delete(path);
            report.Removed(path);
        }
    }

    private static void StripRooCode(ISet<string> alreadyRestored, InstallReport report)
    {
        var configPath = RooCodeSettingsPath();
        if (!alreadyRestored.Contains(configPath) && File.Exists(configPath) &&
            File.ReadAllText(configPath, Encoding.UTF8).Contains("Padra - ", StringComparison.Ordinal))
        {
            File.Delete(configPath);
            report.Removed(configPath);
        }

        foreach (var directory in VsCodeUserDirectories())
        {
            StripJson(Path.Combine(directory, "settings.json"), alreadyRestored, report,
                settings => settings.Remove(RooCodeImportSetting));
        }
    }

    private static void StripVsCode(ISet<string> alreadyRestored, InstallReport report)
    {
        foreach (var directory in VsCodeUserDirectories())
        {
            StripJson(Path.Combine(directory, "settings.json"), alreadyRestored, report, settings =>
            {
                settings.Remove("claudeCode.environmentVariables");
                settings.Remove("claudeCode.disableLoginPrompt");
                settings.Remove("qwen-code.provider");
                settings.Remove("chat.tools.global.autoApprove");

                foreach (var key in TerminalEnvKeys)
                {
                    if (settings[key] is not JsonObject block)
                    {
                        continue;
                    }

                    foreach (var name in ManagedTerminalNames)
                    {
                        block.Remove(name);
                    }

                    if (block.Count == 0)
                    {
                        settings.Remove(key);
                    }
                }
            });

            var modelsPath = Path.Combine(directory, "chatLanguageModels.json");
            if (alreadyRestored.Contains(modelsPath) || !File.Exists(modelsPath))
            {
                continue;
            }

            JsonArray parsed;
            try
            {
                if (ParseLenient(File.ReadAllText(modelsPath, Encoding.UTF8)) is not JsonArray array)
                {
                    continue;
                }

                parsed = array;
            }
            catch (JsonException)
            {
                continue;
            }

            var kept = new JsonArray();
            foreach (var entry in parsed)
            {
                if (entry is JsonObject item && !IsOurCopilotProvider(item))
                {
                    kept.Add(item.DeepClone());
                }
            }

            if (kept.Count == 0)
            {
                File.Delete(modelsPath);
                report.Removed(modelsPath);
            }
            else
            {
                WriteText(modelsPath, Serialise(kept));
                report.RestoredFile(modelsPath);
            }
        }
    }

    /// <summary>
    /// Names the launcher writes into a terminal block. Both key variables are
    /// listed so a restore cleans up whichever mode was last installed.
    /// </summary>
    private static IReadOnlyList<string> ManagedTerminalNames =>
        new[]
            {
                EnvKeyName, Runtime.UnslothServer.CodexKeyVariable,
                "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_MODEL",
            }
            .Concat(ClaudeEnvironmentKeys).ToList();

    private static void StripJson(string path, ISet<string> alreadyRestored, InstallReport report,
        Action<JsonObject> strip)
    {
        if (alreadyRestored.Contains(path) || !File.Exists(path))
        {
            return;
        }

        JsonObject settings;
        try
        {
            if (ParseLenient(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject parsed)
            {
                return;
            }

            settings = parsed;
        }
        catch (JsonException)
        {
            return;
        }

        strip(settings);

        if (settings.Count == 0)
        {
            File.Delete(path);
            report.Removed(path);
            return;
        }

        WriteText(path, Serialise(settings));
        report.RestoredFile(path);
    }

    private static void DeleteBackup(string path)
    {
        try
        {
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .bak is harmless.
        }
    }

    private static void RemoveIfEmpty(string directory, InstallReport report)
    {
        try
        {
            if (Directory.Exists(directory) &&
                Directory.EnumerateFileSystemEntries(directory).FirstOrDefault() is null)
            {
                Directory.Delete(directory);
                report.Removed(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process holds it; leaving an empty folder behind is harmless.
        }
    }

    // ================================================================ plumb #
    /// <summary>Insert or refresh our marker-delimited block, leaving the rest intact.</summary>
    public static string ReplaceManagedBlock(string existing, string block)
    {
        var body = $"{BlockBegin}{Environment.NewLine}{block.Trim()}{Environment.NewLine}{BlockEnd}{Environment.NewLine}";
        var begin = existing.IndexOf(BlockBegin, StringComparison.Ordinal);
        var end = existing.IndexOf(BlockEnd, StringComparison.Ordinal);

        if (begin >= 0 && end > begin)
        {
            var head = existing[..begin].TrimEnd('\r', '\n');
            var tail = existing[(end + BlockEnd.Length)..].TrimStart('\r', '\n');
            var separator = head.Trim().Length > 0
                ? Environment.NewLine + Environment.NewLine
                : string.Empty;
            return head + separator + body + tail;
        }

        var gap = existing.Trim().Length > 0 ? Environment.NewLine + Environment.NewLine : string.Empty;
        return existing.TrimEnd('\r', '\n') + gap + body;
    }

    /// <summary>Remove a marker-delimited block and the blank line it left behind.</summary>
    public static string StripBlock(string existing, string begin, string end)
    {
        var start = existing.IndexOf(begin, StringComparison.Ordinal);
        var stop = existing.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop <= start)
        {
            return existing;
        }

        var head = existing[..start].TrimEnd('\r', '\n');
        var tail = existing[(stop + end.Length)..].TrimStart('\r', '\n');

        if (head.Trim().Length == 0)
        {
            return tail;
        }

        return tail.Trim().Length == 0
            ? head + Environment.NewLine
            : head + Environment.NewLine + Environment.NewLine + tail;
    }

    /// <summary>Parse JSON that may carry comments and trailing commas (VS Code style).</summary>
    public static JsonNode? ParseLenient(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonNode.Parse(text, documentOptions: LenientDocument);
    }

    public static string? Backup(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var target = path + ".bak";
        File.Copy(path, target, overwrite: true);
        return target;
    }

    public static string UserHome() => Discovery.ModelScanner.UserHome();

    private static JsonObject Section(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string Serialise(JsonNode node) => node.ToJsonString(Pretty) + "\n";

    private static void WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Toml(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string YamlScalar(string value)
    {
        if (value.Length > 0 &&
            value.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_' or '.' or '/' or ':'))
        {
            return value;
        }

        return JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// One install run: captures the pristine copy of every path before the first
    /// write, keeps the <c>.bak</c> convenience copy, and reports what changed.
    /// </summary>
    private sealed class Session(
        ClientContext context, InstallReport report, ClientInstallState state, InstallOptions options)
    {
        public ClientContext Context { get; } = context;

        public InstallReport Report { get; } = report;

        public ClientInstallState State { get; } = state;

        public InstallOptions Options { get; } = options;

        public string Read(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        public void WriteText(ClientTarget target, string path, string content, string existing)
        {
            if (File.Exists(path) && existing == content)
            {
                Report.Unchanged(path);
                return;
            }

            State.Capture(Slug(target), path);
            Report.BackedUp(Backup(path));
            ClientConfigWriter.WriteText(path, content);
            Report.Wrote(path);
        }

        /// <summary>
        /// Write a merged JSON document, unless the file already says the same thing.
        /// <para>
        /// The comparison is on the parsed values, not on the text: these files are
        /// JSON-with-comments and reading one drops its comments, so comparing the
        /// serialised form would rewrite a hand-annotated <c>settings.json</c> on
        /// every install and strip every comment in it. Comparing values means a
        /// reinstall that changes nothing touches nothing.
        /// </para>
        /// </summary>
        public void WriteJson(ClientTarget target, string path, JsonNode node)
        {
            State.Capture(Slug(target), path);

            if (File.Exists(path))
            {
                JsonNode? current = null;
                try
                {
                    current = ParseLenient(Read(path));
                }
                catch (JsonException)
                {
                    current = null;
                }

                if (current is not null && JsonNode.DeepEquals(current, node))
                {
                    Report.Unchanged(path);
                    return;
                }
            }

            Report.BackedUp(Backup(path));
            ClientConfigWriter.WriteText(path, Serialise(node));
            Report.Wrote(path);
        }

        public JsonObject LoadJsonObject(ClientTarget target, string path)
        {
            State.Capture(Slug(target), path);
            if (!File.Exists(path))
            {
                return new JsonObject();
            }

            try
            {
                if (ParseLenient(File.ReadAllText(path, Encoding.UTF8)) is JsonObject parsed)
                {
                    return parsed;
                }

                Report.Note($"{path} was not a JSON object Ã¢â‚¬â€ it was replaced");
            }
            catch (JsonException)
            {
                Report.Note($"{path} was not valid JSON Ã¢â‚¬â€ it was replaced");
            }

            return new JsonObject();
        }

        public JsonArray LoadJsonArray(ClientTarget target, string path)
        {
            State.Capture(Slug(target), path);
            if (!File.Exists(path))
            {
                return new JsonArray();
            }

            try
            {
                if (ParseLenient(File.ReadAllText(path, Encoding.UTF8)) is JsonArray parsed)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                Report.Note($"{path} was not valid JSON Ã¢â‚¬â€ it was replaced");
            }

            return new JsonArray();
        }
    }
}
