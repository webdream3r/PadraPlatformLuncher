using Padra.Core.Configuration;
using Padra.Core.Discovery;
using Padra.Core.Runtime;
using Xunit;

namespace Padra.Tests;

/// <summary>
/// The offload settings that decide whether a model bigger than this GPU runs at
/// all, and the estimate the panel shows before it does.
/// </summary>
[Collection("environment")]
public sealed class RuntimeTests
{
    private static ModelBackend Backend(Action<ModelOptions>? configure = null)
    {
        var options = new ModelOptions
        {
            Alias = "big",
            Repo = "unsloth/Big-GGUF",
            Quant = "UD-Q4_K_XL",
            Port = 8801,
            ContextLength = 32768,
        };
        configure?.Invoke(options);
        return new ModelBackend(options, new GatewayOptions(), new ActivityLog());
    }

    private static GgufMetadata Header(int experts = 0) => new(
        Architecture: "qwen3",
        ContextLength: 262144,
        BlockCount: 48,
        EmbeddingLength: 5120,
        HeadCount: 40,
        HeadCountKv: 8,
        KeyLength: 128,
        ValueLength: 128,
        ExpertCount: experts);

    private static HardwareSnapshot Machine(long vramBytes, long ramBytes) => new(
        ramBytes,
        ramBytes,
        vramBytes > 0
            ? new[] { new GpuDevice(0, "Test GPU", "nvidia", vramBytes, vramBytes) }
            : Array.Empty<GpuDevice>(),
        UnifiedMemory: false,
        Notes: Array.Empty<string>(),
        TakenAt: DateTimeOffset.UnixEpoch);

    // ============================================================= arguments #
    [Fact]
    public void A_default_runtime_adds_nothing_to_the_command_line()
    {
        Assert.Empty(Backend().RuntimeArguments());
    }

    [Fact]
    public void Every_runtime_option_reaches_the_command_line()
    {
        var backend = Backend(o => o.Runtime = new RuntimeOptions
        {
            GpuMemoryMode = GpuMemoryMode.Manual,
            GpuLayers = 24,
            Fit = Toggle.On,
            FitContext = 4096,
            CpuMoeLayers = 20,
            KvCacheType = "q8_0",
            FlashAttention = Toggle.On,
            Threads = 12,
            NoKvOffload = true,
            SwaFull = true,
        });

        var arguments = string.Join(' ', backend.RuntimeArguments());

        Assert.Contains("--gpu-memory-mode manual", arguments, StringComparison.Ordinal);
        Assert.Contains("-ngl 24", arguments, StringComparison.Ordinal);
        Assert.Contains("--fit on", arguments, StringComparison.Ordinal);
        Assert.Contains("-fitc 4096", arguments, StringComparison.Ordinal);
        Assert.Contains("-ncmoe 20", arguments, StringComparison.Ordinal);
        Assert.Contains("-ctk q8_0 -ctv q8_0", arguments, StringComparison.Ordinal);
        Assert.Contains("-fa on", arguments, StringComparison.Ordinal);
        Assert.Contains("-t 12", arguments, StringComparison.Ordinal);
        Assert.Contains("-nkvo", arguments, StringComparison.Ordinal);
        Assert.Contains("--swa-full", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Minus_one_means_all_layers_and_all_experts()
    {
        var backend = Backend(o => o.Runtime = new RuntimeOptions
        {
            GpuLayers = RuntimeOptions.AllGpuLayers,
            CpuMoeLayers = RuntimeOptions.AllCpuMoeLayers,
        });

        var arguments = backend.RuntimeArguments();

        Assert.Contains("all", arguments);
        Assert.Contains("-cmoe", arguments);
        Assert.DoesNotContain("-ncmoe", arguments);
    }

    [Fact]
    public void Hand_written_arguments_win_over_panel_settings()
    {
        var backend = Backend(o =>
        {
            o.Runtime = new RuntimeOptions { Fit = Toggle.Off };
            o.ExtraArgs = new List<string> { "--fit", "on" };
        });

        var arguments = backend.BuildArguments().ToList();

        // The inference server takes the last occurrence, so ours has to come first.
        Assert.True(arguments.IndexOf("--fit") < arguments.LastIndexOf("--fit"));
        Assert.Equal("on", arguments[^1]);
    }

    [Fact]
    public void A_changed_runtime_block_replaces_the_running_backend()
    {
        var backend = Backend();
        var changed = backend.Options.Clone();
        changed.Runtime.KvCacheType = "q8_0";

        Assert.True(backend.Matches(backend.Options.Clone()));
        Assert.False(backend.Matches(changed));
    }

    // ============================================================= footprint #
    [Fact]
    public void The_cache_is_sized_from_the_header_when_the_shape_is_there()
    {
        var model = new ModelOptions { Alias = "a", Repo = "x", ContextLength = 32768 };
        var footprint = ModelFootprintCalculator.Estimate(
            model, 20L * 1024 * 1024 * 1024, Header(), Machine(24L << 30, 64L << 30));

        // 48 blocks × 8 kv heads × (128 + 128) × 32 768 tokens × 2 bytes.
        Assert.Equal(48L * 8 * 256 * 32768 * 2, footprint.KvCacheBytes);
        Assert.False(footprint.KvCacheEstimated);
        Assert.Equal(32768, footprint.ContextLength);
    }

    [Fact]
    public void Quantising_the_cache_shrinks_the_estimate()
    {
        var plain = new ModelOptions { Alias = "a", Repo = "x", ContextLength = 32768 };
        var quantised = plain.Clone();
        quantised.Runtime.KvCacheType = "q4_0";

        var machine = Machine(24L << 30, 64L << 30);
        var before = ModelFootprintCalculator.Estimate(plain, 1L << 30, Header(), machine);
        var after = ModelFootprintCalculator.Estimate(quantised, 1L << 30, Header(), machine);

        Assert.True(after.KvCacheBytes < before.KvCacheBytes / 3);
    }

    [Fact]
    public void An_unreadable_header_still_produces_a_flagged_estimate()
    {
        var footprint = ModelFootprintCalculator.Estimate(
            new ModelOptions { Alias = "a", Repo = "x" }, 1L << 30, GgufMetadata.Empty,
            Machine(24L << 30, 64L << 30));

        Assert.True(footprint.KvCacheEstimated);
        Assert.Equal(ModelFootprintCalculator.FallbackContextLength, footprint.ContextLength);
    }

    [Fact]
    public void An_unreadable_header_does_not_make_a_huge_model_look_small()
    {
        // The failure this guards: with no shape in the header the cache was costed
        // at 128 bytes a token instead of ~128 KiB, so a model needing 39 GB was
        // reported as fitting a 24 GB card and started without offload.
        var model = new ModelOptions { Alias = "a", Repo = "x", ContextLength = 262144 };
        var machine = Machine(24L << 30, 64L << 30);
        var weights = 3L << 30;

        var known = ModelFootprintCalculator.Estimate(model, weights, Header(), machine);
        var unknown = ModelFootprintCalculator.Estimate(model, weights, GgufMetadata.Empty, machine);

        Assert.Equal(FitVerdict.Offload, known.Verdict);
        Assert.Equal(FitVerdict.Offload, unknown.Verdict);

        // Within an order of magnitude of the measured figure, and never under it by
        // more than a factor of two.
        Assert.True(unknown.KvCacheBytes * 2 >= known.KvCacheBytes,
            $"fallback {unknown.KvCacheBytes} is far below measured {known.KvCacheBytes}");
    }

    [Fact]
    public void Weights_that_are_not_on_disk_yield_no_verdict()
    {
        Assert.Equal(FitVerdict.Unknown, ModelFootprintCalculator.Estimate(
            new ModelOptions { Alias = "a", Repo = "x" }, 0, Header(),
            Machine(24L << 30, 64L << 30)).Verdict);
    }

    [Theory]
    [InlineData(24, 64, 8, FitVerdict.Gpu)]
    [InlineData(8, 64, 40, FitVerdict.Offload)]
    [InlineData(8, 16, 400, FitVerdict.Disk)]
    [InlineData(0, 64, 8, FitVerdict.Cpu)]
    public void The_verdict_follows_the_memory_this_machine_has(
        int vramGb, int ramGb, int weightGb, FitVerdict expected)
    {
        var footprint = ModelFootprintCalculator.Estimate(
            new ModelOptions { Alias = "a", Repo = "x", ContextLength = 8192 },
            (long)weightGb << 30,
            Header(),
            Machine((long)vramGb << 30, (long)ramGb << 30));

        Assert.Equal(expected, footprint.Verdict);
    }

    // ============================================================ suggestion #
    [Fact]
    public void A_model_that_fits_is_left_to_place_itself()
    {
        var machine = Machine(48L << 30, 64L << 30);
        var model = new ModelOptions { Alias = "a", Repo = "x", ContextLength = 8192 };
        var footprint = ModelFootprintCalculator.Estimate(model, 8L << 30, Header(), machine);

        var suggested = ModelFootprintCalculator.SuggestRuntime(model.Runtime, footprint, Header());

        Assert.Equal(Toggle.Auto, suggested.Fit);
        Assert.Null(suggested.CpuMoeLayers);
        Assert.True(suggested.AllowOversize);
    }

    [Fact]
    public void A_model_that_does_not_fit_is_given_offload_settings()
    {
        var machine = Machine(8L << 30, 64L << 30);
        var model = new ModelOptions { Alias = "a", Repo = "x", ContextLength = 8192 };
        var header = Header(experts: 128);
        var footprint = ModelFootprintCalculator.Estimate(model, 40L << 30, header, machine);

        var suggested = ModelFootprintCalculator.SuggestRuntime(model.Runtime, footprint, header);

        Assert.Equal(Toggle.On, suggested.Fit);
        Assert.Equal("q8_0", suggested.KvCacheType);
        Assert.True(suggested.AllowOversize);

        // A Mixture-of-Experts checkpoint moves experts, not whole layers.
        Assert.NotNull(suggested.CpuMoeLayers);
        Assert.True(suggested.CpuMoeLayers is RuntimeOptions.AllCpuMoeLayers or (> 0 and <= 48));

        // And the result is a command line the backend accepts.
        var backend = Backend(o =>
        {
            o.ContextLength = 8192;
            o.Runtime = suggested;
        });
        UnslothArguments.Validate(backend.RuntimeArguments(), "runtime");
    }

    [Fact]
    public void A_dense_model_is_not_given_expert_offload()
    {
        var machine = Machine(8L << 30, 64L << 30);
        var model = new ModelOptions { Alias = "a", Repo = "x", ContextLength = 8192 };
        var footprint = ModelFootprintCalculator.Estimate(model, 40L << 30, Header(), machine);

        Assert.Null(ModelFootprintCalculator
            .SuggestRuntime(model.Runtime, footprint, Header()).CpuMoeLayers);
    }

    // ================================================================= fleet #
    [Fact]
    public async Task A_model_too_large_for_this_machine_is_started_with_offload()
    {
        using var environment = new TestEnvironment();
        var options = environment.WriteFleet(o =>
            FleetFile.AddModel(o, "unsloth/Huge-GGUF", "UD-Q4_K_XL", contextLength: 4096));

        await using var fleet = new ModelFleet(options, environment.ConfigPath, new ActivityLog())
        {
            // Larger than any machine's VRAM and RAM together.
            WeightsSource = _ => new ModelWeights(4096L << 30, Header()),
        };

        var backend = fleet.Find("huge")!;
        backend.UnslothBin = Path.Combine(environment.Work, "no-such-unsloth");

        await Assert.ThrowsAsync<BackendStartException>(() => fleet.EnsureAsync(backend));

        Assert.Equal(new[] { "--fit", "on" }, backend.AdaptiveArguments);
        Assert.Contains("--fit", backend.BuildArguments());
    }

    [Fact]
    public async Task A_model_that_fits_is_started_exactly_as_configured()
    {
        using var environment = new TestEnvironment();
        var options = environment.WriteFleet(o =>
            FleetFile.AddModel(o, "unsloth/Tiny-GGUF", "Q4_K_M", contextLength: 4096));

        await using var fleet = new ModelFleet(options, environment.ConfigPath, new ActivityLog())
        {
            WeightsSource = _ => new ModelWeights(64L << 20, Header()),
        };

        var backend = fleet.Find("tiny")!;
        backend.UnslothBin = Path.Combine(environment.Work, "no-such-unsloth");

        await Assert.ThrowsAsync<BackendStartException>(() => fleet.EnsureAsync(backend));

        Assert.Empty(backend.AdaptiveArguments);
    }

    [Fact]
    public async Task An_explicit_fit_setting_is_never_overridden()
    {
        using var environment = new TestEnvironment();
        var options = environment.WriteFleet(o =>
        {
            FleetFile.AddModel(o, "unsloth/Huge-GGUF", "UD-Q4_K_XL", contextLength: 4096);
            FleetFile.SetRuntime(o, "huge", new RuntimeOptions { Fit = Toggle.Off });
        });

        await using var fleet = new ModelFleet(options, environment.ConfigPath, new ActivityLog())
        {
            WeightsSource = _ => new ModelWeights(4096L << 30, Header()),
        };

        var backend = fleet.Find("huge")!;
        backend.UnslothBin = Path.Combine(environment.Work, "no-such-unsloth");

        await Assert.ThrowsAsync<BackendStartException>(() => fleet.EnsureAsync(backend));

        Assert.Empty(backend.AdaptiveArguments);
        Assert.Contains("off", backend.BuildArguments());
    }

    [Fact]
    public async Task Turning_off_oversize_support_leaves_the_command_line_alone()
    {
        using var environment = new TestEnvironment();
        var options = environment.WriteFleet(o =>
        {
            FleetFile.AddModel(o, "unsloth/Huge-GGUF", "UD-Q4_K_XL", contextLength: 4096);
            FleetFile.SetRuntime(o, "huge", new RuntimeOptions { AllowOversize = false });
        });

        await using var fleet = new ModelFleet(options, environment.ConfigPath, new ActivityLog())
        {
            WeightsSource = _ => new ModelWeights(4096L << 30, Header()),
        };

        var backend = fleet.Find("huge")!;
        backend.UnslothBin = Path.Combine(environment.Work, "no-such-unsloth");

        await Assert.ThrowsAsync<BackendStartException>(() => fleet.EnsureAsync(backend));

        Assert.Empty(backend.AdaptiveArguments);
    }

    // ============================================================== hardware #
    [Fact]
    public void The_hardware_probe_answers_without_throwing()
    {
        var snapshot = HardwareProbe.Read(refresh: true);

        Assert.True(snapshot.TotalRamBytes > 0, "no system memory was reported");
        Assert.True(snapshot.AvailableRamBytes <= snapshot.TotalRamBytes);
        Assert.All(snapshot.Gpus, gpu => Assert.True(gpu.TotalBytes >= gpu.FreeBytes));

        // A second read inside the freshness window is the same snapshot.
        Assert.Same(snapshot, HardwareProbe.Read());
    }
}
