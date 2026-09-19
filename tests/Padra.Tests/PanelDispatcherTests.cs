using System.Text.RegularExpressions;
using Xunit;

namespace Padra.Tests;

/// <summary>
/// Blazor components must resume on their dispatcher. A single stray
/// <c>ConfigureAwait(false)</c> in component code makes the next
/// <c>StateHasChanged()</c> throw and terminates the circuit — the panel goes
/// dead and every button stops responding. This guards that regression.
/// </summary>
public sealed class PanelDispatcherTests
{
    /// <summary>The refresh loop deliberately runs off the dispatcher and re-enters
    /// through <c>InvokeAsync</c>; those awaits are the only allowed exceptions.</summary>
    private static readonly string[] Allowed =
    {
        "_timer.WaitForNextTickAsync(token)",
        "}).ConfigureAwait(false);",
        "_refreshLoop.CancelAsync()",
    };

    [Fact]
    public void Component_code_never_detaches_from_the_dispatcher()
    {
        var componentDirectory = Path.Combine(FindWebProject(), "Components");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentDirectory, "*.*",
                     SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) ||
                                 f.EndsWith(".razor", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimStart();
                if (!line.Contains("ConfigureAwait(false)", StringComparison.Ordinal) ||
                    line.StartsWith("//", StringComparison.Ordinal) ||
                    line.StartsWith("///", StringComparison.Ordinal) ||
                    line.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Allowed.Any(a => lines[index].Contains(a, StringComparison.Ordinal)))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "component code must stay on the dispatcher:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_panel_action_goes_through_the_shared_error_surface()
    {
        var source = File.ReadAllText(Path.Combine(FindWebProject(), "Components", "Pages",
            "Panel.razor.cs"));

        // Each user action returns RunAsync(...), which sets Busy, catches and
        // clears the error banner. A handler that skips it would show nothing on
        // failure and could leave the panel stuck.
        // Helpers called from inside a RunAsync body are already covered by it, and
        // the refresh-loop poll deliberately runs off the dispatcher without the
        // busy flag — it renders nothing itself.
        string[] helpers =
        {
            "RunAsync", "LoadLibraryAsync", "OnInitializedAsync", "UnloadIfRunningAsync",
            "PollUnslothAsync",
        };

        var handlers = Regex.Matches(source, @"private (?:async )?Task (\w+Async)\(")
            .Select(m => m.Groups[1].Value)
            .Where(name => !helpers.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.NotEmpty(handlers);
        foreach (var handler in handlers)
        {
            var body = Regex.Match(source,
                $@"private (?:async )?Task {handler}\([^)]*\)\s*=>\s*RunAsync|" +
                $@"private async Task {handler}\([^)]*\)\s*\{{[^}}]*RunAsync");
            Assert.True(body.Success, $"{handler} does not run through RunAsync");
        }
    }

    private static string FindWebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Padra.Web");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Padra.Web was not found");
    }
}
