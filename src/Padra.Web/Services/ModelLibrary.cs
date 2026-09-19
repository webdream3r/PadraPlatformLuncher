using Padra.Core.Configuration;
using Padra.Core.Discovery;
using Padra.Core.Runtime;

namespace Padra.Web.Services;

public sealed record LibraryEntry(LocalModel Model, bool InFleet);

public sealed record LibrarySnapshot(
    IReadOnlyList<LibraryEntry> Models,
    long TotalBytes,
    IReadOnlyList<string> Roots,
    DateTimeOffset ScannedAt);

/// <summary>
/// The downloaded-model view the panel shows: a disk scan, cached briefly so
/// switching tabs does not re-walk a cache full of multi-gigabyte files.
/// </summary>
public sealed class ModelLibrary(ModelFleet fleet, HuggingFaceCatalog catalog)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibrarySnapshot? _cache;

    public HuggingFaceCatalog Catalog { get; } = catalog;

    public async Task<LibrarySnapshot> LocalAsync(bool refresh = false, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache is { } cached &&
                DateTimeOffset.UtcNow - cached.ScannedAt < Freshness)
            {
                return cached;
            }

            var extra = fleet.Gateway.ExtraModelPaths;
            var found = await Task.Run(() => ModelScanner.Scan(extra, token: token), token)
                .ConfigureAwait(false);

            var inFleet = fleet.Options.Models
                .Select(m => (m.Repo.ToLowerInvariant(), m.Quant.ToLowerInvariant()))
                .ToHashSet();

            var entries = found
                .Select(m => new LibraryEntry(m,
                    inFleet.Contains((m.Repo.ToLowerInvariant(), m.Quant.ToLowerInvariant()))))
                .ToList();

            var roots = ModelScanner.HuggingFaceCacheRoots().Where(Directory.Exists)
                .Concat(ModelScanner.LmStudioRoots())
                .ToList();

            _cache = new LibrarySnapshot(entries, entries.Sum(e => e.Model.SizeBytes), roots,
                DateTimeOffset.UtcNow);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cache = null;

    /// <summary>
    /// The bytes and header of a fleet entry's GGUF, from the cached scan.
    /// <para>
    /// Called on the load path, so it never starts a scan of its own: until the
    /// panel or the API has listed the library once there is nothing to report, and
    /// the fleet simply starts the model as configured.
    /// </para>
    /// </summary>
    public ModelWeights? Weights(ModelOptions model)
    {
        var snapshot = _cache;
        if (snapshot is null)
        {
            return null;
        }

        var match = snapshot.Models.FirstOrDefault(e =>
            string.Equals(e.Model.Repo, model.Repo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Model.Quant, model.Quant, StringComparison.OrdinalIgnoreCase));

        // A repository with no quantisation recorded still identifies the weights.
        match ??= snapshot.Models.FirstOrDefault(e =>
            string.Equals(e.Model.Repo, model.Repo, StringComparison.OrdinalIgnoreCase));

        return match is null || match.Model.SizeBytes <= 0
            ? null
            : new ModelWeights(match.Model.SizeBytes, match.Model.Header);
    }

    /// <summary>Scan once in the background so the fit estimate has something to use.</summary>
    public async Task WarmAsync(CancellationToken token = default)
    {
        if (_cache is not null)
        {
            return;
        }

        try
        {
            await LocalAsync(token: token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or OperationCanceledException)
        {
            // The estimate is optional; a failed scan must not stop the gateway.
        }
    }

    public async Task<IReadOnlyList<LocalModel>> LocalModelsAsync(CancellationToken token = default)
    {
        var snapshot = await LocalAsync(token: token).ConfigureAwait(false);
        return snapshot.Models.Select(e => e.Model).ToList();
    }
}
