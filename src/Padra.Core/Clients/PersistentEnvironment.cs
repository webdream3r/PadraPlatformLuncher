using System.Text;

namespace Padra.Core.Clients;

/// <summary>
/// Puts a variable where an editor started from the desktop will see it.
/// <para>
/// The Codex VS Code extension spawns the Codex binary as a child of the
/// extension host, so it reads the key named by <c>env_key</c> from the editor's
/// own process environment. <c>terminal.integrated.env.*</c> never reaches that
/// process, and neither does a variable exported by the launcher for itself. The
/// only thing that does is the per-user environment: the Windows user
/// environment block, or a login shell profile on macOS and Linux.
/// </para>
/// <para>
/// Every change is recorded before it is made, so <see cref="Remove"/> puts the
/// machine back exactly as it was — including deleting a variable that did not
/// exist before.
/// </para>
/// </summary>
public static class PersistentEnvironment
{
    public const string BlockBegin = "# >>> padra-platform-launcher env >>>";
    public const string BlockEnd = "# <<< padra-platform-launcher env <<<";

    /// <summary>Profiles a login shell reads, in the order they are written.</summary>
    public static IReadOnlyList<string> ProfileFiles()
    {
        var home = Discovery.ModelScanner.UserHome();
        var files = new List<string> { Path.Combine(home, ".profile") };

        // Only touch a zsh profile the user already has: creating one changes which
        // startup file zsh reads, which is not this launcher's decision to make.
        var zprofile = Path.Combine(home, ".zprofile");
        if (File.Exists(zprofile))
        {
            files.Add(zprofile);
        }

        return files;
    }

    /// <summary>
    /// Persist <paramref name="values"/> for this user and record the previous state.
    /// </summary>
    /// <returns>The paths or scopes that were changed, for the install report.</returns>
    public static IReadOnlyList<string> Apply(
        string target,
        IReadOnlyDictionary<string, string> values,
        ClientInstallState state)
    {
        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var (name, value) in values)
            {
                var previous = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                state.CaptureEnvironment(target, name, EnvScope.WindowsUser, null, previous);
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

                // So the running launcher and anything it starts see it immediately.
                Environment.SetEnvironmentVariable(name, value);
            }

            return new[] { "the Windows user environment" };
        }

        var written = new List<string>();
        foreach (var file in ProfileFiles())
        {
            // The profile is captured whole, so the variable's own previous value does
            // not need storing separately: restoring the file restores it.
            state.Capture(target, file);
            foreach (var name in values.Keys)
            {
                state.CaptureEnvironment(target, name, EnvScope.ProfileFile, file, null);
            }

            WriteBlock(file, Block(values));
            written.Add(file);
        }

        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return written;
    }

    /// <summary>Undo <see cref="Apply"/> using what was recorded at install time.</summary>
    public static IReadOnlyList<string> Remove(
        IEnumerable<InstalledEnv> records, ClientInstallState state)
    {
        var touched = new List<string>();

        foreach (var record in records)
        {
            if (record.Scope == EnvScope.WindowsUser)
            {
                if (!OperatingSystem.IsWindows())
                {
                    continue;
                }

                Environment.SetEnvironmentVariable(record.Name,
                    record.Existed ? record.Previous : null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(record.Name,
                    record.Existed ? record.Previous : null);
                touched.Add("the Windows user environment");
                continue;
            }

            if (record.File is not { Length: > 0 } file)
            {
                continue;
            }

            // The profile itself is restored from its captured copy where one exists;
            // stripping the block covers a profile the launcher could not capture.
            if (!Restore(file, state))
            {
                WriteBlock(file, string.Empty);
            }

            Environment.SetEnvironmentVariable(record.Name, null);
            touched.Add(file);
        }

        return touched.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Restore(string file, ClientInstallState state)
    {
        var record = state.Manifest.Files.FirstOrDefault(f =>
            string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(file),
                StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return false;
        }

        try
        {
            if (!record.Existed)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return true;
            }

            if (state.OriginalBytes(record) is not { } bytes)
            {
                return false;
            }

            File.WriteAllBytes(file, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Block(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append("export ").Append(name).Append("='")
                .Append(value.Replace("'", "'\\''", StringComparison.Ordinal))
                .Append("'\n");
        }

        return builder.ToString();
    }

    /// <summary>Insert, replace or drop the managed block inside a shell profile.</summary>
    public static void WriteBlock(string path, string body)
    {
        var existing = string.Empty;
        try
        {
            if (File.Exists(path))
            {
                existing = File.ReadAllText(path, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var begin = existing.IndexOf(BlockBegin, StringComparison.Ordinal);
        var end = existing.IndexOf(BlockEnd, StringComparison.Ordinal);
        var head = begin >= 0 && end > begin ? existing[..begin].TrimEnd('\n') : existing.TrimEnd('\n');
        var tail = begin >= 0 && end > begin
            ? existing[(end + BlockEnd.Length)..].TrimStart('\n')
            : string.Empty;

        var builder = new StringBuilder();
        if (head.Trim().Length > 0)
        {
            builder.Append(head).Append("\n\n");
        }

        if (body.Trim().Length > 0)
        {
            builder.Append(BlockBegin).Append('\n').Append(body.TrimEnd('\n')).Append('\n')
                .Append(BlockEnd).Append('\n');
        }

        if (tail.Trim().Length > 0)
        {
            builder.Append(tail.TrimEnd('\n')).Append('\n');
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported by the caller through the install report.
        }
    }
}
