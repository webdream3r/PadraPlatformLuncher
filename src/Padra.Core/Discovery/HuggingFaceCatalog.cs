using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Padra.Core.Discovery;

public sealed record CatalogVariant(
    string Repo,
    string Quant,
    long SizeBytes,
    int FileCount,
    int? ContextLength,
    bool Downloaded = false,
    string? Path = null)
{
    public string Spec => string.IsNullOrEmpty(Quant) ? Repo : $"{Repo}:{Quant}";
}

public sealed record CatalogRepo(
    string Repo,
    long Downloads,
    long Likes,
    string LastModified,
    int? ContextLength,
    IReadOnlyList<CatalogVariant> Variants);

/// <summary>Raised when the Hugging Face API cannot be reached or answers badly.</summary>
public sealed class CatalogException : Exception
{
    public CatalogException(string message) : base(message)
    {
    }

    public CatalogException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Live view of the Unsloth GGUF repositories on Hugging Face: repositories,
/// quantisation variants, download sizes and the context length recorded in the
/// GGUF metadata. Results are cached briefly so browsing stays responsive.
/// </summary>
public sealed class HuggingFaceCatalog
{
    public const string Author = "unsloth";

    private readonly HttpClient _http;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, CatalogRepo Value)> _variants = new();
    private (DateTimeOffset At, IReadOnlyList<CatalogRepo> Value)? _repos;

    public HuggingFaceCatalog(HttpClient http, TimeSpan? ttl = null)
    {
        _http = http;
        _ttl = ttl ?? TimeSpan.FromMinutes(15);
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(
                Environment.GetEnvironmentVariable("HF_ENDPOINT")?.TrimEnd('/')
                ?? "https://huggingface.co");
        }

        var token = Environment.GetEnvironmentVariable("HF_TOKEN")
                    ?? Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<IReadOnlyList<CatalogRepo>> RepositoriesAsync(
        string search = "", int limit = 60, bool refresh = false, CancellationToken token = default)
    {
        var query = search.Trim();
        if (query.Length == 0 && !refresh && _repos is { } cached &&
            DateTimeOffset.UtcNow - cached.At < _ttl)
        {
            return cached.Value.Take(limit).ToList();
        }

        var bounded = Math.Clamp(limit, 1, 200);
        var url = $"/api/models?author={Author}&filter=gguf&sort=downloads&direction=-1&limit={bounded}";
        if (query.Length > 0)
        {
            url += "&search=" + WebUtility.UrlEncode(query);
        }

        var payload = await GetAsync<List<HubModel>>(url, token).ConfigureAwait(false);
        var repos = (payload ?? new List<HubModel>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new CatalogRepo(m.Id!, m.Downloads, m.Likes, m.LastModified ?? string.Empty,
                null, Array.Empty<CatalogVariant>()))
            .ToList();

        if (query.Length == 0)
        {
            _repos = (DateTimeOffset.UtcNow, repos);
        }

        return repos;
    }

    public async Task<CatalogRepo> VariantsAsync(
        string repo, bool refresh = false, CancellationToken token = default)
    {
        var key = repo.ToLowerInvariant();
        if (!refresh && _variants.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < _ttl)
        {
            return cached.Value;
        }

        var meta = await GetAsync<HubModel>($"/api/models/{repo}", token).ConfigureAwait(false)
                   ?? throw new CatalogException($"{repo} was not found");
        var tree = await GetAsync<List<HubTreeEntry>>($"/api/models/{repo}/tree/main?recursive=1", token)
            .ConfigureAwait(false) ?? new List<HubTreeEntry>();

        var context = meta.Gguf?.ContextLength;
        var grouped = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (var entry in tree)
        {
            if (!string.Equals(entry.Type, "file", StringComparison.Ordinal) ||
                entry.Path is null ||
                !entry.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = entry.Size > 0 ? entry.Size : entry.Lfs?.Size ?? 0;
            var quant = ModelNaming.QuantFromRelativePath(entry.Path, repo);
            if (!grouped.TryGetValue(quant, out var sizes))
            {
                grouped[quant] = sizes = new List<long>();
            }

            sizes.Add(size);
        }

        var record = new CatalogRepo(
            repo,
            meta.Downloads,
            meta.Likes,
            meta.LastModified ?? string.Empty,
            context,
            grouped
                .Select(pair => new CatalogVariant(repo, pair.Key, pair.Value.Sum(),
                    pair.Value.Count, context))
                .OrderBy(v => v.SizeBytes)
                .ToList());

        _variants[key] = (DateTimeOffset.UtcNow, record);
        return record;
    }

    /// <summary>Catalog variants annotated with what is already on disk.</summary>
    public static CatalogRepo MergeDownloaded(CatalogRepo record, IEnumerable<LocalModel> local)
    {
        var have = local
            .Where(m => string.Equals(m.Repo, record.Repo, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.Quant.ToLowerInvariant(), m => m);

        var variants = record.Variants.Select(variant =>
            have.TryGetValue(variant.Quant.ToLowerInvariant(), out var match)
                ? variant with
                {
                    Downloaded = true,
                    SizeBytes = match.SizeBytes,
                    Path = match.Path,
                    ContextLength = match.ContextLength ?? variant.ContextLength,
                }
                : variant).ToList();

        return record with { Variants = variants };
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken token)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CatalogException(ex.Message, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new CatalogException($"HTTP {(int)response.StatusCode} for {url}");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new CatalogException("the catalog response was not JSON", ex);
            }
        }
    }

    private sealed class HubModel
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("likes")] public long Likes { get; set; }
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
        [JsonPropertyName("gguf")] public HubGguf? Gguf { get; set; }
    }

    private sealed class HubGguf
    {
        [JsonPropertyName("context_length")] public int? ContextLength { get; set; }
    }

    private sealed class HubTreeEntry
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("lfs")] public HubLfs? Lfs { get; set; }
    }

    private sealed class HubLfs
    {
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
