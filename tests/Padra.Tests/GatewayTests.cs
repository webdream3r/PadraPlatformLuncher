using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Padra.Core.Configuration;
using Padra.Core.Runtime;
using Xunit;

namespace Padra.Tests;

[Collection("environment")]
public sealed class GatewayTests
{
    // -------------------------------------------------------------- probes #
    [Fact]
    public async Task Healthz_needs_no_key()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var body = await gateway.Client.GetFromJsonAsync<JsonObject>("/healthz");

        Assert.Equal("ok", body!["status"]!.GetValue<string>());
        Assert.Equal("demo", body["default_model"]!.GetValue<string>());
        Assert.Equal(new[] { "demo", "other" },
            body["models"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Head_root_answers_the_claude_code_probe()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/");
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_model_list_requires_the_key()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await gateway.Client.GetAsync("/v1/models")).StatusCode);

        var body = await gateway.Authorised.GetFromJsonAsync<JsonObject>("/v1/models");
        var data = body!["data"]!.AsArray();
        Assert.Equal(new[] { "demo", "other" }, data.Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal(4096, data[0]!["context_length"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_wrong_key_is_refused_and_x_api_key_is_accepted()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        wrong.Headers.Add("Authorization", "Bearer nope");
        using var refused = await gateway.Client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("authentication_error", problem!["error"]!["type"]!.GetValue<string>());

        using var header = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        header.Headers.Add("x-api-key", GatewayFixture.Key);
        Assert.Equal(HttpStatusCode.OK, (await gateway.Client.SendAsync(header)).StatusCode);
    }

    // ------------------------------------------------------------ proxying #
    [Fact]
    public async Task Chat_completions_are_proxied_and_the_model_is_rewritten()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Authorised.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "demo",
            messages = new[] { new { role = "user", content = "hi" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("demo", response.Headers.GetValues("X-Unsloth-Backend").Single());
        Assert.Contains("[DONE]", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var received = gateway.Backend.Received[^1];
        Assert.Equal("chat/completions", received.Path);
        Assert.Equal(gateway.Backend.ServedId, received.Body["model"]!.GetValue<string>());
        Assert.Equal("hi", received.Body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_headers_reach_the_backend()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(
                """{"model":"demo","max_tokens":16,"messages":[{"role":"user","content":"."}]}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {GatewayFixture.Key}");
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "fine-grained-tool-streaming-2025-05-14");

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = gateway.Backend.Received[^1];
        Assert.Equal("messages", received.Path);
        Assert.Equal("2023-06-01", received.Headers["anthropic-version"]);
        Assert.Equal("fine-grained-tool-streaming-2025-05-14", received.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task Count_tokens_gets_a_model_injected()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        await gateway.Authorised.PostAsJsonAsync("/v1/messages/count_tokens", new
        {
            messages = new[] { new { role = "user", content = "." } },
        });

        Assert.Equal(gateway.Backend.ServedId,
            gateway.Backend.Received[^1].Body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Query_parameters_survive_the_proxy()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        await gateway.Authorised.PostAsJsonAsync("/v1/responses?beta=true&trace=1", new { model = "demo" });

        Assert.Equal("?beta=true&trace=1", gateway.Backend.Received[^1].Query);
    }

    [Fact]
    public async Task Unknown_models_fall_back_to_the_default()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Authorised.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "claude-sonnet-4-6",
            messages = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("demo", response.Headers.GetValues("X-Unsloth-Backend").Single());
    }

    [Fact]
    public async Task Unknown_models_can_be_rejected_instead()
    {
        await using var gateway = await GatewayFixture.StartAsync(
            options => options.Gateway.UnknownModel = UnknownModelBehaviour.Error);

        using var response = await gateway.Authorised.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "ghost",
            messages = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(new[] { "demo", "other" },
            problem!["error"]!["available"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Repo_and_spec_names_resolve_to_the_alias()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        foreach (var name in new[]
                 {
                     "unsloth/Demo-GGUF", "unsloth/Demo-GGUF:Q4_K_M", "Demo-GGUF", "openai:demo",
                 })
        {
            using var response = await gateway.Authorised.PostAsJsonAsync("/v1/chat/completions",
                new { model = name, messages = Array.Empty<object>() });
            Assert.Equal("demo", response.Headers.GetValues("X-Unsloth-Backend").Single());
        }
    }

    [Fact]
    public async Task A_backend_that_cannot_start_is_reported_as_unavailable()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Authorised.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "other",
            messages = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("backend_start_error", problem!["error"]!["type"]!.GetValue<string>());
        Assert.Contains("Install the Unsloth CLI",
            problem["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- admin #
    [Fact]
    public async Task Load_unload_and_status()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var loaded = await (await gateway.Authorised.PostAsync("/admin/load/demo", null))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(loaded!["running"]!.GetValue<bool>());
        Assert.True(loaded["external"]!.GetValue<bool>());

        var status = await gateway.Authorised.GetFromJsonAsync<JsonObject>("/admin/status");
        Assert.Equal("exclusive", status!["mode"]!.GetValue<string>());
        Assert.Equal(new[] { "demo", "other" },
            status["backends"]!.AsArray().Select(n => n!["Alias"]!.GetValue<string>()));

        var unloaded = await (await gateway.Authorised.PostAsync("/admin/unload/demo", null))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(unloaded!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_mode_switch_is_persisted()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var applied = await gateway.Authorised.PostAsync("/admin/mode/concurrent", null);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(FleetMode.Concurrent, FleetFile.Load(gateway.ConfigPath).Gateway.Mode);

        using var rejected = await gateway.Authorised.PostAsync("/admin/mode/sideways", null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Reload_picks_up_an_edited_file()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var options = FleetFile.Load(gateway.ConfigPath);
        FleetFile.RemoveModel(options, "other");
        FleetFile.Save(gateway.ConfigPath, options);

        var body = await (await gateway.Authorised.PostAsync("/admin/reload", null))
            .Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(new[] { "demo" }, body!["models"]!.AsArray().Select(n => n!.GetValue<string>()));
        var health = await gateway.Client.GetFromJsonAsync<JsonObject>("/healthz");
        Assert.Equal(new[] { "demo" }, health!["models"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task A_broken_file_does_not_break_a_running_gateway()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        await File.WriteAllTextAsync(gateway.ConfigPath, "gateway: [1,2,3]\n");

        using var response = await gateway.Authorised.PostAsync("/admin/reload", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var health = await gateway.Client.GetFromJsonAsync<JsonObject>("/healthz");
        Assert.Equal(new[] { "demo", "other" },
            health!["models"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ------------------------------------------------------- without a key #
    [Fact]
    public async Task A_key_is_generated_when_the_file_has_none()
    {
        await using var gateway = await GatewayFixture.StartAsync(secondModel: false, apiKey: null);

        // Start-up always leaves a real key behind, so the endpoint is never open.
        var key = FleetFile.Load(gateway.ConfigPath).Gateway.ApiKey;
        Assert.StartsWith("sk-padra-", key, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await gateway.Client.GetAsync("/v1/models")).StatusCode);

        using var authorised = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        authorised.Headers.Add("Authorization", $"Bearer {key}");
        Assert.Equal(HttpStatusCode.OK, (await gateway.Client.SendAsync(authorised)).StatusCode);

        // The panel keeps working locally without the caller knowing the key.
        Assert.Equal(HttpStatusCode.OK, (await gateway.Client.GetAsync("/api/status")).StatusCode);
    }

    // ---------------------------------------------------------- unit level #
    [Fact]
    public void The_backend_command_covers_the_documented_flags()
    {
        var backend = new ModelBackend(
            new ModelOptions
            {
                Alias = "a",
                Repo = "unsloth/X-GGUF",
                Quant = "Q4_K_M",
                Port = 8801,
                ContextLength = 4096,
                Reasoning = "off",
                DisableTools = true,
                ExtraArgs = new List<string> { "--temp", "0.6" },
            },
            new GatewayOptions(), new ActivityLog());

        var command = backend.BuildArguments().ToList();

        Assert.Equal(new[] { "run", "--model", "unsloth/X-GGUF:Q4_K_M" }, command.Take(3));
        Assert.Contains("-H", command);
        Assert.Contains("-p", command);
        Assert.Contains("-c", command);
        Assert.Contains("--disable-tools", command);
        Assert.Equal("off", command[command.IndexOf("--reasoning") + 1]);
        Assert.Contains("--yes", command);
        Assert.Equal(new[] { "--temp", "0.6" }, command.TakeLast(2));
    }

    [Fact]
    public void Flags_are_not_duplicated_when_given_explicitly()
    {
        var backend = new ModelBackend(
            new ModelOptions
            {
                Alias = "a", Repo = "x", Port = 8801, DisableTools = true,
                ExtraArgs = new List<string> { "--disable-tools", "--yes" },
            },
            new GatewayOptions(), new ActivityLog());

        var command = backend.BuildArguments();

        Assert.Equal(1, command.Count(a => a == "--disable-tools"));
        Assert.Equal(1, command.Count(a => a == "--yes"));
    }

    [Fact]
    public async Task A_missing_unsloth_binary_produces_a_useful_error()
    {
        var backend = new ModelBackend(
            new ModelOptions { Alias = "a", Repo = "x", Port = StubBackend.FreePort() },
            new GatewayOptions { UnslothBin = "definitely-not-installed-binary", StartTimeout = 5 },
            new ActivityLog());

        var error = await Assert.ThrowsAsync<BackendStartException>(() => backend.StartAsync());
        Assert.Contains("Install the Unsloth CLI", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_orphan_registry_round_trips()
    {
        using var environment = new TestEnvironment();
        var registry = new OrphanRegistry(Path.Combine(environment.Work, "backends.json"),
            new ActivityLog());

        registry.Add("demo", 4242, 8801);
        Assert.Equal(4242, registry.Read()[0].Pid);

        registry.Remove(4242);
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void Auto_import_seeds_an_empty_fleet()
    {
        using var environment = new TestEnvironment();
        var cache = Environment.GetEnvironmentVariable("HF_HUB_CACHE")!;
        foreach (var (repo, name, size) in new[]
                 {
                     ("unsloth--A-GGUF", "A-Q4_K_M.gguf", 10),
                     ("unsloth--B-GGUF", "B-Q8_0.gguf", 20),
                 })
        {
            var snapshot = Path.Combine(cache, "models--" + repo, "snapshots", "s1");
            Directory.CreateDirectory(snapshot);
            File.WriteAllBytes(Path.Combine(snapshot, name), new byte[size]);
        }

        var options = FleetFile.Parse(string.Empty);
        Assert.True(ModelFleet.AutoImport(options, new ActivityLog()));
        Assert.Equal(new[] { "a", "b" }, options.Models.Select(m => m.Alias));
        Assert.Equal("a", options.Gateway.DefaultModel);
    }

    // ------------------------------------------- never fight a running server #
    [Fact]
    public async Task An_occupied_port_with_a_rejected_key_is_an_error_never_a_second_process()
    {
        // A server is already answering on the port, but it refuses the key. The
        // old behaviour was to spawn `unsloth run` on top of it, which fights
        // whatever is running — including a server the user started by hand.
        var port = StubBackend.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.MapGet("/v1/models", () => Results.Unauthorized());
        await app.StartAsync();

        try
        {
            var backend = new ModelBackend(
                new ModelOptions { Alias = "busy", Repo = "unsloth/X-GGUF", Port = port, ApiKey = "sk-wrong" },
                new GatewayOptions { StartTimeout = 5 },
                new ActivityLog());

            var error = await Assert.ThrowsAsync<BackendStartException>(() => backend.StartAsync());

            Assert.Contains("already in use", error.Message, StringComparison.Ordinal);
            Assert.Contains("left untouched", error.Message, StringComparison.Ordinal);
            Assert.False(backend.IsRunning);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void Requests_in_flight_are_counted_and_stamp_the_idle_clock()
    {
        var backend = new ModelBackend(
            new ModelOptions { Alias = "a", Repo = "unsloth/X-GGUF", Port = 8801 },
            new GatewayOptions(), new ActivityLog());

        Assert.Equal(0, backend.ActiveRequests);
        Assert.Null(backend.LastUsed);

        backend.BeginRequest();
        backend.BeginRequest();
        Assert.Equal(2, backend.ActiveRequests);

        backend.EndRequest();
        backend.EndRequest();
        Assert.Equal(0, backend.ActiveRequests);
        Assert.NotNull(backend.LastUsed);
    }

    [Fact]
    public void Only_the_process_recorded_at_that_moment_counts_as_the_orphan()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var started = new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero);

        // The real pid at its real start time is ours; the same pid claimed to have
        // been started two hours earlier is a recycled number or someone else's
        // server, and must never be killed.
        Assert.True(ProcessTree.StartedNear(current.Id, started));
        Assert.False(ProcessTree.StartedNear(current.Id, started - TimeSpan.FromHours(2)));
        Assert.False(ProcessTree.StartedNear(-1, DateTimeOffset.UtcNow));
        Assert.False(ProcessTree.StartedNear(0, DateTimeOffset.UtcNow));
    }
}
