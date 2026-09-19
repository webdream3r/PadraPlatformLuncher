using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Padra.Core.Clients;
using Padra.Core.Configuration;
using Padra.Core.Networking;
using Padra.Core.Runtime;
using Xunit;

namespace Padra.Tests;

/// <summary>
/// A stand-in for Unsloth's own server (Unsloth Studio's backend): the
/// unauthenticated health probe, the authenticated model list, the runtime
/// status, the load/unload routes and the sign-in that mints a key — with the
/// same shapes and the same <c>Authorization: Bearer</c>-only rule the real one
/// enforces.
/// </summary>
public sealed class StubUnsloth : IAsyncDisposable
{
    public const string Key = "sk-unsloth-0123456789abcdef0123456789abcdef";

    public const string Username = "unsloth";

    public const string Password = "correct horse battery staple";

    public const string Session = "jwt-session-token";

    public const string Gemma = "unsloth/gemma-4-26B-A4B-it-GGUF";

    public const string Qwen = "unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF";

    /// <summary>An embedding model: listed, no <c>task</c>, and not conversational.</summary>
    public const string Embedding = "Qwen/Qwen3-Embedding-8B-GGUF";

    /// <summary>An image model: listed with a <c>task</c> that rules chat out.</summary>
    public const string Diffusion = "unsloth/FLUX.2-klein-4B-GGUF";

    private readonly WebApplication _app;
    private readonly object _lock = new();

    private StubUnsloth(WebApplication app, int port)
    {
        _app = app;
        Origin = $"http://127.0.0.1:{port}";
    }

    public string Origin { get; }

    public List<string> SeenAuthorization { get; } = new();

    /// <summary>The bodies of every <c>/v1/load</c> and <c>/v1/unload</c> received.</summary>
    public List<JsonObject> Operations { get; } = new();

    /// <summary>Keys created through sign-in, by name.</summary>
    public List<string> MintedKeys { get; } = new();

    /// <summary>The model the runtime currently has resident, as <c>id:quant</c>, or empty.</summary>
    public string Resident { get; private set; } = string.Empty;

    /// <summary>Delay before a load answers, to exercise the in-flight reporting.</summary>
    public TimeSpan LoadDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Delay before the liveness probe answers, to exercise its own budget.</summary>
    public TimeSpan HealthDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Delay before the model list answers: a cold catalog scan is slow.</summary>
    public TimeSpan ModelsDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Hold a response open, and give up quietly when the caller walks away.</summary>
    private static async Task Idle(TimeSpan delay, CancellationToken token)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The probe gave up on us; that is what the test is measuring.
        }
    }

    public static async Task<StubUnsloth> StartAsync(bool authenticate = true, bool loaded = true)
    {
        var port = StubBackend.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        var stub = new StubUnsloth(app, port);
        if (loaded)
        {
            stub.Resident = $"{Gemma}:UD-Q4_K_XL";
        }

        bool Authorised(HttpContext context, params string[] accepted)
        {
            var header = context.Request.Headers.Authorization.ToString();
            stub.SeenAuthorization.Add(header);
            return !authenticate || accepted.Any(a => header == $"Bearer {a}");
        }

        IResult Refused() => Results.Json(new { detail = "Invalid or expired API key" },
            statusCode: (int)HttpStatusCode.Unauthorized);

        app.MapGet("/api/health", async (HttpContext context) =>
        {
            await Idle(stub.HealthDelay, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new
            {
                status = "healthy",
                service = "Unsloth UI Backend",
                version = "2026.8.19",
            });
        });

        app.MapGet("/v1/models", async (HttpContext context) =>
        {
            if (!Authorised(context, Key))
            {
                return Refused();
            }

            await Idle(stub.ModelsDelay, context.RequestAborted).ConfigureAwait(false);

            lock (stub._lock)
            {
                return Results.Json(new
                {
                    @object = "list",
                    data = new object[]
                    {
                        new
                        {
                            id = Gemma,
                            @object = "model",
                            owned_by = "local",
                            quant = "UD-Q4_K_XL",
                            context_length = 131072,
                            loaded = stub.Resident == $"{Gemma}:UD-Q4_K_XL",
                        },
                        new
                        {
                            id = Qwen,
                            @object = "model",
                            owned_by = "local",
                            context_length = 262144,
                            loaded = stub.Resident == Qwen,
                        },
                        // The library, not the chat models: an agent can talk to
                        // neither of these and must never be offered them.
                        new
                        {
                            id = Embedding,
                            @object = "model",
                            owned_by = "local",
                            quant = "Q8_0",
                            context_length = 32768,
                            loaded = false,
                        },
                        new
                        {
                            id = Diffusion,
                            @object = "model",
                            owned_by = "local",
                            task = "text-to-image",
                            context_length = 0,
                            loaded = false,
                        },
                    },
                });
            }
        });

        app.MapGet("/v1/status", (HttpContext context) =>
        {
            if (!Authorised(context, Key))
            {
                return Refused();
            }

            lock (stub._lock)
            {
                var colon = stub.Resident.LastIndexOf(':');
                return Results.Json(new
                {
                    active_model = stub.Resident.Length > 0 ? stub.Resident : null,
                    model_identifier = colon > 0 ? stub.Resident[..colon] : stub.Resident,
                    gguf_variant = colon > 0 ? stub.Resident[(colon + 1)..] : null,
                    loaded = stub.Resident.Length > 0 ? new[] { stub.Resident } : Array.Empty<string>(),
                    loading = Array.Empty<string>(),
                    context_length = stub.Resident.Length > 0 ? 32768 : (int?)null,
                    supports_tools = true,
                });
            }
        });

        app.MapGet("/v1/load-progress", (HttpContext context) =>
            Authorised(context, Key)
                ? Results.Json(new { phase = "mmap", bytes_loaded = 512L, bytes_total = 1024L, fraction = 0.5 })
                : Refused());

        app.MapPost("/v1/load", async (HttpContext context) =>
        {
            if (!Authorised(context, Key))
            {
                return Refused();
            }

            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            stub.Operations.Add(body);
            var path = body["model_path"]!.GetValue<string>();
            var variant = body["gguf_variant"]?.GetValue<string>();

            if (path != Gemma && path != Qwen)
            {
                return Results.Json(new { detail = $"Model not found: {path}" },
                    statusCode: (int)HttpStatusCode.NotFound);
            }

            await Task.Delay(stub.LoadDelay);
            lock (stub._lock)
            {
                stub.Resident = string.IsNullOrEmpty(variant) ? path : $"{path}:{variant}";
            }

            // The real server pads a slow answer with whitespace before the JSON.
            return Results.Text("   \n" + new JsonObject
            {
                ["status"] = "loaded",
                ["model"] = path,
                ["display_name"] = path.Split('/')[^1],
                ["is_gguf"] = true,
                ["context_length"] = 32768,
                ["inference"] = new JsonObject { ["temperature"] = 0.7 },
            }.ToJsonString(), "application/json");
        });

        app.MapPost("/v1/unload", async (HttpContext context) =>
        {
            if (!Authorised(context, Key))
            {
                return Refused();
            }

            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            stub.Operations.Add(body);
            lock (stub._lock)
            {
                stub.Resident = string.Empty;
            }

            return Results.Json(new { status = "unloaded", model = body["model_path"]!.GetValue<string>() });
        });

        app.MapPost("/api/auth/login", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            if (body["username"]?.GetValue<string>() != Username ||
                body["password"]?.GetValue<string>() != Password)
            {
                return Results.Json(new { detail = "Incorrect username or password" },
                    statusCode: (int)HttpStatusCode.Unauthorized);
            }

            return Results.Json(new
            {
                access_token = Session,
                refresh_token = "refresh",
                token_type = "bearer",
                must_change_password = false,
            });
        });

        app.MapPost("/api/auth/api-keys", async (HttpContext context) =>
        {
            if (!Authorised(context, Session))
            {
                return Results.Json(new { detail = "Not authenticated" },
                    statusCode: (int)HttpStatusCode.Unauthorized);
            }

            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            var name = body["name"]!.GetValue<string>();
            stub.MintedKeys.Add(name);
            return Results.Json(new
            {
                key = Key,
                api_key = new
                {
                    id = stub.MintedKeys.Count,
                    name,
                    key_prefix = Key.Substring(KeyPrefixLength, 8),
                    created_at = "2026-08-22T00:00:00",
                    is_active = true,
                },
            });
        });

        await app.StartAsync();
        return stub;
    }

    private const int KeyPrefixLength = 11;   // "sk-unsloth-"

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Pointing the coding agents straight at Unsloth's own server, with nothing in
/// between.
/// </summary>
[Collection("environment")]
public sealed class UnslothDirectTests
{
    private static readonly InstallOptions Options = new(MakeDefault: true, PersistEnvironment: false);

    private static ClientInstallState State() =>
        ClientInstallState.OpenAt(Path.Combine(ClientConfigWriter.UserHome(), ".padra", "client-state"));

    // ================================================================ probe #
    [Fact]
    public async Task The_server_is_found_and_its_models_are_listed()
    {
        using var environment = new TestEnvironment();
        await using var server = await StubUnsloth.StartAsync();
        Environment.SetEnvironmentVariable("UNSLOTH_API_KEY", StubUnsloth.Key);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var status = await UnslothServer.ProbeAsync(http, server.Origin);

        Assert.True(status.Reachable);
        Assert.True(status.Authorised);
        Assert.True(status.Usable);
        Assert.Equal("UNSLOTH_API_KEY", status.KeySource);

        // The probe reports the server's whole library — chat models, an embedding
        // model and an image model. Filtering to what an agent can use happens later,
        // in EndpointResolver, so the panel can still show everything on that disk.
        Assert.Equal(4, status.Models.Count);
        Assert.Equal(2, status.Models.Count(m => m.IsChat));

        // The bare id is what /v1/models reports; the quant is appended to pin one.
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL", status.Models[0].Spec);
        Assert.Equal("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", status.Models[1].Spec);
        Assert.Equal(131072, status.Models[0].ContextLength);
        Assert.Single(status.LoadedModels);

        // The real server accepts Bearer and nothing else.
        Assert.All(server.SeenAuthorization,
            header => Assert.StartsWith("Bearer ", header, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_key_is_reported_as_a_key_problem_not_a_missing_server()
    {
        using var environment = new TestEnvironment();
        await using var server = await StubUnsloth.StartAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var status = await UnslothServer.ProbeAsync(http, server.Origin, "sk-unsloth-wrong");

        Assert.True(status.Reachable);
        Assert.False(status.Authorised);
        Assert.True(status.HaveKey);
        Assert.Contains("refused", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_server_that_is_not_running_is_reported_plainly()
    {
        using var environment = new TestEnvironment();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        var status = await UnslothServer.ProbeAsync(http,
            $"http://127.0.0.1:{StubBackend.FreePort()}");

        Assert.False(status.Reachable);
        Assert.False(status.Usable);
        Assert.Empty(status.Models);
    }

    [Fact]
    public void The_key_cached_by_unsloth_start_is_found()
    {
        using var environment = new TestEnvironment();
        var authDirectory = Path.Combine(environment.Home, ".unsloth", "studio", "auth");
        Directory.CreateDirectory(authDirectory);

        File.WriteAllText(Path.Combine(authDirectory, "agent_api_key.json"), $$"""
            {
              "servers": {
                "http://127.0.0.1:8888": {
                  "saved": [],
                  "minted": ["sk-unsloth-aaaa", "{{StubUnsloth.Key}}"]
                },
                "http://other:9999": { "minted": ["sk-unsloth-elsewhere"] }
              }
            }
            """);

        Assert.Equal(StubUnsloth.Key, UnslothServer.ReadCachedKey("http://127.0.0.1:8888"));
        Assert.Equal(StubUnsloth.Key, UnslothServer.ReadCachedKey("http://127.0.0.1:8888/v1/"));
        Assert.Null(UnslothServer.ReadCachedKey("http://127.0.0.1:1234"));

        var (key, source) = UnslothServer.ResolveKey("http://127.0.0.1:8888");
        Assert.Equal(StubUnsloth.Key, key);
        Assert.Equal("agent_api_key.json", source);

        // A key pinned in models.yaml wins over anything discovered.
        Assert.Equal("sk-unsloth-pinned",
            UnslothServer.ResolveKey("http://127.0.0.1:8888", "sk-unsloth-pinned").Key);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8888/", "http://127.0.0.1:8888")]
    [InlineData("http://127.0.0.1:8888/v1", "http://127.0.0.1:8888")]
    [InlineData("http://127.0.0.1:8888/v1/", "http://127.0.0.1:8888")]
    [InlineData("", "http://127.0.0.1:8888")]
    public void An_origin_is_stored_once_however_it_was_typed(string typed, string expected)
    {
        Assert.Equal(expected, UnslothServer.Normalise(typed));
        Assert.Equal(expected + "/v1", UnslothServer.OpenAiBase(typed));
    }

    // =============================================================== config #
    private static ClientContext Direct(string origin) => ClientContext.ForUnsloth(
        origin,
        StubUnsloth.Key,
        new[]
        {
            new UnslothModel("unsloth/gemma-4-26B-A4B-it-GGUF", "UD-Q4_K_XL", 131072, true),
            new UnslothModel("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", string.Empty, 262144, false),
        });

    [Fact]
    public void The_codex_config_matches_the_recipe_unsloth_publishes()
    {
        var context = Direct("http://127.0.0.1:8888");
        var block = ClientConfigWriter.CodexProviderBlock(context);

        Assert.StartsWith("[model_providers.unsloth_api]", block, StringComparison.Ordinal);
        Assert.Contains("base_url             = \"http://127.0.0.1:8888/v1\"", block,
            StringComparison.Ordinal);
        Assert.Contains("env_key              = \"UNSLOTH_STUDIO_AUTH_TOKEN\"", block,
            StringComparison.Ordinal);

        // Unsloth only streams /v1/responses, which is what Codex needs.
        Assert.Contains("wire_api             = \"responses\"", block, StringComparison.Ordinal);
        Assert.Contains("requires_openai_auth = false", block, StringComparison.Ordinal);

        // `codex --oss` picks the provider named here instead of Ollama or LM Studio.
        Assert.Contains("oss_provider   = \"unsloth_api\"",
            ClientConfigWriter.CodexDefaultsBlock(context), StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_code_is_given_the_bare_origin_and_a_cacheable_prompt()
    {
        var env = ClientConfigWriter.ClaudeEnvironment(Direct("http://127.0.0.1:8888"));

        // Claude Code appends /v1/messages itself.
        Assert.Equal("http://127.0.0.1:8888", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal(StubUnsloth.Key, env["ANTHROPIC_AUTH_TOKEN"]);

        // The attribution header changes every turn and destroys the prompt cache a
        // local model depends on.
        Assert.Equal("0", env["CLAUDE_CODE_ATTRIBUTION_HEADER"]);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL", env["ANTHROPIC_MODEL"]);
    }

    [Fact]
    public void One_codex_profile_covers_every_model_because_an_id_is_a_path()
    {
        using var environment = new TestEnvironment();
        var context = Direct("http://127.0.0.1:8888");

        var report = ClientConfigWriter.Install(context, new[] { ClientTarget.Codex }, Options,
            State());
        Assert.True(report.Ok);

        var codex = environment.Path_(".codex");
        var profiles = Directory.GetFiles(codex, "*.config.toml").Select(Path.GetFileName).ToList();

        // A model id contains a slash, so it cannot be a file name; Unsloth's own
        // tooling writes one profile named after the provider.
        Assert.Equal(new[] { "unsloth_api.config.toml" }, profiles);

        var profile = File.ReadAllText(Path.Combine(codex, "unsloth_api.config.toml"));
        Assert.Contains("model_provider       = \"unsloth_api\"", profile, StringComparison.Ordinal);
        Assert.Contains("model                = \"unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL\"",
            profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_from_the_gateway_prunes_the_profiles_it_left_behind()
    {
        using var environment = new TestEnvironment();
        var state = State();

        var fleet = FleetFile.Parse(string.Empty);
        fleet.Gateway.ApiKey = "sk-padra-test-key";
        FleetFile.AddModel(fleet, "unsloth/Qwen3.5-4B-GGUF", "UD-Q4_K_XL");
        ClientConfigWriter.Install(ClientContext.FromFleet(fleet, "http://127.0.0.1:8900/v1"),
            new[] { ClientTarget.Codex }, Options, state);

        Assert.True(File.Exists(environment.Path_(".codex", "qwen3.5-4b.config.toml")));

        ClientConfigWriter.Install(Direct("http://127.0.0.1:8888"),
            new[] { ClientTarget.Codex }, Options, state);

        Assert.False(File.Exists(environment.Path_(".codex", "qwen3.5-4b.config.toml")));
        Assert.True(File.Exists(environment.Path_(".codex", "unsloth_api.config.toml")));

        var config = File.ReadAllText(environment.Path_(".codex", "config.toml"));
        Assert.Contains("[model_providers.unsloth_api]", config, StringComparison.Ordinal);
        Assert.DoesNotContain("[model_providers.unsloth_gw]", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Qwen_and_vscode_are_pointed_at_the_unsloth_endpoint()
    {
        using var environment = new TestEnvironment();
        var userDirectory = environment.VsCodeUserDirectory();
        var context = Direct("http://127.0.0.1:8888");

        ClientConfigWriter.Install(context, new[] { ClientTarget.Qwen, ClientTarget.VsCode },
            Options, State());

        var qwen = JsonNode.Parse(
            File.ReadAllText(environment.Path_(".qwen", "settings.json")))!;
        var provider = qwen["modelProviders"]!["openai"]![0]!;
        Assert.Equal("http://127.0.0.1:8888/v1", provider["baseUrl"]!.GetValue<string>());
        Assert.Equal("UNSLOTH_STUDIO_AUTH_TOKEN", provider["envKey"]!.GetValue<string>());
        Assert.Equal(StubUnsloth.Key,
            qwen["env"]!["UNSLOTH_STUDIO_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal("openai", qwen["security"]!["auth"]!["selectedType"]!.GetValue<string>());

        var settings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(userDirectory, "settings.json")))!;
        Assert.Equal("api-key", settings["qwen-code.provider"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8888/v1",
            settings[ClientConfigWriter.TerminalEnvKey]!["OPENAI_BASE_URL"]!.GetValue<string>());
    }

    [Fact]
    public void Everything_the_bundle_writes_is_addressed_to_the_unsloth_server()
    {
        using var environment = new TestEnvironment();
        var files = ClientConfigWriter.GeneratedFiles(Direct("http://127.0.0.1:8888"));

        Assert.Contains("codex/unsloth_api.config.toml", files.Keys);
        Assert.DoesNotContain(files.Keys, name => name.Contains('/', StringComparison.Ordinal) &&
                                                  name.Count(c => c == '/') > 1);

        foreach (var (name, content) in files)
        {
            Assert.DoesNotContain("8900", content, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientConfigWriter.EnvKeyName, content, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(content), name);
        }
    }

    // ================================================================== api #
    [Fact]
    public async Task The_endpoint_can_be_switched_and_reports_what_it_found()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();

        var before = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/endpoint");
        Assert.Equal("gateway", before!["source"]!.GetValue<string>());

        var after = await gateway.Client.PostAsJsonAsync("/api/endpoint", new
        {
            source = "unsloth",
            serverUrl = server.Origin,
            apiKey = StubUnsloth.Key,
        });
        var payload = (await after.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("unsloth", payload["source"]!.GetValue<string>());

        var unsloth = payload["unsloth"]!;
        Assert.True(unsloth["reachable"]!.GetValue<bool>());
        Assert.True(unsloth["authorised"]!.GetValue<bool>());

        // The panel is shown the library as it is; only agent configuration is filtered.
        Assert.Equal(4, unsloth["models"]!.AsArray().Count);
        Assert.Equal(server.Origin + "/v1", unsloth["openAiBaseUrl"]!.GetValue<string>());

        // The key is never echoed in full.
        Assert.DoesNotContain(StubUnsloth.Key, payload.ToJsonString(), StringComparison.Ordinal);

        // And the choice survives a reload of models.yaml.
        Assert.Equal(EndpointSource.Unsloth,
            FleetFile.Load(gateway.ConfigPath).Gateway.EndpointSource);

        // Generated configuration now names the Unsloth server, not the gateway.
        var configs = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/client-configs");
        Assert.Equal("unsloth", configs!["source"]!.GetValue<string>());
        Assert.Equal(server.Origin + "/v1", configs["baseUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configuring_against_a_server_that_is_not_there_is_refused_with_a_reason()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var switched = await gateway.Client.PostAsJsonAsync("/api/endpoint", new
        {
            source = "unsloth",
            serverUrl = $"http://127.0.0.1:{StubBackend.FreePort()}",
        });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        using var install = await gateway.Client.PostAsJsonAsync("/api/install-configs",
            new { targets = new[] { "codex" }, persistEnvironment = false });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, install.StatusCode);
        var body = await install.Content.ReadAsStringAsync();
        Assert.Contains("unsloth run", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_source_is_rejected()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint",
            new { source = "ollama" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ===================================================== choosing a model #
    private static async Task SwitchToAsync(GatewayFixture gateway, StubUnsloth server,
        string? key = StubUnsloth.Key)
    {
        using var switched = await gateway.Client.PostAsJsonAsync("/api/endpoint", new
        {
            source = "unsloth",
            serverUrl = server.Origin,
            apiKey = key ?? string.Empty,
        });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
    }

    [Fact]
    public async Task The_resident_model_is_the_default_until_one_is_chosen()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server);

        var endpoint = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/endpoint");
        var unsloth = endpoint!["unsloth"]!;
        Assert.Equal("", unsloth["selectedModel"]!.GetValue<string>());
        Assert.Equal($"{StubUnsloth.Gemma}:UD-Q4_K_XL", unsloth["effectiveModel"]!.GetValue<string>());
        Assert.True(unsloth["effectiveLoaded"]!.GetValue<bool>());
        Assert.Equal($"{StubUnsloth.Gemma}:UD-Q4_K_XL",
            unsloth["runtime"]!["activeModel"]!.GetValue<string>());
        Assert.Equal("Unsloth UI Backend", unsloth["serverName"]!.GetValue<string>());

        // The resident model is written with the context the runtime actually has
        // (32 768 after fitting to VRAM), not the 131 072 its header advertises.
        var configs = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/client-configs");
        var profile = configs!["files"]!["codex/unsloth_api.config.toml"]!.GetValue<string>();
        Assert.Contains("model_context_window = 32768", profile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chosen_model_is_remembered_and_written_into_every_agent()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server);

        // The bare repository id names the model just as well as its spec.
        using var selected = await gateway.Client.PostAsJsonAsync("/api/endpoint/select",
            new { model = StubUnsloth.Qwen });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var payload = (await selected.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(StubUnsloth.Qwen, payload["model"]!["spec"]!.GetValue<string>());

        // The choice survives a restart.
        Assert.Equal(StubUnsloth.Qwen, FleetFile.Load(gateway.ConfigPath).Gateway.UnslothModel);

        var endpoint = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/endpoint");
        var unsloth = endpoint!["unsloth"]!;
        Assert.Equal(StubUnsloth.Qwen, unsloth["effectiveModel"]!.GetValue<string>());
        Assert.False(unsloth["effectiveLoaded"]!.GetValue<bool>());
        var row = unsloth["models"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == StubUnsloth.Qwen)!;
        Assert.True(row["selected"]!.GetValue<bool>());

        // Every generated file names that model, not the resident one.
        var configs = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/client-configs");
        var files = configs!["files"]!.AsObject();
        var claude = JsonNode.Parse(files["claude/settings.json"]!.GetValue<string>())!;
        Assert.Equal(StubUnsloth.Qwen, claude["env"]!["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Contains($"model          = \"{StubUnsloth.Qwen}\"",
            files["codex/config.toml"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains($"OPENAI_MODEL={StubUnsloth.Qwen}",
            files["qwen/.env"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_the_server_does_not_list_cannot_be_chosen()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server);

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/select",
            new { model = "unsloth/Nope-GGUF" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(string.Empty, FleetFile.Load(gateway.ConfigPath).Gateway.UnslothModel);

        using var blank = await gateway.Client.PostAsJsonAsync("/api/endpoint/select", new { model = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    // ================================================== loading through the api #
    [Fact]
    public async Task A_model_is_loaded_through_the_servers_own_api()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server);

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = StubUnsloth.Qwen, wait = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var operation = payload["operation"]!;
        Assert.Equal("load", operation["kind"]!.GetValue<string>());
        Assert.False(operation["inFlight"]!.GetValue<bool>());
        Assert.True(operation["ok"]!.GetValue<bool>());
        Assert.Equal(32768, operation["contextLength"]!.GetValue<int>());

        // The request the server saw is its own shape: a bare path plus the variant.
        var load = Assert.Single(server.Operations);
        Assert.Equal(StubUnsloth.Qwen, load["model_path"]!.GetValue<string>());
        Assert.Null(load["gguf_variant"]);
        Assert.Equal("auto", load["gpu_memory_mode"]!.GetValue<string>());

        // Loading also makes it the agents' model, and the server now reports it resident.
        Assert.Equal(StubUnsloth.Qwen, FleetFile.Load(gateway.ConfigPath).Gateway.UnslothModel);
        var unsloth = payload["endpoint"]!["unsloth"]!;
        Assert.True(unsloth["effectiveLoaded"]!.GetValue<bool>());
        Assert.Equal(StubUnsloth.Qwen, unsloth["runtime"]!["activeModel"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_quantised_variant_is_loaded_by_path_and_variant()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync(loaded: false);
        await SwitchToAsync(gateway, server);

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = $"{StubUnsloth.Gemma}:UD-Q4_K_XL", wait = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var load = Assert.Single(server.Operations);
        Assert.Equal(StubUnsloth.Gemma, load["model_path"]!.GetValue<string>());
        Assert.Equal("UD-Q4_K_XL", load["gguf_variant"]!.GetValue<string>());
        Assert.Equal($"{StubUnsloth.Gemma}:UD-Q4_K_XL", server.Resident);
    }

    [Fact]
    public async Task A_load_in_flight_is_reported_with_its_progress()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync(loaded: false);
        server.LoadDelay = TimeSpan.FromSeconds(2);
        await SwitchToAsync(gateway, server);

        using var started = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = StubUnsloth.Qwen });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var payload = (await started.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(payload["operation"]!["inFlight"]!.GetValue<bool>());

        var endpoint = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/endpoint");
        var unsloth = endpoint!["unsloth"]!;
        Assert.True(unsloth["operation"]!["inFlight"]!.GetValue<bool>());
        Assert.Equal("mmap", unsloth["loadProgress"]!["phase"]!.GetValue<string>());
        Assert.Equal(0.5, unsloth["loadProgress"]!["fraction"]!.GetValue<double>());

        // A second load is not stacked behind the first.
        using var again = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = StubUnsloth.Gemma });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(StubUnsloth.Qwen,
            (await again.Content.ReadFromJsonAsync<JsonObject>())!["operation"]!["model"]!.GetValue<string>());

        using var waited = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = StubUnsloth.Qwen, wait = true });
        Assert.Equal(HttpStatusCode.OK, waited.StatusCode);
        Assert.Single(server.Operations);
        Assert.Equal(StubUnsloth.Qwen, server.Resident);
    }

    [Fact]
    public async Task A_failed_load_is_reported_not_swallowed()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server);

        using var unknown = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = "unsloth/Nope-GGUF", wait = true });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
        Assert.Empty(server.Operations);

        // The server's own refusal comes back with its reason.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await UnslothServer.LoadAsync(http, server.Origin, StubUnsloth.Key,
            new UnslothLoadRequest("unsloth/Missing-GGUF"));
        Assert.False(result.Ok);
        Assert.Contains("Model not found", result.Detail, StringComparison.Ordinal);

        var refused = await UnslothServer.LoadAsync(http, server.Origin, "sk-unsloth-wrong",
            new UnslothLoadRequest(StubUnsloth.Qwen));
        Assert.False(refused.Ok);
        Assert.Contains("refused", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unload_frees_the_resident_model()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server);

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/unload",
            new { model = $"{StubUnsloth.Gemma}:UD-Q4_K_XL", wait = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var unload = Assert.Single(server.Operations);
        Assert.Equal(StubUnsloth.Gemma, unload["model_path"]!.GetValue<string>());
        Assert.Equal(string.Empty, server.Resident);

        var endpoint = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/endpoint");
        Assert.False(endpoint!["unsloth"]!["effectiveLoaded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Loading_needs_a_working_key()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server, key: "sk-unsloth-wrong");

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/load",
            new { model = StubUnsloth.Qwen, wait = true });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("refused", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(server.Operations);
    }

    // ============================================================ sign-in #
    [Fact]
    public async Task Signing_in_creates_a_key_and_pins_it()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server, key: null);

        var before = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/endpoint");
        Assert.False(before!["unsloth"]!["authorised"]!.GetValue<bool>());

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/key",
            new { username = StubUnsloth.Username, password = StubUnsloth.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(new[] { UnslothServer.MintedKeyName }, server.MintedKeys);

        // The key is saved for every later probe and install, never echoed in full.
        Assert.Equal(StubUnsloth.Key, FleetFile.Load(gateway.ConfigPath).Gateway.UnslothApiKey);
        Assert.DoesNotContain(StubUnsloth.Key, payload.ToJsonString(), StringComparison.Ordinal);
        Assert.True(payload["endpoint"]!["unsloth"]!["authorised"]!.GetValue<bool>());
        Assert.True(payload["endpoint"]!["unsloth"]!["keyPinned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_nothing_is_saved()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        await SwitchToAsync(gateway, server, key: null);

        using var response = await gateway.Client.PostAsJsonAsync("/api/endpoint/key",
            new { username = StubUnsloth.Username, password = "nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(server.MintedKeys);
        Assert.Equal(string.Empty, FleetFile.Load(gateway.ConfigPath).Gateway.UnslothApiKey);

        using var blank = await gateway.Client.PostAsJsonAsync("/api/endpoint/key",
            new { username = "", password = "" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blank.StatusCode);
    }

    // ======================================================= empty fleet #
    [Fact]
    public async Task Direct_mode_needs_no_model_in_the_fleet()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();
        foreach (var alias in new[] { "demo", "other" })
        {
            Assert.Equal(HttpStatusCode.OK, (await gateway.Client.DeleteAsync($"/api/models/{alias}")).StatusCode);
        }

        await SwitchToAsync(gateway, server);

        var result = await (await gateway.Client.PostAsJsonAsync("/api/install-configs",
                new { targets = new[] { "claude" }, scope = "local", persistEnvironment = false }))
            .Content.ReadFromJsonAsync<JsonObject>();

        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal(server.Origin + "/v1", result["baseUrl"]!.GetValue<string>());

        var home = Environment.GetEnvironmentVariable("PADRA_HOME")!;
        var settings = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(home, ".claude", "settings.json")))!;
        Assert.Equal(server.Origin, settings["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal(StubUnsloth.Key, settings["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal($"{StubUnsloth.Gemma}:UD-Q4_K_XL", settings["env"]!["ANTHROPIC_MODEL"]!.GetValue<string>());

        // The launcher resolves the server's own model ids, not fleet aliases.
        using var launch = await gateway.Client.PostAsJsonAsync("/api/launch-agent",
            new { agent = "codex", alias = StubUnsloth.Qwen, folder = Path.GetTempPath() });
        Assert.Equal(HttpStatusCode.FailedDependency, launch.StatusCode);   // codex is not installed here
    }

    // ============================================================== launch #
    [Fact]
    public void The_launch_terminal_matches_the_direct_configuration()
    {
        var context = Direct("http://192.168.1.20:8888");
        var spec = $"{StubUnsloth.Gemma}:UD-Q4_K_XL";

        // Codex has one profile against Unsloth; the model rides on the command line.
        Assert.Equal(new[] { "codex", "--profile", "unsloth_api", "--model", spec },
            AgentLauncher.CommandLine(CodingAgent.Codex, spec, context));
        Assert.Equal(new[] { "codex", "--profile", "demo" },
            AgentLauncher.CommandLine(CodingAgent.Codex, "demo"));

        // And the key goes under the variable `env_key` in config.toml names.
        var env = AgentLauncher.Environment(CodingAgent.Codex, context, spec);
        Assert.Equal(StubUnsloth.Key, env[UnslothServer.CodexKeyVariable]);
        Assert.False(env.ContainsKey(ClientConfigWriter.EnvKeyName));
        Assert.Equal("http://192.168.1.20:8888/v1", env["OPENAI_BASE_URL"]);

        var claude = AgentLauncher.Environment(CodingAgent.Claude, context, spec);
        Assert.Equal("http://192.168.1.20:8888", claude["ANTHROPIC_BASE_URL"]);
        Assert.Equal(spec, claude["ANTHROPIC_MODEL"]);
    }

    [Fact]
    public void A_server_address_typed_without_a_scheme_is_still_a_server()
    {
        Assert.Equal("http://192.168.30.227:8888", UnslothServer.Normalise("192.168.30.227:8888"));
        Assert.Equal("http://192.168.30.227:8888", UnslothServer.Normalise("192.168.30.227:8888/v1/"));
        Assert.False(UnslothServer.IsLocalOrigin("http://192.168.30.227:8888"));
        Assert.True(UnslothServer.IsLocalOrigin("http://127.0.0.1:8888"));
        Assert.True(UnslothServer.IsLocalOrigin("http://localhost:8888"));
    }

    [Fact]
    public void The_model_list_accepts_every_shape_the_server_has_used()
    {
        var models = UnslothServer.ParseModels("""
            {"object":"list","data":[
              {"id":"unsloth/A-GGUF:UD-Q4_K_XL","loaded":true},
              {"id":"unsloth/B-GGUF","gguf_variant":"Q8_0","max_context_length":"65536"},
              {"id":"unsloth/A-GGUF","quant":"UD-Q4_K_XL","loaded":false},
              {"id":""},
              {"object":"model"}
            ]}
            """);

        Assert.Equal(2, models.Count);
        Assert.Equal("unsloth/A-GGUF", models[0].Id);
        Assert.Equal("UD-Q4_K_XL", models[0].Quant);
        Assert.True(models[0].Loaded);
        Assert.Equal("unsloth/B-GGUF:Q8_0", models[1].Spec);
        Assert.Equal(65536, models[1].ContextLength);
        Assert.True(models[0].IsNamed("unsloth/a-gguf"));
        Assert.True(models[0].IsNamed("unsloth/A-GGUF:UD-Q4_K_XL"));
        Assert.False(models[0].IsNamed("unsloth/B-GGUF"));

        var runtime = UnslothServer.ParseRuntime("""
            {"active_model":"A-GGUF","model_identifier":"unsloth/A-GGUF","gguf_variant":"UD-Q4_K_XL",
             "loaded":["unsloth/A-GGUF"],"loading":["unsloth/B-GGUF"],"context_length":8192,
             "supports_tools":true}
            """);
        Assert.Equal("unsloth/A-GGUF:UD-Q4_K_XL", runtime.ActiveSpec);
        Assert.True(runtime.IsLoading);
        Assert.True(runtime.SupportsTools);
        Assert.Equal(8192, runtime.ContextLength);
    }

    // ------------------------------------------------- proxy and probe budgets #

    /// <summary>
    /// The regression this guards: a proxy meant for the open internet
    /// (<c>HTTP_PROXY</c> with a <c>NO_PROXY</c> that lists only loopback) swallowed
    /// every probe of an Unsloth server on the LAN, and the panel reported a healthy
    /// server as unreachable. Nothing that talks to a model server may be proxied.
    /// </summary>
    [Fact]
    public void Model_server_clients_never_go_through_a_proxy()
    {
        using var handler = DirectHttp.CreateHandler();
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.True(handler.AllowAutoRedirect);

        using var forwarder = DirectHttp.CreateHandler(allowAutoRedirect: false);
        Assert.False(forwarder.UseProxy);
        Assert.False(forwarder.AllowAutoRedirect);

        using var client = DirectHttp.CreateClient(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }

    /// <summary>Liveness is the quick question; the catalog is allowed to be slow.</summary>
    [Fact]
    public void Liveness_is_bounded_more_tightly_than_the_rest_of_the_probe()
    {
        Assert.True(UnslothServer.HealthTimeout < UnslothServer.ProbeTimeout);
        Assert.True(UnslothServer.HealthTimeout > TimeSpan.Zero);
    }

    /// <summary>
    /// <c>/v1/models</c> walks the server's model library and takes seconds on a cold
    /// cache. That must not be charged to the liveness budget, or a server that is
    /// answering perfectly well is reported unreachable and unauthorised.
    /// </summary>
    [Fact]
    public async Task A_slow_catalog_does_not_make_a_live_server_look_unreachable()
    {
        await using var stub = await StubUnsloth.StartAsync();
        stub.ModelsDelay = TimeSpan.FromMilliseconds(600);

        using var http = DirectHttp.CreateClient(TimeSpan.FromSeconds(20));
        var status = await UnslothServer.ProbeAsync(http, stub.Origin, StubUnsloth.Key,
            healthTimeout: TimeSpan.FromMilliseconds(200));

        Assert.True(status.Reachable);
        Assert.True(status.Authorised);
        Assert.NotEmpty(status.Models);
        Assert.Equal(string.Empty, status.Detail);
    }

    /// <summary>A server that will not answer liveness is unreachable, and says why.</summary>
    [Fact]
    public async Task A_server_that_never_answers_liveness_is_reported_unreachable()
    {
        await using var stub = await StubUnsloth.StartAsync();
        stub.HealthDelay = TimeSpan.FromSeconds(30);

        using var http = DirectHttp.CreateClient(TimeSpan.FromSeconds(60));
        var status = await UnslothServer.ProbeAsync(http, stub.Origin, StubUnsloth.Key,
            healthTimeout: TimeSpan.FromMilliseconds(200));

        Assert.False(status.Reachable);
        Assert.False(status.Authorised);
        Assert.Contains("did not answer within", status.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unsloth lists its whole library. Image, speech and embedding models answer
    /// nothing a coding agent sends, so they must not reach an agent's model list —
    /// a model the user can select and cannot use is worse than one absent.
    /// </summary>
    [Fact]
    public void Only_chat_models_are_offered_to_an_agent()
    {
        var models = UnslothServer.ParseModels("""
            {"data":[
              {"id":"unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF","quant":"UD-Q4_K_XL","loaded":true},
              {"id":"Qwen/Qwen3-Embedding-8B-GGUF","quant":"Q8_0","loaded":false},
              {"id":"BAAI/bge-reranker-v2-GGUF","quant":"Q8_0","loaded":false},
              {"id":"unsloth/FLUX.2-klein-4B-GGUF","task":"text-to-image","loaded":false},
              {"id":"qwen3-asr-1.7b","task":"automatic-speech-recognition","loaded":false}
            ]}
            """);

        Assert.Equal(5, models.Count);
        Assert.Single(models, m => m.IsChat);
        Assert.Equal("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", models.First(m => m.IsChat).Id);
        Assert.Equal("text-to-image", models.First(m => m.Id.Contains("FLUX")).Task);
    }

    /// <summary>
    /// The same rule end to end: the panel may show the whole library, but the file
    /// written for Continue names only models it can hold a conversation with.
    /// </summary>
    [Fact]
    public async Task An_agent_configuration_never_names_a_non_chat_model()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        await using var server = await StubUnsloth.StartAsync();

        var switched = await gateway.Client.PostAsJsonAsync("/api/endpoint", new
        {
            source = "unsloth",
            serverUrl = server.Origin,
            apiKey = StubUnsloth.Key,
        });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        var configs = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/client-configs");
        var written = configs!["files"]!["continue/config.yaml"]!.GetValue<string>();

        Assert.Contains(StubUnsloth.Gemma, written, StringComparison.Ordinal);
        Assert.Contains(StubUnsloth.Qwen, written, StringComparison.Ordinal);
        Assert.DoesNotContain(StubUnsloth.Embedding, written, StringComparison.Ordinal);
        Assert.DoesNotContain(StubUnsloth.Diffusion, written, StringComparison.Ordinal);
        Assert.Contains($"apiBase: {server.Origin}/v1", written, StringComparison.Ordinal);
    }
}
