using System.Text.RegularExpressions;

namespace Padra.Core.Discovery;

/// <summary>Where a downloaded model was found.</summary>
public enum ModelSource
{
    HuggingFace,
    LmStudio,
    Folder,
}

/// <summary>One downloaded quantisation variant of one repository.</summary>
public sealed record LocalModel(
    string Repo,
    string Quant,
    ModelSource Source,
    long SizeBytes,
    int FileCount,
    DateTimeOffset Modified,
    string Path,
    int? ContextLength = null,
    string? Architecture = null,
    GgufMetadata? Metadata = null)
{
    public string Spec => string.IsNullOrEmpty(Quant) ? Repo : $"{Repo}:{Quant}";

    /// <summary>The GGUF header, or an empty one when it was not read.</summary>
    public GgufMetadata Header => Metadata ?? GgufMetadata.Empty;
}

/// <summary>Turning repository and file names into the <c>repo:QUANT</c> token Unsloth expects.</summary>
public static partial class ModelNaming
{
    // Shard suffix used by llama.cpp for split GGUF files: name-00001-of-00003.gguf
    [GeneratedRegex(@"-\d{5}-of-\d{5}$")]
    private static partial Regex ShardSuffix();

    // Quantisation tokens Unsloth publishes (UD- = Unsloth Dynamic).
    [GeneratedRegex(@"(?:UD-)?(?:I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuantToken();

    /// <summary><c>unsloth/Qwen3.6-35B-A3B-GGUF</c> -> <c>Qwen3.6-35B-A3B</c>.</summary>
    public static string RepoBaseName(string repo)
    {
        var name = repo.Split('/')[^1];
        foreach (var suffix in new[] { "-GGUF", "-gguf", ".GGUF", ".gguf" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    /// <summary>Derive the <c>--model repo:QUANT</c> token from a file inside a repository.</summary>
    public static string QuantFromRelativePath(string relative, string repo)
    {
        var normalised = relative.Replace('\\', '/').Trim('/');
        var parts = normalised.Split('/');
        var stem = parts[^1];

        if (stem.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^".gguf".Length];
        }

        stem = ShardSuffix().Replace(stem, string.Empty);

        // Sharded models live in a directory named after the quantisation.
        if (parts.Length > 1 && parts[^2].Length > 0)
        {
            return parts[^2];
        }

        var basis = RepoBaseName(repo);
        if (stem.StartsWith(basis + "-", StringComparison.OrdinalIgnoreCase) &&
            stem.Length > basis.Length + 1)
        {
            return stem[(basis.Length + 1)..];
        }

        var match = QuantToken().Match(stem);
        return match.Success ? match.Value : stem;
    }
}

/// <summary>
/// Finds every GGUF already downloaded on this machine.
/// <para>
/// Unsloth stores models in the standard Hugging Face hub cache and additionally
/// detects models downloaded by LM Studio, so both layouts are scanned. Sizes are
/// real on-disk byte counts, de-duplicated by target file so a snapshot symlink
/// is never counted twice.
/// </para>
/// </summary>
public static class ModelScanner
{
    /// <summary>Every Hugging Face hub cache directory this machine may be using.</summary>
    public static IReadOnlyList<string> HuggingFaceCacheRoots()
    {
        var roots = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(full);
            }
        }

        Add(Environment.GetEnvironmentVariable("HF_HUB_CACHE"));

        var hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrWhiteSpace(hfHome))
        {
            Add(Path.Combine(hfHome, "hub"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            Add(Path.Combine(xdg, "huggingface", "hub"));
        }

        Add(Path.Combine(UserHome(), ".cache", "huggingface", "hub"));
        return roots;
    }

    /// <summary>LM Studio's model folder, which Unsloth Studio also picks up.</summary>
    public static IReadOnlyList<string> LmStudioRoots()
    {
        var candidates = new[]
        {
            Path.Combine(UserHome(), ".lmstudio", "models"),
            Path.Combine(UserHome(), ".cache", "lm-studio", "models"),
        };
        return candidates.Where(Directory.Exists).ToList();
    }

    public static string UserHome() =>
        Environment.GetEnvironmentVariable("PADRA_HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>Every downloaded GGUF variant on this machine, grouped by repository.</summary>
    public static IReadOnlyList<LocalModel> Scan(
        IEnumerable<string>? extraRoots = null,
        bool readHeaders = true,
        CancellationToken token = default)
    {
        var found = new Dictionary<(string Repo, string Quant), LocalModel>();

        void Remember(LocalModel model)
        {
            var key = (model.Repo.ToLowerInvariant(), model.Quant.ToLowerInvariant());
            if (model.SizeBytes > 0 && !found.ContainsKey(key))
            {
                found[key] = model;
            }
        }

        foreach (var root in HuggingFaceCacheRoots())
        {
            token.ThrowIfCancellationRequested();
            foreach (var model in ScanHuggingFaceRoot(root, readHeaders, token))
            {
                Remember(model);
            }
        }

        foreach (var root in LmStudioRoots())
        {
            token.ThrowIfCancellationRequested();
            foreach (var model in ScanFolderRoot(root, ModelSource.LmStudio, readHeaders, token))
            {
                Remember(model);
            }
        }

        foreach (var root in extraRoots ?? Array.Empty<string>())
        {
            token.ThrowIfCancellationRequested();
            foreach (var model in ScanFolderRoot(root, ModelSource.Folder, readHeaders, token))
            {
                Remember(model);
            }
        }

        return found.Values
            .OrderBy(m => m.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.SizeBytes)
            .ToList();
    }

    private static IEnumerable<LocalModel> ScanHuggingFaceRoot(
        string root, bool readHeaders, CancellationToken token)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var repoDir in SafeDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(repoDir);
            if (!name.StartsWith("models--", StringComparison.Ordinal))
            {
                continue;
            }

            var repo = name["models--".Length..].Replace("--", "/", StringComparison.Ordinal);
            var snapshots = Path.Combine(repoDir, "snapshots");
            if (!Directory.Exists(snapshots))
            {
                continue;
            }

            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var snapshot in SafeDirectories(snapshots))
            {
                foreach (var file in SafeFiles(snapshot, "*.gguf"))
                {
                    var relative = Path.GetRelativePath(snapshot, file);
                    var quant = ModelNaming.QuantFromRelativePath(relative, repo);
                    if (!grouped.TryGetValue(quant, out var list))
                    {
                        grouped[quant] = list = new List<string>();
                    }

                    list.Add(file);
                }
            }

            foreach (var (quant, files) in grouped)
            {
                yield return Build(repo, quant, files, ModelSource.HuggingFace, readHeaders);
            }
        }
    }

    private static IEnumerable<LocalModel> ScanFolderRoot(
        string root, ModelSource source, bool readHeaders, CancellationToken token)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var grouped = new Dictionary<(string Repo, string Quant), List<string>>();
        foreach (var file in SafeFiles(root, "*.gguf"))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var parts = relative.Split('/');

            string repo;
            string inner;
            if (parts.Length >= 3)
            {
                repo = $"{parts[0]}/{parts[1]}";
                inner = string.Join('/', parts[2..]);
            }
            else if (parts.Length == 2)
            {
                repo = parts[0];
                inner = parts[1];
            }
            else
            {
                repo = new DirectoryInfo(root).Name;
                inner = parts[0];
            }

            var key = (repo, ModelNaming.QuantFromRelativePath(inner, repo));
            if (!grouped.TryGetValue(key, out var list))
            {
                grouped[key] = list = new List<string>();
            }

            list.Add(file);
        }

        foreach (var ((repo, quant), files) in grouped)
        {
            yield return Build(repo, quant, files, source, readHeaders);
        }
    }

    private static LocalModel Build(
        string repo, string quant, List<string> files, ModelSource source, bool readHeaders)
    {
        files.Sort(StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        var modified = DateTimeOffset.MinValue;

        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            // Hugging Face snapshots are links into blobs/, so several entries can
            // point at one file; resolving first keeps the total honest.
            var identity = ResolveIdentity(info);
            if (!seen.Add(identity))
            {
                continue;
            }

            size += info.Length;
            var stamp = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (stamp > modified)
            {
                modified = stamp;
            }
        }

        var metadata = GgufMetadata.Empty;
        if (readHeaders && files.Count > 0)
        {
            metadata = GgufReader.Read(files[0]);
        }

        return new LocalModel(
            repo,
            quant,
            source,
            size,
            files.Count,
            modified == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : modified,
            files.Count > 0 ? Path.GetDirectoryName(files[0]) ?? string.Empty : string.Empty,
            metadata.ContextLength,
            metadata.Architecture,
            metadata);
    }

    private static string ResolveIdentity(FileInfo info)
    {
        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return info.FullName;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            var entries = Directory.GetDirectories(root);
            Array.Sort(entries, StringComparer.Ordinal);
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.System,
        };

        try
        {
            return Directory.EnumerateFiles(root, pattern, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
