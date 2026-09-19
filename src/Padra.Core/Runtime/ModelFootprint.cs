using Padra.Core.Configuration;
using Padra.Core.Discovery;

namespace Padra.Core.Runtime;

/// <summary>What a model weighs on disk, and what its header says about its shape.</summary>
public sealed record ModelWeights(long Bytes, GgufMetadata Header);

/// <summary>Where a model can be held on this machine.</summary>
public enum FitVerdict
{
    /// <summary>The weights were not found on disk, so nothing can be said.</summary>
    Unknown,

    /// <summary>Everything fits in one GPU.</summary>
    Gpu,

    /// <summary>It fits once part of it is kept in system RAM.</summary>
    Offload,

    /// <summary>Larger than VRAM and RAM together — it runs, paged from disk.</summary>
    Disk,

    /// <summary>No GPU was detected; the CPU and system RAM carry the model.</summary>
    Cpu,
}

/// <summary>
/// An estimate of what one model costs to serve, and what that means on this
/// machine.
/// <para>
/// Every figure is derived from the GGUF on disk and the header's own shape, so
/// it is an estimate and labelled as one. It exists to explain a decision to the
/// user and to pre-fill offload settings — it never gates anything.
/// </para>
/// </summary>
public sealed record ModelFootprint(
    long WeightBytes,
    long KvCacheBytes,
    long OverheadBytes,
    int ContextLength,
    FitVerdict Verdict,
    long LargestGpuBytes,
    long UsableBytes,
    bool KvCacheEstimated)
{
    public static readonly ModelFootprint Unknown =
        new(0, 0, 0, 0, FitVerdict.Unknown, 0, 0, false);

    public long TotalBytes => WeightBytes + KvCacheBytes + OverheadBytes;

    /// <summary>How much has to live outside VRAM, or zero when it all fits.</summary>
    public long ShortfallBytes => Math.Max(0, TotalBytes - LargestGpuBytes);

    public bool NeedsOffload => Verdict is FitVerdict.Offload or FitVerdict.Disk or FitVerdict.Cpu;
}

/// <summary>Sizing a model against the memory this machine reports.</summary>
public static class ModelFootprintCalculator
{
    /// <summary>Context assumed when neither the fleet nor the header states one.</summary>
    public const int FallbackContextLength = 8192;

    /// <summary>
    /// Compute buffers, the sampler and the graph scratch space. llama.cpp sizes
    /// these from the batch rather than the model, so a flat floor plus a small
    /// share of the weights tracks reality far better than either alone.
    /// </summary>
    private const long OverheadFloorBytes = 512L * 1024 * 1024;

    private const double OverheadWeightShare = 0.06;

    /// <summary>
    /// Cache elements per token assumed when the header does not describe the
    /// model's shape.
    /// <para>
    /// A 36-layer grouped-query model with 8 key/value heads and 128-wide heads
    /// costs 36 × 8 × (128 + 128) = 73 728 elements per token; 80 000 is that order
    /// of magnitude, rounded up. Erring high is deliberate: an under-estimate is
    /// reported as "fits on the GPU" and the model is then handed an optimistic
    /// placement that fails on load, which is the exact failure this whole estimate
    /// exists to avoid.
    /// </para>
    /// </summary>
    private const long FallbackKvElementsPerToken = 80_000;

    /// <summary>Bytes one cache element occupies, by cache type.</summary>
    public static double ElementBytes(string kvCacheType) => kvCacheType switch
    {
        "f32" => 4,
        "" or "f16" or "bf16" => 2,
        // Block-quantised caches store 32 values plus their scales per block.
        "q8_0" => 34d / 32,
        "q5_1" => 24d / 32,
        "q5_0" => 22d / 32,
        "q4_1" => 20d / 32,
        "q4_0" or "iq4_nl" => 18d / 32,
        _ => 2,
    };

    /// <summary>
    /// Size <paramref name="model"/> given the bytes its GGUF occupies and what the
    /// header says about its shape.
    /// </summary>
    public static ModelFootprint Estimate(
        ModelOptions model, long weightBytes, GgufMetadata metadata, HardwareSnapshot hardware)
    {
        if (weightBytes <= 0)
        {
            return ModelFootprint.Unknown;
        }

        var context = model.ContextLength
                      ?? metadata.ContextLength
                      ?? FallbackContextLength;

        var perToken = metadata.KvCacheElementsPerToken();
        long kvCache;
        bool estimated;
        if (perToken is { } exact && exact > 0)
        {
            kvCache = (long)(exact * context * ElementBytes(model.Runtime.KvCacheType));
            estimated = false;
        }
        else
        {
            kvCache = (long)(context * FallbackKvElementsPerToken
                             * ElementBytes(model.Runtime.KvCacheType));
            estimated = true;
        }

        var overhead = Math.Max(OverheadFloorBytes, (long)(weightBytes * OverheadWeightShare));
        var total = weightBytes + kvCache + overhead;

        var largestGpu = hardware.LargestGpuBytes;
        var usable = hardware.TotalUsableBytes;

        FitVerdict verdict;
        if (!hardware.HasGpu)
        {
            // With no accelerator the model has to fit in system memory, or it is
            // paged from disk exactly as an oversized one would be.
            verdict = total <= hardware.TotalRamBytes ? FitVerdict.Cpu : FitVerdict.Disk;
        }
        else if (largestGpu > 0 && total <= largestGpu)
        {
            verdict = FitVerdict.Gpu;
        }
        else if (usable > 0 && total <= usable)
        {
            verdict = FitVerdict.Offload;
        }
        else
        {
            verdict = FitVerdict.Disk;
        }

        return new ModelFootprint(weightBytes, kvCache, overhead, context, verdict,
            largestGpu, usable, estimated);
    }

    /// <summary>
    /// The runtime block that makes <paramref name="footprint"/> run on this
    /// machine: quantise the cache, hand placement to the inference server, and for
    /// a Mixture-of-Experts model move enough expert layers into system RAM to
    /// cover the shortfall.
    /// </summary>
    public static RuntimeOptions SuggestRuntime(
        RuntimeOptions current, ModelFootprint footprint, GgufMetadata metadata)
    {
        var suggested = current.Clone();
        suggested.AllowOversize = true;

        if (footprint.Verdict is FitVerdict.Unknown or FitVerdict.Gpu)
        {
            // Nothing has to change; leave placement to Unsloth.
            suggested.GpuMemoryMode = GpuMemoryMode.Auto;
            suggested.Fit = Toggle.Auto;
            suggested.CpuMoeLayers = null;
            return suggested;
        }

        suggested.Fit = Toggle.On;
        suggested.FlashAttention = Toggle.On;

        if (suggested.KvCacheType.Length == 0)
        {
            // Halves the cache at a cost that does not show in coding work.
            suggested.KvCacheType = "q8_0";
        }

        if (metadata.IsMixtureOfExperts && metadata.BlockCount is { } blocks and > 0 &&
            footprint.LargestGpuBytes > 0 && footprint.ShortfallBytes > 0)
        {
            // Expert weights dominate an MoE checkpoint, so moving the first N layers'
            // experts to system RAM frees roughly N/blocks of the whole model.
            var share = (double)footprint.ShortfallBytes / Math.Max(1, footprint.WeightBytes);
            var layers = (int)Math.Ceiling(share * blocks);
            suggested.CpuMoeLayers = layers >= blocks
                ? RuntimeOptions.AllCpuMoeLayers
                : Math.Clamp(layers, 1, blocks);
        }

        return suggested;
    }
}
