using Padra.Core.Configuration;
using Xunit;

namespace Padra.Tests;

[Collection("environment")]
public sealed class FleetFileTests
{
    [Fact]
    public void Defaults_are_applied_to_an_empty_file()
    {
        var options = FleetFile.Parse(string.Empty);

        Assert.Equal("127.0.0.1", options.Gateway.Host);
        Assert.Equal(8900, options.Gateway.Port);
        Assert.Equal(FleetMode.Exclusive, options.Gateway.Mode);
        Assert.Equal(CodexWireApi.Responses, options.Gateway.WireApi);
        Assert.Equal(UnknownModelBehaviour.Default, options.Gateway.UnknownModel);
        Assert.Empty(options.Gateway.DefaultModel);
        Assert.Empty(options.Models);
    }

    [Fact]
    public void Model_entries_are_normalised()
    {
        var options = FleetFile.Parse("""
            models:
              - alias: Demo
                repo: unsloth/X-GGUF
                quant: UD-Q4_K_XL
                port: 8801
                contextLength: 4096
                extraArgs: ["--temp", "0.6"]
            """);

        var model = Assert.Single(options.Models);
        Assert.Equal("demo", model.Alias);
        Assert.Equal(8801, model.Port);
        Assert.Equal(4096, model.ContextLength);
        Assert.Equal(new[] { "--temp", "0.6" }, model.ExtraArgs);
        Assert.True(model.DisableTools);
        Assert.Equal("demo", options.Gateway.DefaultModel);
        Assert.Equal("unsloth/X-GGUF:UD-Q4_K_XL", model.ModelSpec);
    }

    [Theory]
    [InlineData("models:\n  - repo: x\n", "alias")]
    [InlineData("models:\n  - alias: a\n    repo: x\n    port: 70000\n", "between")]
    [InlineData("models:\n  - alias: A B\n    repo: x\n", "lowercase")]
    [InlineData("gateway:\n  mode: turbo\n", "mode")]
    [InlineData("gateway:\n  wireApi: grpc\n", "wireApi")]
    [InlineData("gateway:\n  unknownModel: maybe\n", "unknownModel")]
    [InlineData("gateway:\n  defaultModel: ghost\n", "defaultModel")]
    public void Invalid_configurations_are_rejected(string yaml, string expected)
    {
        var error = Assert.Throws<FleetFileException>(() => FleetFile.Parse(yaml));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_alias_and_port_are_rejected()
    {
        Assert.Contains("duplicate alias", Assert.Throws<FleetFileException>(() =>
            FleetFile.Parse("""
                models:
                  - {alias: a, repo: x, port: 8801}
                  - {alias: a, repo: y, port: 8802}
                """)).Message);

        Assert.Contains("port 8801", Assert.Throws<FleetFileException>(() =>
            FleetFile.Parse("""
                models:
                  - {alias: a, repo: x, port: 8801}
                  - {alias: b, repo: y, port: 8801}
                """)).Message);
    }

    [Fact]
    public void A_model_cannot_take_the_gateway_port()
    {
        var error = Assert.Throws<FleetFileException>(() => FleetFile.Parse("""
            gateway:
              port: 8900
            models:
              - {alias: a, repo: x, port: 8900}
            """));
        Assert.Contains("gateway port", error.Message);
    }

    [Fact]
    public void The_placeholder_api_key_is_replaced_once()
    {
        var options = FleetFile.Parse("gateway:\n  apiKey: sk-gateway-local-change-me\n");

        Assert.True(FleetFile.EnsureApiKey(options));
        Assert.StartsWith("sk-padra-", options.Gateway.ApiKey, StringComparison.Ordinal);
        Assert.True(options.Gateway.ApiKey.Length > 30);
        Assert.False(FleetFile.EnsureApiKey(options));
    }

    [Fact]
    public void Adding_a_model_keeps_the_default_where_it_was()
    {
        var options = FleetFile.Parse(string.Empty);
        FleetFile.AddModel(options, "unsloth/A-GGUF", "Q4_K_M");
        FleetFile.AddModel(options, "unsloth/B-GGUF", "Q4_K_M");
        FleetFile.SetDefaultModel(options, "b");

        // Adding C used to bounce the default back to the first entry.
        FleetFile.AddModel(options, "unsloth/C-GGUF", "Q4_K_M");
        Assert.Equal("b", options.Gateway.DefaultModel);

        var promoted = FleetFile.AddModel(options, "unsloth/D-GGUF", "Q4_K_M", makeDefault: true);
        Assert.Equal(promoted.Alias, options.Gateway.DefaultModel);
    }

    [Fact]
    public void Models_can_be_added_removed_and_defaulted()
    {
        var options = FleetFile.Parse(string.Empty);

        var first = FleetFile.AddModel(options, "unsloth/Qwen3.5-4B-GGUF", "UD-Q4_K_XL",
            contextLength: 262144);
        Assert.Equal("qwen3.5-4b", first.Alias);
        Assert.Equal(FleetOptions.FirstBackendPort, first.Port);
        Assert.Equal("qwen3.5-4b", options.Gateway.DefaultModel);

        var second = FleetFile.AddModel(options, "unsloth/Qwen3.5-4B-GGUF", "Q8_0");
        Assert.Equal("qwen3.5-4b-q8_0", second.Alias);
        Assert.Equal(FleetOptions.FirstBackendPort + 1, second.Port);

        Assert.Contains("already in the fleet", Assert.Throws<FleetFileException>(() =>
            FleetFile.AddModel(options, "unsloth/Qwen3.5-4B-GGUF", "Q8_0")).Message);

        FleetFile.SetDefaultModel(options, "qwen3.5-4b-q8_0");
        Assert.Equal("qwen3.5-4b-q8_0", options.Gateway.DefaultModel);

        FleetFile.RemoveModel(options, "qwen3.5-4b-q8_0");
        Assert.Equal(new[] { "qwen3.5-4b" }, options.Models.Select(m => m.Alias));
        Assert.Equal("qwen3.5-4b", options.Gateway.DefaultModel);

        FleetFile.RemoveModel(options, "qwen3.5-4b");
        Assert.Empty(options.Gateway.DefaultModel);
        Assert.Throws<FleetFileException>(() => FleetFile.RemoveModel(options, "qwen3.5-4b"));
    }

    [Fact]
    public void Saving_is_atomic_and_round_trips()
    {
        using var environment = new TestEnvironment();
        var options = FleetFile.Parse(string.Empty);
        FleetFile.AddModel(options, "unsloth/gemma-4-26B-A4B-it-GGUF", "UD-Q4_K_XL",
            contextLength: 131072);
        options.Gateway.ApiKey = "sk-padra-round-trip";

        FleetFile.Save(environment.ConfigPath, options);

        var text = File.ReadAllText(environment.ConfigPath);
        Assert.StartsWith("# Padra Platform Launcher", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(environment.Work, "*.tmp"));

        var reloaded = FleetFile.Load(environment.ConfigPath);
        Assert.Equal("gemma-4-26b-a4b-it", reloaded.Models[0].Alias);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF", reloaded.Models[0].Repo);
        Assert.Equal(131072, reloaded.Models[0].ContextLength);
        Assert.Equal("sk-padra-round-trip", reloaded.Gateway.ApiKey);
        Assert.Equal(options.Gateway.DefaultModel, reloaded.Gateway.DefaultModel);
    }

    [Fact]
    public void Loading_reports_a_missing_file()
    {
        using var environment = new TestEnvironment();
        var error = Assert.Throws<FleetFileException>(() =>
            FleetFile.Load(Path.Combine(environment.Work, "nope.yaml")));
        Assert.Contains("config not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_yaml_is_reported_not_thrown_raw()
    {
        var error = Assert.Throws<FleetFileException>(() => FleetFile.Parse("gateway: [1, 2\n"));
        Assert.Contains("not valid YAML", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_next_free_port_skips_everything_in_use()
    {
        var options = FleetFile.Parse("""
            gateway:
              port: 8802
            models:
              - {alias: a, repo: x, port: 8801}
            """);
        Assert.Equal(8803, FleetFile.NextFreePort(options));
    }

    [Fact]
    public void Validate_keeps_the_chosen_default_and_key()
    {
        var options = FleetFile.Parse(string.Empty);
        options.Gateway.ApiKey = "sk-padra-keep";
        FleetFile.AddModel(options, "unsloth/A-GGUF", "Q4_K_M");
        FleetFile.AddModel(options, "unsloth/B-GGUF", "Q4_K_M");
        FleetFile.SetDefaultModel(options, "b");

        var validated = FleetFile.Validate(options);
        Assert.Equal("b", validated.Gateway.DefaultModel);
        Assert.Equal("sk-padra-keep", validated.Gateway.ApiKey);
        Assert.Equal(2, validated.Models.Count);
    }

    [Fact]
    public void The_starter_config_is_valid()
    {
        var options = FleetFile.Parse(StarterConfig.Yaml);
        Assert.Empty(options.Models);
        Assert.True(options.Gateway.AutoImport);
        Assert.Equal(8900, options.Gateway.Port);
        Assert.Equal(EndpointSource.Gateway, options.Gateway.EndpointSource);
        Assert.Empty(options.Gateway.UnslothModel);
    }

    [Fact]
    public void The_direct_mode_choices_round_trip_through_the_file()
    {
        using var environment = new TestEnvironment();
        var options = FleetFile.Parse("""
            gateway:
              endpointSource: unsloth
              unslothServerUrl: http://192.168.30.227:8888
              unslothApiKey: sk-unsloth-pinned
              unslothModel: " unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL "
            """);

        Assert.Equal(EndpointSource.Unsloth, options.Gateway.EndpointSource);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL", options.Gateway.UnslothModel);

        FleetFile.Save(environment.ConfigPath, FleetFile.Validate(options));
        var reloaded = FleetFile.Load(environment.ConfigPath);

        Assert.Equal("http://192.168.30.227:8888", reloaded.Gateway.UnslothServerUrl);
        Assert.Equal("sk-unsloth-pinned", reloaded.Gateway.UnslothApiKey);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL", reloaded.Gateway.UnslothModel);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL", reloaded.Clone().Gateway.UnslothModel);
    }

    // =============================================================== aliases #
    [Fact]
    public void A_long_descriptive_alias_is_accepted()
    {
        // The name that used to be refused: 65 characters, every one of them legal.
        const string alias = "qwen3.6-27b-heretic-uncensored-finetune-neo-code-di-imatrix-max-2";
        Assert.True(alias.Length > 64);

        var options = FleetFile.Parse($"models:\n  - alias: {alias}\n    repo: unsloth/X-GGUF\n");

        Assert.Equal(alias, Assert.Single(options.Models).Alias);
    }

    [Fact]
    public void An_over_long_alias_is_rejected_by_length_not_by_character()
    {
        var alias = new string('a', FleetFile.MaxAliasLength + 1);
        var error = Assert.Throws<FleetFileException>(() =>
            FleetFile.Parse($"models:\n  - alias: {alias}\n    repo: unsloth/X-GGUF\n"));

        Assert.Contains("characters", error.Message, StringComparison.Ordinal);
        Assert.Contains(FleetFile.MaxAliasLength.ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("may contain only", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qwen3.6 27B (Heretic)", "qwen3.6-27b-heretic")]
    [InlineData("  --Weird__Name--  ", "weird-name")]
    [InlineData("///", "model")]
    [InlineData("UPPER", "upper")]
    public void A_typed_alias_is_repaired_rather_than_refused(string typed, string expected)
    {
        var options = FleetFile.Parse(string.Empty);
        var entry = FleetFile.AddModel(options, "unsloth/X-GGUF", alias: typed);

        Assert.Equal(expected, entry.Alias);
    }

    [Fact]
    public void A_derived_alias_always_survives_its_own_validation()
    {
        var options = FleetFile.Parse(string.Empty);
        var repo = "unsloth/" + new string('x', 200) + "-GGUF";

        var entry = FleetFile.AddModel(options, repo, "UD-Q4_K_XL");

        Assert.True(entry.Alias.Length <= FleetFile.MaxAliasLength);
        FleetFile.ValidateAlias(entry.Alias);

        // A second variant of the same over-long repository still lands somewhere free.
        var second = FleetFile.AddModel(options, repo, "Q8_0");
        Assert.NotEqual(entry.Alias, second.Alias);
        FleetFile.ValidateAlias(second.Alias);
    }

    [Fact]
    public void A_shortened_alias_does_not_end_in_a_separator()
    {
        var alias = FleetFile.SanitiseAlias(new string('a', FleetFile.MaxAliasLength - 1) + "-tail");

        Assert.True(alias.Length <= FleetFile.MaxAliasLength);
        Assert.DoesNotContain(alias[^1], "-._");
    }

    // =============================================================== runtime #
    [Fact]
    public void Runtime_options_round_trip_through_the_file()
    {
        using var environment = new TestEnvironment();
        var options = FleetFile.Parse(string.Empty);
        FleetFile.AddModel(options, "unsloth/Big-GGUF", "UD-Q4_K_XL");
        FleetFile.SetRuntime(options, "big", new RuntimeOptions
        {
            GpuMemoryMode = GpuMemoryMode.Manual,
            GpuLayers = 24,
            Fit = Toggle.On,
            FitContext = 8192,
            CpuMoeLayers = 12,
            KvCacheType = "q8_0",
            FlashAttention = Toggle.On,
            Threads = 10,
            NoKvOffload = true,
            SwaFull = true,
            AllowOversize = true,
        });

        FleetFile.Save(environment.ConfigPath, options);
        var runtime = FleetFile.Load(environment.ConfigPath).Models[0].Runtime;

        Assert.Equal(GpuMemoryMode.Manual, runtime.GpuMemoryMode);
        Assert.Equal(24, runtime.GpuLayers);
        Assert.Equal(Toggle.On, runtime.Fit);
        Assert.Equal(8192, runtime.FitContext);
        Assert.Equal(12, runtime.CpuMoeLayers);
        Assert.Equal("q8_0", runtime.KvCacheType);
        Assert.Equal(Toggle.On, runtime.FlashAttention);
        Assert.Equal(10, runtime.Threads);
        Assert.True(runtime.NoKvOffload);
        Assert.True(runtime.SwaFull);
    }

    [Fact]
    public void A_default_runtime_block_is_not_written_at_all()
    {
        using var environment = new TestEnvironment();
        var options = FleetFile.Parse(string.Empty);
        FleetFile.AddModel(options, "unsloth/Small-GGUF", "Q4_K_M");

        FleetFile.Save(environment.ConfigPath, options);

        var body = File.ReadAllText(environment.ConfigPath)[FleetFile.Header.Length..];
        Assert.DoesNotContain("runtime:", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_cache_type_is_rejected()
    {
        var error = Assert.Throws<FleetFileException>(() => FleetFile.Parse("""
            models:
              - alias: a
                repo: x
                runtime:
                  kvCacheType: q3_k_m
            """));
        Assert.Contains("kvCacheType", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--host")]
    [InlineData("--api-key")]
    [InlineData("-m")]
    [InlineData("--model=x")]
    public void Extra_arguments_the_backend_manages_are_refused_early(string flag)
    {
        var error = Assert.Throws<FleetFileException>(() => FleetFile.Parse($"""
            models:
              - alias: a
                repo: x
                extraArgs: ["{flag}", "value"]
            """));
        Assert.Contains("managed by the launcher", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_arguments_the_backend_forwards_are_kept()
    {
        var options = FleetFile.Parse("""
            models:
              - alias: a
                repo: x
                extraArgs: ["-ot", "blk\\..*_exps=CPU", "--jinja"]
            """);

        Assert.Equal(new[] { "-ot", "blk\\..*_exps=CPU", "--jinja" },
            Assert.Single(options.Models).ExtraArgs);
    }
}
