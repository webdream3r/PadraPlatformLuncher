using System.Runtime.CompilerServices;
using Padra.Core.Configuration;
using Xunit;

namespace Padra.Tests;

/// <summary>
/// A throwaway HOME plus a temp working directory.
/// <para>
/// The client-config writers deliberately touch real per-user locations, so every
/// test that exercises them redirects those locations first. Environment
/// variables are process-wide, so tests that use this run in one non-parallel
/// collection.
/// </para>
/// </summary>
public sealed class TestEnvironment : IDisposable
{
    private readonly List<(string Name, string? Original)> _restore = new();

    public TestEnvironment([CallerMemberName] string name = "test")
    {
        Root = Path.Combine(Path.GetTempPath(), "padra-tests",
            $"{name}-{Guid.NewGuid():N}"[..Math.Min(64, name.Length + 33)]);
        Home = Path.Combine(Root, "home");
        Work = Path.Combine(Root, "work");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Work);

        Set("PADRA_HOME", Home);
        Set("HOME", Home);
        Set("USERPROFILE", Home);
        Set("APPDATA", Path.Combine(Home, "AppData", "Roaming"));
        Set("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));
        Set("XDG_CACHE_HOME", Path.Combine(Home, ".cache"));
        Set("HF_HUB_CACHE", Path.Combine(Home, ".cache", "huggingface", "hub"));
        Set("HF_HOME", null);
        Set("CODEX_HOME", null);
        Set("QWEN_HOME", null);
        Set("GATEWAY_API_KEY", null);
        Set("UNSLOTH_API_KEY", null);
        Set("UNSLOTH_STUDIO_AUTH_TOKEN", null);
        Set("UNSLOTH_STUDIO_URL", null);
        Set("UNSLOTH_STUDIO_HOME", null);
        Set("STUDIO_HOME", null);
        Set("UNSLOTH_BIN", null);
        Set("PATH", Path.Combine(Root, "bin"));
        Directory.CreateDirectory(Path.Combine(Root, "bin"));
    }

    public string Root { get; }

    public string Home { get; }

    public string Work { get; }

    public string ConfigPath => Path.Combine(Work, "models.yaml");

    public string Path_(params string[] parts) => System.IO.Path.Combine(
        new[] { Home }.Concat(parts).ToArray());

    public string VsCodeUserDirectory()
    {
        var directory = OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Home, "AppData", "Roaming", "Code", "User")
            : System.IO.Path.Combine(Home, ".config", "Code", "User");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public FleetOptions WriteFleet(Action<FleetOptions>? configure = null)
    {
        var options = FleetFile.Normalise(new RawFleet());
        options.Gateway.ApiKey = "sk-padra-test-key";
        options.Gateway.AutoImport = false;
        configure?.Invoke(options);
        FleetFile.Save(ConfigPath, options);
        return options;
    }

    private void Set(string name, string? value)
    {
        _restore.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, original) in _restore)
        {
            Environment.SetEnvironmentVariable(name, original);
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be locked on Windows; the temp folder is disposable.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

/// <summary>Tests that mutate process-wide environment variables share this collection.</summary>
[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection
{
}
