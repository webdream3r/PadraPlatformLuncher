using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Padra.Core.Runtime;

namespace Padra.Core.Clients;

public enum CodingAgent
{
    Codex,
    Qwen,
    Claude,
}

public sealed record AgentAvailability(CodingAgent Agent, bool Installed, string Path);

public sealed record LaunchResult(bool Started, string ScriptPath, string CommandLine);

/// <summary>
/// Opens a terminal in a project folder with the gateway endpoint, key and model
/// already exported.
/// <para>
/// A script file is written instead of one long shell string, so folder names and
/// keys containing spaces or quotes cannot break the command line.
/// </para>
/// </summary>
public static class AgentLauncher
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string CommandFor(CodingAgent agent) => agent switch
    {
        CodingAgent.Codex => "codex",
        CodingAgent.Qwen => "qwen",
        CodingAgent.Claude => "claude",
        _ => throw new ArgumentOutOfRangeException(nameof(agent)),
    };

    public static string Slug(CodingAgent agent) => agent.ToString().ToLowerInvariant();

    public static bool TryParse(string value, out CodingAgent agent) =>
        Enum.TryParse(value.Trim(), ignoreCase: true, out agent) && Enum.IsDefined(agent);

    public static AgentAvailability Availability(CodingAgent agent)
    {
        var path = ExecutableFinder.Find(CommandFor(agent));
        return new AgentAvailability(agent, path is not null, path ?? string.Empty);
    }

    public static IReadOnlyList<AgentAvailability> AllAvailability() =>
        Enum.GetValues<CodingAgent>().Select(Availability).ToList();

    /// <summary>
    /// The environment each agent needs to reach the configured server.
    /// <para>
    /// The key goes under the variable the agent's own configuration names —
    /// <c>env_key</c> in <c>~/.codex/config.toml</c> reads
    /// <c>UNSLOTH_GATEWAY_KEY</c> against the gateway and
    /// <c>UNSLOTH_STUDIO_AUTH_TOKEN</c> against Unsloth's server — so a terminal
    /// opened here works even before the per-user variable has propagated.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Environment(
        CodingAgent agent, ClientContext context, string alias)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [context.KeyEnvName] = context.ApiKey,
        };

        if (agent == CodingAgent.Claude)
        {
            foreach (var (key, value) in ClientConfigWriter.ClaudeEnvironment(context))
            {
                env[key] = value;
            }

            env["ANTHROPIC_MODEL"] = alias;
        }
        else
        {
            env["OPENAI_API_KEY"] = context.ApiKey;
            env["OPENAI_BASE_URL"] = context.BaseUrl;
            env["OPENAI_MODEL"] = alias;
        }

        return env;
    }

    /// <summary>
    /// The command that opens the agent on one model. Codex selects a profile
    /// file, and against Unsloth's server every model shares the one profile named
    /// after the provider (a model id is a path, which cannot be a file name), so
    /// the model itself is passed on the command line there.
    /// </summary>
    public static IReadOnlyList<string> CommandLine(CodingAgent agent, string alias,
        ClientContext? context = null) => agent switch
    {
        CodingAgent.Codex => context is { SingleProfile: true }
            ? new[] { "codex", "--profile", context.ProviderId, "--model", alias }
            : new[] { "codex", "--profile", alias },
        CodingAgent.Qwen => new[] { "qwen", "--model", alias },
        CodingAgent.Claude => new[] { "claude", "--model", alias },
        _ => throw new ArgumentOutOfRangeException(nameof(agent)),
    };

    /// <summary>Open a terminal in <paramref name="folder"/> running the agent.</summary>
    public static LaunchResult Launch(
        CodingAgent agent,
        string alias,
        string folder,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> command,
        ActivityLog log)
    {
        // The window title reaches cmd.exe through a command line, and a console
        // running a legacy code page mangles anything outside ASCII there.
        var title = $"{Slug(agent)} - {alias}";
        var scripts = Path.Combine(Path.GetTempPath(), "padra-launcher");
        Directory.CreateDirectory(scripts);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (IsWindows)
        {
            var script = Path.Combine(scripts, $"{Slug(agent)}-{stamp}.cmd");
            // cmd.exe reads a batch file in the console's active code page, not
            // UTF-8, so a Persian profile path or a non-ASCII alias is corrupted
            // unless the script switches the console to UTF-8 on its first line.
            var body = new StringBuilder()
                .Append("@echo off\r\n")
                .Append("chcp 65001 >nul\r\n")
                .Append($"title {title}\r\n")
                .Append($"cd /d \"{folder}\"\r\n");
            foreach (var (key, value) in environment)
            {
                body.Append($"set \"{key}={value}\"\r\n");
            }

            body.Append(string.Join(' ', command.Select(QuoteWindows))).Append("\r\n");
            File.WriteAllText(script, body.ToString(), new UTF8Encoding(false));

            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("start");
            start.ArgumentList.Add(title);
            start.ArgumentList.Add("cmd");
            start.ArgumentList.Add("/k");
            start.ArgumentList.Add(script);
            Process.Start(start)?.Dispose();
            return new LaunchResult(true, script, string.Join(' ', command));
        }

        var shellScript = Path.Combine(scripts, $"{Slug(agent)}-{stamp}.sh");
        var shell = new StringBuilder()
            .Append("#!/usr/bin/env bash\n")
            .Append("set -e\n")
            .Append($"cd {QuotePosix(folder)}\n");
        foreach (var (key, value) in environment)
        {
            shell.Append($"export {key}={QuotePosix(value)}\n");
        }

        shell.Append(string.Join(' ', command.Select(QuotePosix))).Append('\n');
        File.WriteAllText(shellScript, shell.ToString(), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shellScript,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (IsMacOs)
        {
            var open = new ProcessStartInfo("open") { UseShellExecute = false };
            open.ArgumentList.Add("-a");
            open.ArgumentList.Add("Terminal");
            open.ArgumentList.Add(shellScript);
            Process.Start(open)?.Dispose();
            return new LaunchResult(true, shellScript, string.Join(' ', command));
        }

        string[][] terminals =
        {
            new[] { "x-terminal-emulator", "-e" },
            new[] { "gnome-terminal", "--" },
            new[] { "konsole", "-e" },
            new[] { "xfce4-terminal", "-e" },
            new[] { "alacritty", "-e" },
            new[] { "kitty" },
            new[] { "xterm", "-e" },
        };

        foreach (var terminal in terminals)
        {
            if (ExecutableFinder.Find(terminal[0]) is null)
            {
                continue;
            }

            var start = new ProcessStartInfo(terminal[0]) { UseShellExecute = false };
            foreach (var argument in terminal.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add("bash");
            start.ArgumentList.Add("-lc");
            start.ArgumentList.Add($"{QuotePosix(shellScript)}; exec bash");
            Process.Start(start)?.Dispose();
            return new LaunchResult(true, shellScript, string.Join(' ', command));
        }

        log.Write($"no terminal emulator was found; run manually: {shellScript}");
        return new LaunchResult(false, shellScript, string.Join(' ', command));
    }

    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string QuoteWindows(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
