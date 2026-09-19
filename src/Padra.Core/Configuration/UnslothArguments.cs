using System.Globalization;

namespace Padra.Core.Configuration;

/// <summary>
/// What <c>unsloth run</c> lets a caller pass through, and what it refuses.
/// <para>
/// Unknown flags are forwarded to the inference server and override Unsloth's own
/// value, but a small set is reserved: model identity, the bind address, the API
/// key, TLS, the single-model web UI and the parallel-slot count. Passing one of
/// those makes the backend answer <c>400</c> after the process has already
/// started, which surfaces as an opaque start failure minutes later. Checking the
/// same list when the fleet file is read turns that into an immediate, precise
/// message on the field the user just edited.
/// </para>
/// </summary>
public static class UnslothArguments
{
    /// <summary>Flags Unsloth manages itself and rejects as extra arguments.</summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // model identity — the fleet file owns this
            "-m", "--model", "-a", "--alias", "-hf", "-hfr", "--hf-repo",
            "-hff", "--hf-file", "-mm", "--mmproj", "--gguf-variant",
            // bind address and routing — the gateway owns this
            "--host", "-H", "--port", "-p", "--path", "--api-prefix", "--reuse-port",
            // authentication and TLS
            "--api-key", "--api-key-name", "--ssl-key-file", "--ssl-cert-file",
            // slots and serving mode
            "-np", "--parallel", "--n-parallel",
            "--ui", "--webui", "--models-json", "--models-path",
            "--embedding", "--embeddings", "--rerank", "--pooling",
            // tool policy — `disableTools` owns this
            "--tools", "--enable-tools", "--disable-tools", "-ag", "--agent",
            "--mcp-servers", "--mcp-servers-file",
            // logging and CORS the gateway configures
            "--cors-allow-origin", "--cors-allow-origins", "--media-path",
            "--log-file", "--log-disable", "--slot-save-path",
            // meta
            "-h", "--help", "--version", "-V", "--list-devices",
        };

    /// <summary>
    /// Reject reserved flags and value-less arguments before they reach a process.
    /// </summary>
    /// <param name="arguments">The <c>extraArgs</c> list, in order.</param>
    /// <param name="label">Where the list came from, for the error message.</param>
    public static void Validate(IReadOnlyList<string> arguments, string label)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index]?.Trim() ?? string.Empty;
            if (argument.Length == 0)
            {
                throw new FleetFileException($"{label}[{index.ToString(CultureInfo.InvariantCulture)}] is empty");
            }

            if (argument.Any(char.IsControl))
            {
                throw new FleetFileException(
                    $"{label}[{index.ToString(CultureInfo.InvariantCulture)}] contains a control character");
            }

            if (!argument.StartsWith('-'))
            {
                continue;
            }

            // `--flag=value` is the same flag as `--flag value`.
            var name = argument.Split('=', 2)[0];
            if (Reserved.Contains(name))
            {
                throw new FleetFileException(
                    $"`{name}` is managed by the launcher and cannot be passed in {label}. " +
                    "Set it through the model's own fields instead.");
            }
        }
    }
}
