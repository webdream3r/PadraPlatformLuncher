using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Padra.Core.Clients;

/// <summary>Where a persisted environment variable lives.</summary>
public enum EnvScope
{
    /// <summary>The Windows per-user environment block.</summary>
    WindowsUser,

    /// <summary>A marker-delimited block inside a POSIX shell profile.</summary>
    ProfileFile,
}

/// <summary>One file the launcher changed, and what it looked like beforehand.</summary>
public sealed class InstalledFile
{
    [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;

    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>False when the launcher created the file, so a restore deletes it.</summary>
    [JsonPropertyName("existed")] public bool Existed { get; set; }

    /// <summary>File name under <c>originals/</c> holding the pristine bytes.</summary>
    [JsonPropertyName("original")] public string? Original { get; set; }

    [JsonPropertyName("capturedAt")] public long CapturedAt { get; set; }
}

/// <summary>One environment variable the launcher set, and its previous value.</summary>
public sealed class InstalledEnv
{
    [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("scope")] public EnvScope Scope { get; set; }

    /// <summary>The profile file the block was written into, for <see cref="EnvScope.ProfileFile"/>.</summary>
    [JsonPropertyName("file")] public string? File { get; set; }

    [JsonPropertyName("existed")] public bool Existed { get; set; }

    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

/// <summary>The manifest as it sits on disk.</summary>
public sealed class InstallManifest
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("files")] public List<InstalledFile> Files { get; set; } = new();

    [JsonPropertyName("environment")] public List<InstalledEnv> Environment { get; set; } = new();

    /// <summary>Directories the launcher created, deepest first, for a clean restore.</summary>
    [JsonPropertyName("directories")] public List<string> Directories { get; set; } = new();
}

/// <summary>
/// Remembers what every agent's configuration looked like before the launcher
/// first touched it, so "restore original configuration" is a real restore rather
/// than a best-effort undo.
/// <para>
/// The pristine copy is taken once — the first time a path is written — and never
/// replaced, so reinstalling ten times still reverts to what the user had. The
/// store lives under the user's home beside the agents it describes, because the
/// files it protects are per-user and outlive any single <c>models.yaml</c>.
/// </para>
/// </summary>
public sealed class ClientInstallState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();

    private ClientInstallState(string root)
    {
        Root = root;
        ManifestPath = Path.Combine(root, "manifest.json");
        OriginalsDirectory = Path.Combine(root, "originals");
        Manifest = ReadManifest(ManifestPath);
    }

    public string Root { get; }

    public string ManifestPath { get; }

    public string OriginalsDirectory { get; }

    public InstallManifest Manifest { get; private set; }

    /// <summary>The store for the current user, created on first write.</summary>
    public static ClientInstallState Open() =>
        new(Path.Combine(Discovery.ModelScanner.UserHome(), ".padra", "client-state"));

    public static ClientInstallState OpenAt(string root) => new(root);

    /// <summary>True when a restore has something to put back for these targets.</summary>
    public bool HasRecordFor(IEnumerable<string> targets)
    {
        var wanted = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            return Manifest.Files.Any(f => wanted.Contains(f.Target)) ||
                   Manifest.Environment.Any(e => wanted.Contains(e.Target));
        }
    }

    public IReadOnlyList<InstalledFile> FilesFor(string target)
    {
        lock (_lock)
        {
            return Manifest.Files
                .Where(f => string.Equals(f.Target, target, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    // ------------------------------------------------------------- capturing #
    /// <summary>
    /// Record what <paramref name="path"/> looks like right now, unless it was
    /// already captured by an earlier install.
    /// </summary>
    public void Capture(string target, string path)
    {
        var full = Path.GetFullPath(path);
        lock (_lock)
        {
            if (Manifest.Files.Any(f => PathsMatch(f.Path, full)))
            {
                return;
            }

            var record = new InstalledFile
            {
                Target = target,
                Path = full,
                Existed = File.Exists(full),
                CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            if (record.Existed)
            {
                record.Original = StoreOriginal(full);
                if (record.Original is null)
                {
                    // Nothing was captured, so a restore must not delete the file.
                    return;
                }
            }
            else
            {
                RememberCreatedDirectories(full);
            }

            Manifest.Files.Add(record);
            Save();
        }
    }

    /// <summary>Record an environment variable's value before it is overwritten.</summary>
    public void CaptureEnvironment(string target, string name, EnvScope scope, string? file,
        string? previous)
    {
        lock (_lock)
        {
            if (Manifest.Environment.Any(e =>
                    string.Equals(e.Name, name, StringComparison.Ordinal) &&
                    e.Scope == scope &&
                    string.Equals(e.File ?? string.Empty, file ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Manifest.Environment.Add(new InstalledEnv
            {
                Target = target,
                Name = name,
                Scope = scope,
                File = file,
                Existed = previous is not null,
                Previous = previous,
            });
            Save();
        }
    }

    // -------------------------------------------------------------- restoring #
    /// <summary>The recorded files for <paramref name="targets"/>, newest capture first.</summary>
    public IReadOnlyList<InstalledFile> Take(IEnumerable<string> targets)
    {
        var wanted = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            return Manifest.Files.Where(f => wanted.Contains(f.Target)).ToList();
        }
    }

    public IReadOnlyList<InstalledEnv> TakeEnvironment(IEnumerable<string> targets)
    {
        var wanted = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            return Manifest.Environment.Where(e => wanted.Contains(e.Target)).ToList();
        }
    }

    /// <summary>The pristine bytes for a record, or null when they are gone.</summary>
    public byte[]? OriginalBytes(InstalledFile record)
    {
        if (record.Original is null)
        {
            return null;
        }

        var path = Path.Combine(OriginalsDirectory, record.Original);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forget everything recorded for these targets and delete its copies.</summary>
    public void Forget(IEnumerable<string> targets)
    {
        var wanted = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var record in Manifest.Files.Where(f => wanted.Contains(f.Target)).ToList())
            {
                if (record.Original is not null)
                {
                    Delete(Path.Combine(OriginalsDirectory, record.Original));
                }
            }

            Manifest.Files.RemoveAll(f => wanted.Contains(f.Target));
            Manifest.Environment.RemoveAll(e => wanted.Contains(e.Target));

            if (Manifest.Files.Count == 0)
            {
                Manifest.Directories.Clear();
            }

            Save();
        }
    }

    /// <summary>Directories created by the launcher, deepest first.</summary>
    public IReadOnlyList<string> CreatedDirectories()
    {
        lock (_lock)
        {
            return Manifest.Directories
                .OrderByDescending(d => d.Length)
                .ToList();
        }
    }

    // ------------------------------------------------------------------ plumb #
    private string? StoreOriginal(string path)
    {
        try
        {
            Directory.CreateDirectory(OriginalsDirectory);
            var name = Fingerprint(path) + "-" + SafeName(Path.GetFileName(path));
            File.Copy(path, Path.Combine(OriginalsDirectory, name), overwrite: true);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RememberCreatedDirectories(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (!Manifest.Directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                Manifest.Directories.Add(directory);
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var temporary = ManifestPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Manifest, Json) + "\n",
                new UTF8Encoding(false));
            if (File.Exists(ManifestPath))
            {
                File.Replace(temporary, ManifestPath, null);
            }
            else
            {
                File.Move(temporary, ManifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The manifest is a convenience: an install that cannot record itself
            // still succeeds, and the semantic revert below covers it.
        }
    }

    private static InstallManifest ReadManifest(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new InstallManifest();
            }

            return JsonSerializer.Deserialize<InstallManifest>(
                File.ReadAllText(path, Encoding.UTF8), Json) ?? new InstallManifest();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new InstallManifest();
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stale copy behind is harmless.
        }
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length > 0 ? builder.ToString() : "file";
    }
}
