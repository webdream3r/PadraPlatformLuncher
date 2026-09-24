using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Padra.Core.Configuration;

/// <summary>
/// Persistent panel preferences, stored next to the application as
/// <c>launcher.settings.json</c>. A missing, truncated or hand-edited file
/// falls back to the defaults instead of breaking start-up.
/// </summary>
public sealed class UserSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();
    private SettingsData _data = new();

    private UserSettings(string path) => Path = path;

    public string Path { get; }

    public static UserSettings Load(string path)
    {
        var settings = new UserSettings(path);
        settings.Reload();
        return settings;
    }

    public void Reload()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SettingsData>(
                File.ReadAllText(Path, Encoding.UTF8), Json);
            if (parsed is not null)
            {
                lock (_lock)
                {
                    _data = parsed.Sanitised();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the defaults; a broken preferences file must not stop the app.
        }
    }

    public SettingsData Snapshot()
    {
        lock (_lock)
        {
            return _data.Clone();
        }
    }

    public string Culture => Snapshot().Culture;

    public void Update(Action<SettingsData> change)
    {
        lock (_lock)
        {
            change(_data);
            _data = _data.Sanitised();
        }

        Save();
    }

    private void Save()
    {
        SettingsData snapshot;
        lock (_lock)
        {
            snapshot = _data.Clone();
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json) + "\n",
                new UTF8Encoding(false));
            if (File.Exists(Path))
            {
                File.Replace(temporary, Path, null);
            }
            else
            {
                File.Move(temporary, Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are best-effort.
        }
    }
}

public sealed class SettingsData
{
    [JsonPropertyName("culture")] public string Culture { get; set; } = "en-US";

    [JsonPropertyName("projectFolder")] public string ProjectFolder { get; set; } = string.Empty;

    [JsonPropertyName("selectedAlias")] public string SelectedAlias { get; set; } = string.Empty;

    [JsonPropertyName("clientModel")] public string ClientModel { get; set; } = string.Empty;

    [JsonPropertyName("hiddenClientModels")]
    public List<string> HiddenClientModels { get; set; } = new();

    [JsonPropertyName("lastAgent")] public string LastAgent { get; set; } = string.Empty;

    [JsonPropertyName("installTargets")]
    public List<string> InstallTargets { get; set; } =
        ClientConfigTargets();

    /// <summary>
    /// Whether installing also makes the gateway what each agent reaches for
    /// without being told — the only way to steer the editor extensions, which
    /// expose no provider setting of their own.
    /// </summary>
    [JsonPropertyName("makeDefaultProvider")]
    public bool MakeDefaultProvider { get; set; } = true;

    /// <summary>
    /// Whether supported clients may run tools without interactive approval and
    /// without a sandbox. This is intentionally opt-in because it grants the
    /// configured agents the same access as the user running them.
    /// </summary>
    [JsonPropertyName("fullToolAccess")]
    public bool FullToolAccess { get; set; }

    public SettingsData Clone() => new()
    {
        Culture = Culture,
        ProjectFolder = ProjectFolder,
        SelectedAlias = SelectedAlias,
        ClientModel = ClientModel,
        HiddenClientModels = new List<string>(HiddenClientModels),
        LastAgent = LastAgent,
        InstallTargets = new List<string>(InstallTargets),
        MakeDefaultProvider = MakeDefaultProvider,
        FullToolAccess = FullToolAccess,
    };

    public SettingsData Sanitised()
    {
        var known = ClientConfigTargets();

        // An empty list is a choice — "configure none of them" — and has to survive a
        // round trip, so it is not treated as "unset" and refilled.
        var targets = (InstallTargets ?? new List<string>())
            .Where(t => known.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new SettingsData
        {
            Culture = string.IsNullOrWhiteSpace(Culture) ? "en-US" : Culture.Trim(),
            ProjectFolder = ProjectFolder ?? string.Empty,
            SelectedAlias = SelectedAlias ?? string.Empty,
            ClientModel = ClientModel ?? string.Empty,
            HiddenClientModels = (HiddenClientModels ?? new List<string>())
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LastAgent = LastAgent ?? string.Empty,
            InstallTargets = targets,
            MakeDefaultProvider = MakeDefaultProvider,
            FullToolAccess = FullToolAccess,
        };
    }

    private static List<string> ClientConfigTargets() =>
        Clients.ClientConfigWriter.AllTargets.Select(Clients.ClientConfigWriter.Slug).ToList();
}
