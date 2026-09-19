using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Padra.Core.Clients;
using Padra.Core.Configuration;
using Padra.Core.Discovery;
using Padra.Core.Networking;
using Padra.Core.Runtime;
using Padra.Web.Services;

namespace Padra.Web.Endpoints;

public sealed record AddModelRequest(string Repo, string? Quant, string? Alias,
    int? ContextLength, bool MakeDefault, string? Reasoning);

public sealed record NetworkRequest(string? Host, int? Port, bool? AllowLan, int? IdleTimeout);

/// <param name="PersistEnvironment">
/// Also write the gateway key into this user's environment. The Codex editor
/// extension reads it from the editor's own process, so the panel always asks for
/// it; automation that must not touch the user environment can turn it off.
/// </param>
public sealed record InstallRequest(List<string>? Targets, string? Scope, bool? MakeDefault,
    bool? PersistEnvironment, bool? FullToolAccess, string? DefaultModel = null);

public sealed record UninstallRequest(List<string>? Targets);

/// <summary>Which server the coding agents should be configured against.</summary>
/// <param name="Model">
/// In direct mode, the model the agents are pointed at, as Unsloth's server names
/// it. An empty string clears the choice so the resident model is used.
/// </param>
public sealed record EndpointRequest(string? Source, string? ServerUrl, string? ApiKey,
    string? Model = null);

/// <summary>A model on Unsloth's server to select, load or unload.</summary>
public sealed record UnslothModelRequest(string Model, bool? Wait);

/// <summary>Studio credentials, used once to create an API key for the agents.</summary>
public sealed record UnslothSignInRequest(string Username, string Password);

public sealed record LaunchRequest(string Agent, string? Alias, string Folder);

/// <summary>The runtime block as the panel and any script send it.</summary>
public sealed record RuntimeRequest(
    string? GpuMemoryMode,
    int? GpuLayers,
    string? Fit,
    int? FitContext,
    int? CpuMoeLayers,
    string? KvCacheType,
    string? FlashAttention,
    int? Threads,
    bool? NoKvOffload,
    bool? SwaFull,
    bool? AllowOversize,
    int? ContextLength);

/// <summary>Everything the control panel calls, and everything a script can automate.</summary>
public static class ControlEndpoints
{
    public static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // ---------------------------------------------------------- status #
        api.MapGet("/status", (ModelFleet fleet) => Results.Json(new
        {
            mode = fleet.Gateway.Mode.ToString().ToLowerInvariant(),
            defaultModel = fleet.DefaultAlias,
            backends = fleet.Backends.Select(b => b.Status()).ToList(),
            models = FleetPayload(fleet),
            network = NetworkPayload(fleet),
        }));

        api.MapGet("/log", (ActivityLog log, long since = 0) =>
        {
            var entries = log.Since(since);
            return Results.Json(new
            {
                entries = entries.Select(e => new
                {
                    seq = e.Seq,
                    at = e.At.ToUnixTimeMilliseconds(),
                    text = e.Text,
                }).ToList(),
                cursor = entries.Count > 0 ? entries[^1].Seq : since,
            });
        });

        api.MapGet("/diagnostics", (ModelFleet fleet) => Results.Json(new
        {
            runtime = Environment.Version.ToString(),
            platform = Environment.OSVersion.ToString(),
            configPath = fleet.ConfigPath,
            gatewayHost = fleet.Gateway.Host,
            gatewayPort = fleet.Gateway.Port,
            hasApiKey = fleet.ApiKey.Length > 0,
            unslothPath = ExecutableFinder.Find(fleet.Gateway.UnslothBin) ?? string.Empty,
            cacheRoots = ModelScanner.HuggingFaceCacheRoots()
                .Select(r => new { path = r, exists = Directory.Exists(r) }).ToList(),
            backendPorts = fleet.Options.Models.Select(m => new
            {
                alias = m.Alias,
                port = m.Port,
                free = NetworkInfo.PortIsFree("127.0.0.1", m.Port),
            }).ToList(),
        }));

        // ----------------------------------------------------------- fleet #
        api.MapPost("/models/{alias}/load", async (string alias, ModelFleet fleet,
            CancellationToken token) => await LoadAsync(alias, fleet, token).ConfigureAwait(false));

        api.MapPost("/models/{alias}/unload", async (string alias, ModelFleet fleet,
            CancellationToken token) =>
        {
            var backend = fleet.Find(alias);
            if (backend is null)
            {
                return NotFound(alias);
            }

            await fleet.UnloadAsync(backend, token).ConfigureAwait(false);
            return Results.Json(backend.Status());
        });

        api.MapPost("/models/{alias}/activate", async (string alias, ModelFleet fleet,
            UserSettings settings, ActivityLog log, CancellationToken token) =>
        {
            var backend = fleet.Find(alias);
            if (backend is null)
            {
                return NotFound(alias);
            }

            fleet.SetDefault(backend.Alias);
            settings.Update(s => s.SelectedAlias = backend.Alias);

            // In exclusive mode EnsureAsync swaps the others out itself, draining
            // their in-flight requests first; unloading here would skip that grace.
            try
            {
                await fleet.EnsureAsync(backend, token).ConfigureAwait(false);
            }
            catch (BackendStartException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                    "backend_start_error");
            }

            log.Write($"{backend.Alias} is now the active model");
            return Results.Json(backend.Status());
        });

        api.MapPost("/models", (AddModelRequest request, ModelFleet fleet,
            ModelLibrary library, ActivityLog log) =>
        {
            var draft = fleet.Options.Clone();
            ModelOptions entry;
            try
            {
                entry = FleetFile.AddModel(draft, request.Repo, request.Quant ?? string.Empty,
                    request.Alias ?? string.Empty, request.ContextLength,
                    reasoning: request.Reasoning ?? string.Empty,
                    makeDefault: request.MakeDefault);
            }
            catch (FleetFileException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, ex.Message,
                    "invalid_request_error");
            }

            if (!TryApply(fleet, draft, log, out var failure))
            {
                return failure!;
            }

            library.Invalidate();
            log.Write($"added `{entry.Alias}` -> {entry.ModelSpec}");
            return Results.Json(new
            {
                ok = true,
                model = new { alias = entry.Alias, repo = entry.Repo, quant = entry.Quant },
                models = FleetPayload(fleet),
            });
        });

        api.MapDelete("/models/{alias}", async (string alias, ModelFleet fleet,
            ModelLibrary library, ActivityLog log, CancellationToken token) =>
        {
            var backend = fleet.Find(alias);
            if (backend is null)
            {
                return NotFound(alias);
            }

            if (backend.IsRunning)
            {
                await fleet.UnloadAsync(backend, token).ConfigureAwait(false);
            }

            var draft = fleet.Options.Clone();
            try
            {
                FleetFile.RemoveModel(draft, alias);
            }
            catch (FleetFileException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, ex.Message,
                    "invalid_request_error");
            }

            if (!TryApply(fleet, draft, log, out var failure))
            {
                return failure!;
            }

            library.Invalidate();
            log.Write($"removed `{alias}` from the fleet");
            return Results.Json(new { ok = true, models = FleetPayload(fleet) });
        });

        // -------------------------------------------------------- endpoint #
        api.MapGet("/endpoint", async (ModelFleet fleet, EndpointResolver resolver,
            CancellationToken token, bool refresh = false) =>
            Results.Json(await EndpointPayload(fleet, resolver, refresh, token)
                .ConfigureAwait(false)));

        api.MapPost("/endpoint", async (EndpointRequest request, ModelFleet fleet,
            EndpointResolver resolver, ActivityLog log, CancellationToken token) =>
        {
            var draft = fleet.Options.Clone();

            if (request.Source is { Length: > 0 } source)
            {
                if (!Enum.TryParse<EndpointSource>(source.Trim(), ignoreCase: true, out var parsed) ||
                    !Enum.IsDefined(parsed))
                {
                    return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                        "source must be `gateway` or `unsloth`", "invalid_request_error");
                }

                draft.Gateway.EndpointSource = parsed;
            }

            if (request.ServerUrl is not null)
            {
                var trimmed = request.ServerUrl.Trim();
                if (trimmed.Length > 0 &&
                    !Uri.TryCreate(UnslothServer.Normalise(trimmed), UriKind.Absolute, out _))
                {
                    return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                        $"`{trimmed}` is not a URL", "invalid_request_error");
                }

                draft.Gateway.UnslothServerUrl = trimmed.Length > 0
                    ? UnslothServer.Normalise(trimmed)
                    : string.Empty;
            }

            if (request.ApiKey is not null)
            {
                draft.Gateway.UnslothApiKey = request.ApiKey.Trim();
            }

            if (request.Model is not null)
            {
                draft.Gateway.UnslothModel = request.Model.Trim();
            }

            if (!TryApply(fleet, draft, log, out var failure))
            {
                return failure!;
            }

            resolver.Invalidate();
            log.Write(
                $"client configuration now targets {(fleet.Gateway.EndpointSource == EndpointSource.Unsloth ? "Unsloth's own server" : "this gateway")}");
            return Results.Json(await EndpointPayload(fleet, resolver, refresh: true, token)
                .ConfigureAwait(false));
        });

        // The model on Unsloth's server the agents are pointed at. The server has to
        // list it, so a typo is refused here instead of written into every agent.
        api.MapPost("/endpoint/select", async (UnslothModelRequest request, ModelFleet fleet,
            EndpointResolver resolver, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, "model is required",
                    "invalid_request_error");
            }

            try
            {
                var model = await resolver.SelectModelAsync(request.Model, token).ConfigureAwait(false);
                return Results.Json(new
                {
                    ok = true,
                    model = UnslothModelPayload(model),
                    endpoint = await EndpointPayload(fleet, resolver, refresh: false, token)
                        .ConfigureAwait(false),
                });
            }
            catch (EndpointUnavailableException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                    "endpoint_unavailable");
            }
            catch (FleetFileException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.InternalServerError, ex.Message,
                    "config_write_error");
            }
        });

        // Make a model resident on Unsloth's server — the only way to switch models
        // when that server runs on another machine. The load runs in the
        // background; `wait: true` blocks until it finishes, for scripts.
        api.MapPost("/endpoint/load", async (UnslothModelRequest request, ModelFleet fleet,
            EndpointResolver resolver, CancellationToken token) =>
            await OperateAsync(request, fleet, resolver, load: true, token).ConfigureAwait(false));

        api.MapPost("/endpoint/unload", async (UnslothModelRequest request, ModelFleet fleet,
            EndpointResolver resolver, CancellationToken token) =>
            await OperateAsync(request, fleet, resolver, load: false, token).ConfigureAwait(false));

        // Sign in to Unsloth Studio once and create an API key for the agents. The
        // credentials are used for that one request and never stored; the key is.
        api.MapPost("/endpoint/key", async (UnslothSignInRequest request, ModelFleet fleet,
            EndpointResolver resolver, CancellationToken token) =>
        {
            try
            {
                var key = await resolver.MintKeyAsync(request.Username ?? string.Empty,
                    request.Password ?? string.Empty, token).ConfigureAwait(false);
                return Results.Json(new
                {
                    ok = true,
                    keyHint = KeyHint(key),
                    endpoint = await EndpointPayload(fleet, resolver, refresh: true, token)
                        .ConfigureAwait(false),
                });
            }
            catch (UnslothServerException ex)
            {
                var status = ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? HttpStatusCode.Unauthorized
                    : ex.Status == HttpStatusCode.TooManyRequests
                        ? HttpStatusCode.TooManyRequests
                        : HttpStatusCode.ServiceUnavailable;
                return GatewayEndpoints.Problem(status, ex.Message, "unsloth_sign_in_error");
            }
            catch (FleetFileException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.InternalServerError, ex.Message,
                    "config_write_error");
            }
        });

        // ---------------------------------------------------- fit and offload #
        api.MapGet("/hardware", (ModelFleet fleet, bool refresh = false) =>
            Results.Json(HardwarePayload(fleet, refresh)));

        api.MapPost("/models/{alias}/runtime", (string alias, RuntimeRequest request,
            ModelFleet fleet, ActivityLog log) =>
        {
            var current = fleet.Options.Models.FirstOrDefault(m => m.Alias == alias);
            if (current is null)
            {
                return NotFound(alias);
            }

            var draft = fleet.Options.Clone();
            try
            {
                FleetFile.SetRuntime(draft, alias, Merge(current.Runtime, request));
                if (request.ContextLength is { } context)
                {
                    FleetFile.SetContextLength(draft, alias, context);
                }
            }
            catch (FleetFileException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, ex.Message,
                    "invalid_request_error");
            }

            if (!TryApply(fleet, draft, log, out var failure))
            {
                return failure!;
            }

            log.Write($"{alias}: runtime settings updated");
            return Results.Json(new { ok = true, model = RuntimePayload(fleet, alias) });
        });

        api.MapPost("/models/{alias}/runtime/auto", (string alias, ModelFleet fleet, ActivityLog log) =>
        {
            var current = fleet.Options.Models.FirstOrDefault(m => m.Alias == alias);
            if (current is null)
            {
                return NotFound(alias);
            }

            var footprint = fleet.Footprint(current);
            if (footprint.Verdict == FitVerdict.Unknown)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                    $"`{alias}` was not found on this disk, so its size cannot be estimated.",
                    "invalid_request_error");
            }

            var header = fleet.WeightsSource?.Invoke(current)?.Header ?? GgufMetadata.Empty;
            var suggested = ModelFootprintCalculator.SuggestRuntime(current.Runtime, footprint, header);

            var draft = fleet.Options.Clone();
            try
            {
                FleetFile.SetRuntime(draft, alias, suggested);
            }
            catch (FleetFileException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, ex.Message,
                    "invalid_request_error");
            }

            if (!TryApply(fleet, draft, log, out var failure))
            {
                return failure!;
            }

            log.Write($"{alias}: runtime settings tuned for this machine");
            return Results.Json(new { ok = true, model = RuntimePayload(fleet, alias) });
        });

        // ------------------------------------------------------- discovery #
        api.MapGet("/models/local", async (ModelLibrary library, CancellationToken token,
            bool refresh = false) =>
        {
            var snapshot = await library.LocalAsync(refresh, token).ConfigureAwait(false);
            return Results.Json(new
            {
                models = snapshot.Models.Select(LocalPayload).ToList(),
                roots = snapshot.Roots,
                totalBytes = snapshot.TotalBytes,
                scannedAt = snapshot.ScannedAt.ToUnixTimeMilliseconds(),
            });
        });

        api.MapGet("/models/catalog", async (ModelLibrary library, CancellationToken token,
            string? search = null, string? repo = null, bool refresh = false) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(repo))
                {
                    var record = await library.Catalog.VariantsAsync(repo!, refresh, token)
                        .ConfigureAwait(false);
                    var local = await library.LocalModelsAsync(token).ConfigureAwait(false);
                    return Results.Json(new { repo = HuggingFaceCatalog.MergeDownloaded(record, local) });
                }

                var repos = await library.Catalog
                    .RepositoriesAsync(search ?? string.Empty, refresh: refresh, token: token)
                    .ConfigureAwait(false);
                var downloaded = (await library.LocalModelsAsync(token).ConfigureAwait(false))
                    .Select(m => m.Repo.ToLowerInvariant()).ToHashSet();

                return Results.Json(new
                {
                    repos = repos.Select(r => new
                    {
                        repo = r.Repo,
                        downloads = r.Downloads,
                        likes = r.Likes,
                        lastModified = r.LastModified,
                        hasLocal = downloaded.Contains(r.Repo.ToLowerInvariant()),
                    }).ToList(),
                });
            }
            catch (CatalogException ex)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                    "catalog_error");
            }
        });

        // --------------------------------------------------------- network #
        api.MapGet("/network", (ModelFleet fleet) => Results.Json(NetworkPayload(fleet)));

        api.MapPost("/network", (NetworkRequest request, ModelFleet fleet, ActivityLog log) =>
        {
            var draft = fleet.Options.Clone();
            var restart = false;

            if (request.AllowLan is { } allow)
            {
                draft.Gateway.AllowLan = allow;
                draft.Gateway.Host = allow ? "0.0.0.0" : "127.0.0.1";
                restart = true;
            }

            if (!string.IsNullOrWhiteSpace(request.Host))
            {
                draft.Gateway.Host = request.Host!.Trim();
                draft.Gateway.AllowLan = draft.Gateway.IsShared;
                restart = true;
            }

            if (request.Port is { } port)
            {
                draft.Gateway.Port = port;
                restart = true;
            }

            if (request.IdleTimeout is { } idle)
            {
                draft.Gateway.IdleTimeout = idle;
            }

            if (restart && draft.Gateway.AllowLan && fleet.ApiKey.Length == 0)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                    "Set a gateway API key before sharing on the network.", "invalid_request_error");
            }

            if (!TryApply(fleet, draft, log, out var failure))
            {
                return failure!;
            }

            return Results.Json(new
            {
                ok = true,
                restartRequired = restart,
                network = NetworkPayload(fleet),
            });
        });

        api.MapPost("/network/firewall", async (ModelFleet fleet, ActivityLog log,
            CancellationToken token) =>
        {
            var port = fleet.Gateway.Port;
            var result = await FirewallHelper.OpenAsync(port, token).ConfigureAwait(false);
            log.Write($"firewall rule for port {port}: " +
                      (result.Ok ? "added" : $"not added — run: {result.Command}"));
            return Results.Json(new
            {
                ok = result.Ok,
                elevated = result.Elevated,
                command = result.Command,
                output = result.Output,
            });
        });

        // --------------------------------------------------------- clients #
        api.MapGet("/clients", (ModelFleet fleet) =>
        {
            var unsloth = ExecutableFinder.Find(fleet.Gateway.UnslothBin);
            return Results.Json(new
            {
                unsloth = new { installed = unsloth is not null, path = unsloth ?? string.Empty },
                agents = AgentLauncher.AllAvailability().Select(a => new
                {
                    id = AgentLauncher.Slug(a.Agent),
                    installed = a.Installed,
                    path = a.Path,
                }).OrderBy(a => a.id, StringComparer.Ordinal).ToList(),
                vscodeFolders = ClientConfigWriter.VsCodeUserDirectories(),
                targets = ClientConfigWriter.AllTargets.Select(ClientConfigWriter.Slug).ToList(),
            });
        });

        api.MapGet("/client-configs", async (EndpointResolver resolver, UserSettings settings,
            CancellationToken token,
            string? scope = null, string? defaultModel = null) =>
        {
            var (context, failure) = await ContextAsync(resolver, scope, token).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            if (context!.Models.Count == 0)
            {
                return NoModels();
            }

            context = context.WithDefaultModel(defaultModel ?? settings.Snapshot().ClientModel);
            return Results.Json(new
            {
                baseUrl = context.BaseUrl,
                source = context.Source.ToString().ToLowerInvariant(),
                files = ClientConfigWriter.GeneratedFiles(context,
                    fullToolAccess: settings.Snapshot().FullToolAccess),
            });
        });

        api.MapGet("/client-bundle", async (EndpointResolver resolver, UserSettings settings,
            ActivityLog log,
            CancellationToken token, string? scope = null, string? defaultModel = null) =>
        {
            var (context, failure) = await ContextAsync(resolver, scope ?? "lan", token)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            if (context!.Models.Count == 0)
            {
                return NoModels();
            }

            context = context.WithDefaultModel(defaultModel ?? settings.Snapshot().ClientModel);
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (relative, content) in ClientConfigWriter.GeneratedFiles(context,
                             fullToolAccess: settings.Snapshot().FullToolAccess))
                {
                    var entry = archive.CreateEntry($"padra-client/{relative}");
                    using var stream = entry.Open();
                    var bytes = Encoding.UTF8.GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            log.Write($"client bundle generated for {context.BaseUrl}");
            return Results.File(buffer.ToArray(), "application/zip", "padra-client.zip");
        });

        api.MapPost("/install-configs", async (InstallRequest request, ModelFleet fleet,
            EndpointResolver resolver, UserSettings settings, ActivityLog log,
            CancellationToken token) =>
        {
            if (!TryTargets(request.Targets, out var targets, out var unknown))
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                    $"Unknown target(s): {string.Join(", ", unknown)}", "invalid_request_error");
            }

            var (resolved, failure) = await ContextAsync(resolver, request.Scope, token)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            var context = resolved!;
            if (context.Models.Count == 0)
            {
                return NoModels();
            }

            var makeDefault = request.MakeDefault ?? settings.Snapshot().MakeDefaultProvider;
            var fullToolAccess = request.FullToolAccess ?? settings.Snapshot().FullToolAccess;
            var defaultModel = request.DefaultModel ?? settings.Snapshot().ClientModel;
            context = context.WithDefaultModel(defaultModel);
            var installOptions = new InstallOptions(makeDefault, request.PersistEnvironment ?? true,
                fullToolAccess);

            var output = Path.Combine(Path.GetDirectoryName(fleet.ConfigPath) ?? ".", "generated");
            var report = await Task.Run(() =>
            {
                ClientConfigWriter.WriteGenerated(context, output, targets, fullToolAccess);
                return ClientConfigWriter.Install(context, targets, installOptions);
            }).ConfigureAwait(false);

            foreach (var line in report.Lines)
            {
                log.Write(line);
            }

            settings.Update(s =>
            {
                s.InstallTargets = targets.Select(ClientConfigWriter.Slug).ToList();
                s.MakeDefaultProvider = makeDefault;
                s.FullToolAccess = fullToolAccess;
                s.ClientModel = context.DefaultAlias;
            });

            return Results.Json(new
            {
                ok = report.Ok,
                written = report.Written,
                failed = report.Failed,
                output = report.Lines,
                generated = output,
                baseUrl = context.BaseUrl,
                canRestore = ClientInstallState.Open().HasRecordFor(
                    targets.Select(ClientConfigWriter.Slug)),
            });
        });

        api.MapPost("/uninstall-configs", async (UninstallRequest request, ActivityLog log) =>
        {
            if (!TryTargets(request.Targets, out var targets, out var unknown))
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                    $"Unknown target(s): {string.Join(", ", unknown)}", "invalid_request_error");
            }

            var report = await Task.Run(() => ClientConfigWriter.Uninstall(targets))
                .ConfigureAwait(false);

            foreach (var line in report.Lines)
            {
                log.Write(line);
            }

            return Results.Json(new
            {
                ok = report.Ok,
                restored = report.Restored,
                failed = report.Failed,
                output = report.Lines,
            });
        });

        api.MapGet("/client-state", () =>
        {
            var state = ClientInstallState.Open();
            return Results.Json(new
            {
                canRestore = state.HasRecordFor(
                    ClientConfigWriter.AllTargets.Select(ClientConfigWriter.Slug)),
                targets = ClientConfigWriter.AllTargets.ToDictionary(
                    ClientConfigWriter.Slug,
                    t => state.HasRecordFor(new[] { ClientConfigWriter.Slug(t) })),
                statePath = state.Root,
            });
        });

        api.MapPost("/launch-agent", async (LaunchRequest request, ModelFleet fleet,
            EndpointResolver resolver, UserSettings settings, ActivityLog log) =>
        {
            if (!AgentLauncher.TryParse(request.Agent, out var agent))
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
                    $"Unknown agent `{request.Agent}`", "invalid_request_error");
            }

            var folder = Environment.ExpandEnvironmentVariables(request.Folder ?? string.Empty);
            if (!Directory.Exists(folder))
            {
                return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, "folder",
                    "invalid_request_error", new { path = folder });
            }

            var availability = AgentLauncher.Availability(agent);
            if (!availability.Installed)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.FailedDependency, "missing",
                    "missing_agent", new { agent = AgentLauncher.Slug(agent) });
            }

            var (resolved, failure) = await ContextAsync(resolver, "local", CancellationToken.None)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            var context = resolved!;

            // The model is one the configured server knows: a fleet alias against the
            // gateway, or a model id Unsloth's server lists in direct mode.
            var requested = string.IsNullOrWhiteSpace(request.Alias)
                ? context.DefaultAlias
                : request.Alias!.Trim();
            var model = context.Models.FirstOrDefault(m =>
                            string.Equals(m.Alias, requested, StringComparison.OrdinalIgnoreCase))
                        ?? context.Models.FirstOrDefault(m =>
                            context.Direct &&
                            string.Equals(m.Repo, requested, StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                return NotFound(requested);
            }

            var alias = model.Alias;

            // Codex resolves `--profile <alias>` from ~/.codex/<alias>.config.toml, so the
            // agent's own configuration has to exist before the terminal opens. The
            // installers are idempotent, so this is safe to repeat.
            var report = await Task.Run(() => ClientConfigWriter.Install(
                context,
                new[]
                {
                    agent switch
                    {
                        CodingAgent.Codex => ClientTarget.Codex,
                        CodingAgent.Qwen => ClientTarget.Qwen,
                        _ => ClientTarget.Claude,
                    },
                },
                new InstallOptions(settings.Snapshot().MakeDefaultProvider,
                    PersistEnvironment: true,
                    FullToolAccess: settings.Snapshot().FullToolAccess)))
                .ConfigureAwait(false);

            foreach (var line in report.Lines)
            {
                log.Write(line);
            }

            if (!report.Ok)
            {
                return GatewayEndpoints.Problem(HttpStatusCode.InternalServerError,
                    $"Could not write the {AgentLauncher.Slug(agent)} configuration.", "install_error");
            }

            settings.Update(s =>
            {
                s.ProjectFolder = folder;
                s.SelectedAlias = alias;
                s.LastAgent = AgentLauncher.Slug(agent);
            });

            var result = await Task.Run(() => AgentLauncher.Launch(
                agent, alias, folder,
                AgentLauncher.Environment(agent, context, alias),
                AgentLauncher.CommandLine(agent, alias, context), log)).ConfigureAwait(false);

            if (!result.Started)
            {
                return Results.Json(new { ok = false, command = result.ScriptPath });
            }

            log.Write($"launched {AgentLauncher.Slug(agent)} with {alias} in {folder}");
            return Results.Json(new
            {
                ok = true,
                agent = AgentLauncher.Slug(agent),
                alias,
                folder,
            });
        });

        api.MapPost("/shutdown", (ShutdownSignal shutdown) =>
        {
            shutdown.Request();
            return Results.Json(new { ok = true });
        });

        return app;
    }

    // ------------------------------------------------------------- helpers #
    /// <summary>
    /// The settings for whichever server is configured, or a 503 explaining why
    /// that server cannot be configured against right now.
    /// </summary>
    private static async Task<(ClientContext? Context, IResult? Failure)> ContextAsync(
        EndpointResolver resolver, string? scope, CancellationToken token)
    {
        try
        {
            return (await resolver.ContextAsync(scope, token).ConfigureAwait(false), null);
        }
        catch (EndpointUnavailableException ex)
        {
            return (null, GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                "endpoint_unavailable"));
        }
    }

    public static bool TryTargets(List<string>? requested, out List<ClientTarget> targets,
        out List<string> unknown)
    {
        targets = new List<ClientTarget>();
        unknown = new List<string>();

        if (requested is null || requested.Count == 0)
        {
            targets = ClientConfigWriter.AllTargets.ToList();
            return true;
        }

        foreach (var value in requested)
        {
            if (ClientConfigWriter.TryParseTarget(value, out var parsed))
            {
                targets.Add(parsed);
            }
            else
            {
                unknown.Add(value);
            }
        }

        return unknown.Count == 0;
    }

    /// <summary>Start a load or unload on Unsloth's server and report the operation.</summary>
    private static async Task<IResult> OperateAsync(UnslothModelRequest request, ModelFleet fleet,
        EndpointResolver resolver, bool load, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return GatewayEndpoints.Problem(HttpStatusCode.BadRequest, "model is required",
                "invalid_request_error");
        }

        UnslothOperation operation;
        try
        {
            operation = load
                ? await resolver.StartLoadAsync(request.Model, select: true, token).ConfigureAwait(false)
                : await resolver.StartUnloadAsync(request.Model, token).ConfigureAwait(false);
        }
        catch (EndpointUnavailableException ex)
        {
            return GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                "endpoint_unavailable");
        }
        catch (FleetFileException ex)
        {
            return GatewayEndpoints.Problem(HttpStatusCode.InternalServerError, ex.Message,
                "config_write_error");
        }

        if (request.Wait == true)
        {
            await resolver.AwaitOperationAsync(token).ConfigureAwait(false);
        }

        var result = operation.Result;
        if (result is { Ok: false })
        {
            return GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable,
                $"Unsloth's server did not {operation.Kind} `{operation.Spec}`: {result.Detail}",
                "unsloth_operation_error",
                new { operation = OperationPayload(operation) });
        }

        return Results.Json(new
        {
            ok = true,
            operation = OperationPayload(operation),
            endpoint = await EndpointPayload(fleet, resolver, refresh: !operation.InFlight, token)
                .ConfigureAwait(false),
        });
    }

    /// <summary>Never echo a secret in full; enough to recognise which key it is.</summary>
    private static string KeyHint(string key) => key.Length > 12 ? key[..12] + "…" : string.Empty;

    private static object UnslothModelPayload(UnslothModel model) => new
    {
        id = model.Id,
        spec = model.Spec,
        quant = model.Quant,
        contextLength = model.ContextLength,
        loaded = model.Loaded,
    };

    private static object? OperationPayload(UnslothOperation? operation)
    {
        if (operation is null)
        {
            return null;
        }

        var result = operation.Result;
        return new
        {
            kind = operation.Kind,
            model = operation.Spec,
            inFlight = operation.InFlight,
            startedAt = operation.StartedAt.ToUnixTimeMilliseconds(),
            ok = result?.Ok,
            status = result?.Status,
            detail = result?.Detail,
            contextLength = result?.ContextLength,
        };
    }

    /// <summary>Which server is targeted, and whether it can actually be reached.</summary>
    public static async Task<object> EndpointPayload(ModelFleet fleet, EndpointResolver resolver,
        bool refresh, CancellationToken token)
    {
        var status = await resolver.UnslothStatusAsync(refresh, token).ConfigureAwait(false);
        var key = resolver.ResolvedKey(status);
        var effective = resolver.EffectiveModel(status);
        var operation = resolver.Operation;
        var progress = operation is { InFlight: true, Kind: "load" }
            ? await resolver.LoadProgressAsync(token).ConfigureAwait(false)
            : UnslothLoadProgress.None;

        return new
        {
            source = fleet.Gateway.EndpointSource.ToString().ToLowerInvariant(),
            gateway = new
            {
                baseUrl = ClientContext.DefaultBaseUrl(fleet.Options),
                models = fleet.Options.Models.Count,
            },
            unsloth = new
            {
                baseUrl = status.BaseUrl,
                openAiBaseUrl = UnslothServer.OpenAiBase(status.BaseUrl),
                configuredUrl = fleet.Gateway.UnslothServerUrl,
                local = resolver.UnslothIsLocal,
                serverName = status.ServerName,
                serverVersion = status.ServerVersion,
                reachable = status.Reachable,
                authorised = status.Authorised,
                haveKey = status.HaveKey,
                keySource = status.KeySource,
                keyHint = KeyHint(key),
                keyPinned = fleet.Gateway.UnslothApiKey.Length > 0,
                detail = status.Detail,
                cachedKeyPath = UnslothServer.CachedKeyPath(),
                selectedModel = fleet.Gateway.UnslothModel,
                // The model the agents are actually configured with right now.
                effectiveModel = effective?.Spec ?? string.Empty,
                effectiveLoaded = effective?.Loaded ?? false,
                runtime = new
                {
                    activeModel = status.Runtime.ActiveSpec,
                    loading = status.Runtime.Loading,
                    loaded = status.Runtime.Loaded,
                    contextLength = status.Runtime.ContextLength,
                    maxContextLength = status.Runtime.MaxContextLength,
                    supportsTools = status.Runtime.SupportsTools,
                    supportsReasoning = status.Runtime.SupportsReasoning,
                },
                models = status.Models.Select(m => new
                {
                    id = m.Id,
                    spec = m.Spec,
                    quant = m.Quant,
                    contextLength = m.ContextLength,
                    loaded = m.Loaded,
                    loading = status.IsLoading(m.Spec),
                    selected = m.IsNamed(fleet.Gateway.UnslothModel),
                }).ToList(),
                operation = OperationPayload(operation),
                loadProgress = new
                {
                    phase = progress.Phase,
                    fraction = progress.Fraction,
                    bytesLoaded = progress.BytesLoaded,
                    bytesTotal = progress.BytesTotal,
                },
                startCommand = string.Join(' ', new[] { fleet.Gateway.UnslothBin }
                    .Concat(UnslothServer.StartCommand(
                        effective?.Spec ?? status.Models.FirstOrDefault()?.Spec ?? "unsloth/<repo>-GGUF",
                        status.BaseUrl))),
            },
        };
    }

    public static object NetworkPayload(ModelFleet fleet)
    {
        var map = NetworkInfo.EndpointUrls(fleet.Gateway.Host, fleet.Gateway.Port);
        return new
        {
            shared = map.Shared,
            host = map.Host,
            port = map.Port,
            localUrl = map.LocalUrl,
            lan = map.Lan.Select(e => new
            {
                address = e.Address,
                family = e.Family,
                url = e.Url,
                primary = e.Primary,
            }).ToList(),
            allowLan = fleet.Gateway.AllowLan,
            hostname = NetworkInfo.HostName,
            firewallCommand = FirewallHelper.Command(fleet.Gateway.Port),
            canOpenFirewall = FirewallHelper.IsWindows,
            idleTimeout = fleet.Gateway.IdleTimeout,
        };
    }

    /// <summary>Merge a partial runtime request onto what the model already has.</summary>
    public static RuntimeOptions Merge(RuntimeOptions current, RuntimeRequest request)
    {
        var merged = current.Clone();

        if (request.GpuMemoryMode is { Length: > 0 } mode)
        {
            merged.GpuMemoryMode = ParseOr(mode, merged.GpuMemoryMode, "gpuMemoryMode");
        }

        // A negative value clears the field back to automatic; -1 is "all" and is
        // spelt through the dedicated constants.
        if (request.GpuLayers is { } layers)
        {
            merged.GpuLayers = layers < RuntimeOptions.AllGpuLayers ? null : layers;
        }

        if (request.Fit is { Length: > 0 } fit)
        {
            merged.Fit = ParseOr(fit, merged.Fit, "fit");
        }

        if (request.FitContext is { } fitContext)
        {
            merged.FitContext = fitContext > 0 ? fitContext : null;
        }

        if (request.CpuMoeLayers is { } moe)
        {
            merged.CpuMoeLayers = moe < RuntimeOptions.AllCpuMoeLayers ? null : moe;
        }

        if (request.KvCacheType is not null)
        {
            merged.KvCacheType = request.KvCacheType.Trim().ToLowerInvariant();
        }

        if (request.FlashAttention is { Length: > 0 } flash)
        {
            merged.FlashAttention = ParseOr(flash, merged.FlashAttention, "flashAttention");
        }

        if (request.Threads is { } threads)
        {
            merged.Threads = threads > 0 ? threads : null;
        }

        merged.NoKvOffload = request.NoKvOffload ?? merged.NoKvOffload;
        merged.SwaFull = request.SwaFull ?? merged.SwaFull;
        merged.AllowOversize = request.AllowOversize ?? merged.AllowOversize;
        return merged;
    }

    private static TEnum ParseOr<TEnum>(string value, TEnum fallback, string label)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new FleetFileException(
            $"{label} must be {string.Join(" or ", Enum.GetNames<TEnum>().Select(n => $"`{n.ToLowerInvariant()}`"))}");
    }

    public static object HardwarePayload(ModelFleet fleet, bool refresh)
    {
        var hardware = HardwareProbe.Read(refresh);
        return new
        {
            totalRamBytes = hardware.TotalRamBytes,
            availableRamBytes = hardware.AvailableRamBytes,
            totalVramBytes = hardware.TotalVramBytes,
            freeVramBytes = hardware.FreeVramBytes,
            largestGpuBytes = hardware.LargestGpuBytes,
            usableBytes = hardware.TotalUsableBytes,
            unifiedMemory = hardware.UnifiedMemory,
            notes = hardware.Notes,
            gpus = hardware.Gpus.Select(g => new
            {
                index = g.Index,
                name = g.Name,
                vendor = g.Vendor,
                totalBytes = g.TotalBytes,
                freeBytes = g.FreeBytes,
            }).ToList(),
            models = fleet.Options.Models.Select(m => RuntimePayload(fleet, m.Alias)).ToList(),
        };
    }

    /// <summary>One model's runtime block plus what it is estimated to cost.</summary>
    public static object RuntimePayload(ModelFleet fleet, string alias)
    {
        var model = fleet.Options.Models.First(m => m.Alias == alias);
        var runtime = model.Runtime;
        var footprint = fleet.Footprint(model);

        return new
        {
            alias = model.Alias,
            contextLength = model.ContextLength,
            runtime = new
            {
                gpuMemoryMode = runtime.GpuMemoryMode.ToString().ToLowerInvariant(),
                gpuLayers = runtime.GpuLayers,
                fit = runtime.Fit.ToString().ToLowerInvariant(),
                fitContext = runtime.FitContext,
                cpuMoeLayers = runtime.CpuMoeLayers,
                kvCacheType = runtime.KvCacheType,
                flashAttention = runtime.FlashAttention.ToString().ToLowerInvariant(),
                threads = runtime.Threads,
                noKvOffload = runtime.NoKvOffload,
                swaFull = runtime.SwaFull,
                allowOversize = runtime.AllowOversize,
                isDefault = runtime.IsDefault,
            },
            footprint = new
            {
                verdict = footprint.Verdict.ToString().ToLowerInvariant(),
                weightBytes = footprint.WeightBytes,
                kvCacheBytes = footprint.KvCacheBytes,
                overheadBytes = footprint.OverheadBytes,
                totalBytes = footprint.TotalBytes,
                shortfallBytes = footprint.ShortfallBytes,
                estimatedCache = footprint.KvCacheEstimated,
            },
        };
    }

    public static List<object> FleetPayload(ModelFleet fleet) =>
        fleet.Options.Models.Select(m => (object)new
        {
            alias = m.Alias,
            repo = m.Repo,
            quant = m.Quant,
            port = m.Port,
            contextLength = m.ContextLength,
            isDefault = m.Alias == fleet.DefaultAlias,
        }).ToList();

    private static object LocalPayload(LibraryEntry entry) => new
    {
        repo = entry.Model.Repo,
        quant = entry.Model.Quant,
        spec = entry.Model.Spec,
        source = entry.Model.Source.ToString().ToLowerInvariant(),
        sizeBytes = entry.Model.SizeBytes,
        fileCount = entry.Model.FileCount,
        modified = entry.Model.Modified.ToUnixTimeMilliseconds(),
        path = entry.Model.Path,
        contextLength = entry.Model.ContextLength,
        architecture = entry.Model.Architecture,
        downloaded = true,
        inFleet = entry.InFleet,
    };

    private static async Task<IResult> LoadAsync(string alias, ModelFleet fleet, CancellationToken token)
    {
        var backend = fleet.Find(alias);
        if (backend is null)
        {
            return NotFound(alias);
        }

        try
        {
            await fleet.EnsureAsync(backend, token).ConfigureAwait(false);
        }
        catch (BackendStartException ex)
        {
            return GatewayEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                "backend_start_error");
        }

        return Results.Json(backend.Status());
    }

    private static bool TryApply(ModelFleet fleet, FleetOptions draft, ActivityLog log,
        out IResult? failure)
    {
        try
        {
            var validated = FleetFile.Validate(draft);
            FleetFile.Save(fleet.ConfigPath, validated);
            foreach (var alias in fleet.Apply(validated))
            {
                log.Write($"{alias}: stopped because its configuration changed");
            }

            failure = null;
            return true;
        }
        catch (FleetFileException ex)
        {
            failure = GatewayEndpoints.Problem(HttpStatusCode.BadRequest, ex.Message,
                "invalid_request_error");
            return false;
        }
    }

    private static IResult NotFound(string alias) =>
        GatewayEndpoints.Problem(HttpStatusCode.NotFound, $"Unknown model `{alias}`",
            "invalid_request_error");

    private static IResult NoModels() =>
        GatewayEndpoints.Problem(HttpStatusCode.BadRequest,
            "Add a model before generating client configuration.", "invalid_request_error");
}
