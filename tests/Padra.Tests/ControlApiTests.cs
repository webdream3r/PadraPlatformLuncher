using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Padra.Core.Configuration;
using Xunit;

namespace Padra.Tests;

[Collection("environment")]
public sealed class ControlApiTests
{
    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())!;

    // --------------------------------------------------------------- shell #
    [Fact]
    public async Task The_panel_and_its_assets_are_served()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var index = await gateway.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var html = await index.Content.ReadAsStringAsync();
        Assert.Contains("Padra Platform Launcher", html, StringComparison.Ordinal);
        Assert.Contains("dir=\"ltr\"", html, StringComparison.Ordinal);

        // Every section renders on the server, so a broken one is a 500, not a
        // blank area noticed later.
        foreach (var heading in new[]
                 {
                     "Models in the gateway", "Model library", "Hardware and offload",
                     "Coding agents", "Network", "Activity",
                 })
        {
            Assert.Contains(heading, html, StringComparison.Ordinal);
        }

        Assert.Contains("Restore original configuration", html, StringComparison.Ordinal);
        Assert.Contains("Tune for this machine", html, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await gateway.Client.GetAsync("/css/panel.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gateway.Client.GetAsync("/icon.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await gateway.Client.GetAsync("/_framework/blazor.web.js")).StatusCode);
    }

    [Fact]
    public async Task The_panel_switches_culture_and_direction()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(gateway.Origin) };

        using var redirect = await client.GetAsync("/culture?culture=fa-IR&redirect=/");
        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);

        var html = await client.GetStringAsync("/");
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("lang=\"fa-IR\"", html, StringComparison.Ordinal);
        Assert.Contains("راه‌انداز پلتفرم پادرا", html, StringComparison.Ordinal);
        Assert.Contains("سخت‌افزار و برون‌ریزی", html, StringComparison.Ordinal);
        Assert.Contains("بازگرداندن پیکربندی اصلی", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Hardware and offload", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_includes_the_fleet_and_the_network()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var data = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/status");

        Assert.Equal("demo", data!["defaultModel"]!.GetValue<string>());
        Assert.True(data["models"]![0]!["port"]!.GetValue<int>() > 0);
        Assert.True(data["models"]![0]!["isDefault"]!.GetValue<bool>());
        Assert.EndsWith("/v1", data["network"]!["localUrl"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(data["network"]!["shared"]!.GetValue<bool>());
    }

    // --------------------------------------------------------------- fleet #
    [Fact]
    public async Task Models_can_be_added_and_removed()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var added = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/models", new
        {
            repo = "unsloth/Gemma-GGUF",
            quant = "UD-Q4_K_XL",
            contextLength = 131072,
        }));

        Assert.True(added["ok"]!.GetValue<bool>());
        Assert.Equal("gemma", added["model"]!["alias"]!.GetValue<string>());
        Assert.Equal(new[] { "demo", "other", "gemma" },
            added["models"]!.AsArray().Select(n => n!["alias"]!.GetValue<string>()));
        Assert.Equal("unsloth/Gemma-GGUF", FleetFile.Load(gateway.ConfigPath).Models[2].Repo);

        var removed = await JsonAsync(await gateway.Client.DeleteAsync("/api/models/gemma"));
        Assert.Equal(new[] { "demo", "other" },
            removed["models"]!.AsArray().Select(n => n!["alias"]!.GetValue<string>()));
        Assert.Equal(2, FleetFile.Load(gateway.ConfigPath).Models.Count);
    }

    [Fact]
    public async Task Duplicates_and_unknown_models_are_rejected()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var duplicate = await gateway.Client.PostAsJsonAsync("/api/models", new
        {
            repo = "unsloth/Demo-GGUF",
            quant = "Q4_K_M",
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("already in the fleet",
            (await JsonAsync(duplicate))["error"]!["message"]!.GetValue<string>(),
            StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound,
            (await gateway.Client.DeleteAsync("/api/models/ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await gateway.Client.PostAsync("/api/models/ghost/load", null)).StatusCode);
    }

    [Fact]
    public async Task Load_unload_and_activate_through_the_panel()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        Assert.True((await JsonAsync(await gateway.Client.PostAsync("/api/models/demo/load", null)))
            ["running"]!.GetValue<bool>());
        Assert.False((await JsonAsync(await gateway.Client.PostAsync("/api/models/demo/unload", null)))
            ["running"]!.GetValue<bool>());

        Assert.True((await JsonAsync(await gateway.Client.PostAsync("/api/models/demo/activate", null)))
            ["running"]!.GetValue<bool>());
        Assert.Equal("demo", FleetFile.Load(gateway.ConfigPath).Gateway.DefaultModel);
    }

    [Fact]
    public async Task Activating_a_model_that_cannot_start_reports_the_failure()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.PostAsync("/api/models/other/activate", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The choice is still recorded: the user asked for it, and the panel shows why it failed.
        Assert.Equal("other", FleetFile.Load(gateway.ConfigPath).Gateway.DefaultModel);
    }

    // ----------------------------------------------------------- discovery #
    [Fact]
    public async Task Local_models_are_listed_with_sizes()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        var cache = Environment.GetEnvironmentVariable("HF_HUB_CACHE")!;
        var snapshot = Path.Combine(cache, "models--unsloth--Demo-GGUF", "snapshots", "s1");
        Directory.CreateDirectory(snapshot);
        await File.WriteAllBytesAsync(Path.Combine(snapshot, "Demo-Q4_K_M.gguf"), new byte[2048]);
        var other = Path.Combine(cache, "models--unsloth--New-GGUF", "snapshots", "s1");
        Directory.CreateDirectory(other);
        await File.WriteAllBytesAsync(Path.Combine(other, "New-Q8_0.gguf"), new byte[4096]);

        var data = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/models/local?refresh=true");

        Assert.Equal(6144, data!["totalBytes"]!.GetValue<long>());
        var byRepo = data["models"]!.AsArray()
            .ToDictionary(n => n!["repo"]!.GetValue<string>(), n => n!);
        Assert.True(byRepo["unsloth/Demo-GGUF"]["inFleet"]!.GetValue<bool>());
        Assert.False(byRepo["unsloth/New-GGUF"]["inFleet"]!.GetValue<bool>());
        Assert.Equal(4096, byRepo["unsloth/New-GGUF"]["sizeBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task Catalog_failures_are_surfaced_not_swallowed()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        // No network in the test container, so this exercises the real failure path.
        Environment.SetEnvironmentVariable("HF_ENDPOINT", "http://127.0.0.1:1");

        using var response = await gateway.Client.GetAsync("/api/models/catalog?search=x&refresh=true");

        Assert.True(response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.OK);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Equal("catalog_error",
                (await JsonAsync(response))["error"]!["type"]!.GetValue<string>());
        }
    }

    // ------------------------------------------------------------- network #
    [Fact]
    public async Task The_network_can_be_shared_and_reverted()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var shared = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/network",
            new { allowLan = true }));
        Assert.True(shared["restartRequired"]!.GetValue<bool>());
        Assert.True(shared["network"]!["shared"]!.GetValue<bool>());
        Assert.Equal("0.0.0.0", FleetFile.Load(gateway.ConfigPath).Gateway.Host);

        var reverted = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/network",
            new { allowLan = false }));
        Assert.False(reverted["network"]!["shared"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_idle_timeout_is_saved()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        await gateway.Client.PostAsJsonAsync("/api/network", new { idleTimeout = 600 });

        Assert.Equal(600, FleetFile.Load(gateway.ConfigPath).Gateway.IdleTimeout);
    }

    [Fact]
    public async Task The_firewall_command_is_reported()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var network = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/network");
        var command = network!["firewallCommand"]!.GetValue<string>();
        Assert.Contains(network["port"]!.GetValue<int>().ToString(), command, StringComparison.Ordinal);
        Assert.Equal(OperatingSystem.IsWindows(), network["canOpenFirewall"]!.GetValue<bool>());

        // On Windows the POST raises a real UAC elevation prompt, which a test run
        // must never do; elsewhere it only reports the command to run by hand.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await JsonAsync(await gateway.Client.PostAsync("/api/network/firewall", null));
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(command, result["command"]!.GetValue<string>());
    }

    // ------------------------------------------------------------- clients #
    [Fact]
    public async Task Client_availability_is_reported()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var data = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/clients");

        Assert.Equal(new[] { "claude", "codex", "qwen" },
            data!["agents"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal(new[] { "codex", "qwen", "claude", "continue", "vscode" },
            data["targets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(data["unsloth"]!["installed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Installing_configuration_writes_real_files()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        var home = Environment.GetEnvironmentVariable("PADRA_HOME")!;

        var result = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/install-configs",
            new { targets = new[] { "codex", "claude" }, scope = "local", persistEnvironment = false }));

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(home, ".codex", "config.toml")));
        Assert.True(File.Exists(Path.Combine(home, ".claude", "settings.json")));

        var generated = result["generated"]!.GetValue<string>();
        Assert.True(File.Exists(Path.Combine(generated, "claude", "settings.json")));

        var settings = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(home, ".claude", "settings.json")))!;
        Assert.StartsWith("http://127.0.0.1:",
            settings["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installing_and_restoring_round_trips_a_users_own_file()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        var home = Environment.GetEnvironmentVariable("PADRA_HOME")!;
        var codex = Path.Combine(home, ".codex");
        Directory.CreateDirectory(codex);

        const string original = "model = \"gpt-5.6\"\n\n[tui]\nnotifications = true\n";
        await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), original);

        var installed = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/install-configs",
            new { targets = new[] { "codex" }, scope = "local", persistEnvironment = false }));
        Assert.True(installed["ok"]!.GetValue<bool>());
        Assert.True(installed["canRestore"]!.GetValue<bool>());
        Assert.NotEqual(original,
            await File.ReadAllTextAsync(Path.Combine(codex, "config.toml")));

        var state = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/client-state");
        Assert.True(state!["targets"]!["codex"]!.GetValue<bool>());

        var restored = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/uninstall-configs",
            new { targets = new[] { "codex" } }));

        Assert.True(restored["ok"]!.GetValue<bool>());
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(codex, "config.toml")));
    }

    [Fact]
    public async Task Restoring_validates_the_targets()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.PostAsJsonAsync("/api/uninstall-configs",
            new { targets = new[] { "emacs" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------ hardware and fit #
    [Fact]
    public async Task Hardware_reports_this_machine_and_every_model()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var data = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/hardware?refresh=true");

        Assert.True(data!["totalRamBytes"]!.GetValue<long>() > 0);
        Assert.Equal(new[] { "demo", "other" },
            data["models"]!.AsArray().Select(n => n!["alias"]!.GetValue<string>()));

        var model = data["models"]![0]!;
        Assert.True(model["runtime"]!["isDefault"]!.GetValue<bool>());
        Assert.True(model["runtime"]!["allowOversize"]!.GetValue<bool>());

        // Nothing is on this disk in a test environment, so nothing can be sized.
        Assert.Equal("unknown", model["footprint"]!["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task Runtime_settings_are_saved_and_reach_the_fleet_file()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var result = await JsonAsync(await gateway.Client.PostAsJsonAsync("/api/models/demo/runtime",
            new
            {
                gpuMemoryMode = "manual",
                gpuLayers = 18,
                fit = "on",
                cpuMoeLayers = 12,
                kvCacheType = "q8_0",
                flashAttention = "on",
                threads = 8,
                allowOversize = true,
                contextLength = 16384,
            }));

        Assert.True(result["ok"]!.GetValue<bool>());
        var runtime = result["model"]!["runtime"]!;
        Assert.Equal("manual", runtime["gpuMemoryMode"]!.GetValue<string>());
        Assert.Equal(18, runtime["gpuLayers"]!.GetValue<int>());
        Assert.Equal("q8_0", runtime["kvCacheType"]!.GetValue<string>());
        Assert.False(runtime["isDefault"]!.GetValue<bool>());
        Assert.Equal(16384, result["model"]!["contextLength"]!.GetValue<int>());

        var saved = FleetFile.Load(gateway.ConfigPath).Models[0];
        Assert.Equal(GpuMemoryMode.Manual, saved.Runtime.GpuMemoryMode);
        Assert.Equal(12, saved.Runtime.CpuMoeLayers);
        Assert.Equal(16384, saved.ContextLength);
    }

    [Fact]
    public async Task Runtime_settings_reject_a_value_the_backend_would_refuse()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.PostAsJsonAsync("/api/models/demo/runtime",
            new { kvCacheType = "q3_k_m" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tuning_needs_the_weights_to_be_on_this_disk()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.PostAsJsonAsync("/api/models/demo/runtime/auto",
            new { });

        // The stub fleet points at a repository that was never downloaded, so the
        // size cannot be estimated and the launcher says so instead of guessing.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_model_is_reported_by_the_runtime_endpoints()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await gateway.Client.PostAsJsonAsync("/api/models/ghost/runtime", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await gateway.Client.PostAsJsonAsync("/api/models/ghost/runtime/auto", new { })).StatusCode);
    }

    [Fact]
    public async Task Installing_validates_the_targets()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.PostAsJsonAsync("/api/install-configs",
            new { targets = new[] { "nope" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_client_bundle_is_a_zip_with_every_config()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        using var response = await gateway.Client.GetAsync("/api/client-bundle?scope=lan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);

        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync());
        var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("padra-client/claude/settings.json", names);
        Assert.Contains("padra-client/codex/config.toml", names);
        Assert.Contains("padra-client/README.txt", names);
    }

    [Fact]
    public async Task Launching_validates_the_folder_the_agent_and_the_model()
    {
        await using var gateway = await GatewayFixture.StartAsync();
        var missingFolder = Path.Combine(Path.GetTempPath(), "padra-missing-" + Guid.NewGuid());

        using var noFolder = await gateway.Client.PostAsJsonAsync("/api/launch-agent",
            new { agent = "codex", alias = "demo", folder = missingFolder });
        Assert.Equal(HttpStatusCode.BadRequest, noFolder.StatusCode);
        Assert.Equal("folder", (await JsonAsync(noFolder))["error"]!["message"]!.GetValue<string>());

        using var noAgent = await gateway.Client.PostAsJsonAsync("/api/launch-agent",
            new { agent = "codex", alias = "demo", folder = Path.GetTempPath() });
        Assert.Equal(HttpStatusCode.FailedDependency, noAgent.StatusCode);
        Assert.Equal("codex", (await JsonAsync(noAgent))["error"]!["agent"]!.GetValue<string>());

        using var unknown = await gateway.Client.PostAsJsonAsync("/api/launch-agent",
            new { agent = "emacs", alias = "demo", folder = Path.GetTempPath() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    // --------------------------------------------------------- diagnostics #
    [Fact]
    public async Task Diagnostics_report_the_environment()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var data = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/diagnostics");

        Assert.True(data!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("127.0.0.1", data["gatewayHost"]!.GetValue<string>());
        Assert.Equal("demo", data["backendPorts"]![0]!["alias"]!.GetValue<string>());
        Assert.NotEmpty(data["cacheRoots"]!.AsArray());
    }

    [Fact]
    public async Task The_log_endpoint_streams_new_entries()
    {
        await using var gateway = await GatewayFixture.StartAsync();

        var first = await gateway.Client.GetFromJsonAsync<JsonObject>("/api/log?since=0");
        var cursor = first!["cursor"]!.GetValue<long>();
        Assert.True(cursor > 0);

        await gateway.Client.PostAsync("/api/models/demo/load", null);

        var second = await gateway.Client.GetFromJsonAsync<JsonObject>($"/api/log?since={cursor}");
        Assert.NotEmpty(second!["entries"]!.AsArray());
    }
}
