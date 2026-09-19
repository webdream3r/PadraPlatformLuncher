using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Padra.Web;
using Padra.Web.Services;
using Xunit;

namespace Padra.Tests;

/// <summary>
/// Every user-facing string is a resource key, and every key exists in every
/// language. These tests are what stops a new label from shipping English-only.
/// </summary>
public sealed class LocalizationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static readonly string[] DynamicPrefixes =
    {
        "unit.", "dialog.missing.", "source.", "client.", "mode.", "state.",
        "culture.", "btn.open_", "fit.", "toggle.", "gpumode.", "serve.", "hint.serve.",
    };

    [Fact]
    public void Both_languages_carry_the_same_keys()
    {
        var english = Keys("UiStrings.resx");
        var persian = Keys("UiStrings.fa-IR.resx");

        Assert.Equal(english.OrderBy(k => k, StringComparer.Ordinal),
            persian.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(english.Count > 100, $"only {english.Count} keys — did a file get truncated?");
    }

    [Fact]
    public void Placeholders_match_across_languages()
    {
        var english = Entries("UiStrings.resx");
        var persian = Entries("UiStrings.fa-IR.resx");
        var pattern = new Regex(@"\{(\d+)\}");

        foreach (var (key, value) in english)
        {
            var expected = pattern.Matches(value).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            var actual = pattern.Matches(persian[key]).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            Assert.True(expected.SetEquals(actual),
                $"{key}: english has {string.Join(",", expected)}, persian has {string.Join(",", actual)}");
        }
    }

    [Fact]
    public void No_translation_is_empty()
    {
        foreach (var file in new[] { "UiStrings.resx", "UiStrings.fa-IR.resx" })
        {
            foreach (var (key, value) in Entries(file))
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key} is empty");
            }
        }
    }

    [Fact]
    public void Every_key_the_panel_uses_exists()
    {
        var known = Keys("UiStrings.resx");
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(ProjectRoot, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) ||
                                  f.EndsWith(".cs", StringComparison.Ordinal)) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                     StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"L\[\s*""([a-z][\w.]*)""", RegexOptions.None))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);
        var missing = used.Except(known).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "keys used but not translated: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_dynamic_key_family_is_populated()
    {
        var known = Keys("UiStrings.resx");
        foreach (var prefix in DynamicPrefixes)
        {
            Assert.True(known.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)),
                $"no keys start with {prefix}");
        }

        // The families the panel indexes by enum value must be complete.
        foreach (var target in Padra.Core.Clients.ClientConfigWriter.AllTargets)
        {
            Assert.Contains($"client.{Padra.Core.Clients.ClientConfigWriter.Slug(target)}", known);
        }

        foreach (var agent in Enum.GetValues<Padra.Core.Clients.CodingAgent>())
        {
            var slug = Padra.Core.Clients.AgentLauncher.Slug(agent);
            Assert.Contains($"btn.open_{slug}", known);
            Assert.Contains($"dialog.missing.{slug}", known);
        }

        foreach (var source in Enum.GetValues<Padra.Core.Discovery.ModelSource>())
        {
            Assert.Contains($"source.{source.ToString().ToLowerInvariant()}", known);
        }

        foreach (var culture in PadraHost.SupportedCultures)
        {
            Assert.Contains($"culture.{culture}", known);
        }

        foreach (var verdict in Enum.GetValues<Padra.Core.Runtime.FitVerdict>())
        {
            Assert.Contains($"fit.{verdict.ToString().ToLowerInvariant()}", known);
        }

        foreach (var toggle in Enum.GetValues<Padra.Core.Configuration.Toggle>())
        {
            Assert.Contains($"toggle.{toggle.ToString().ToLowerInvariant()}", known);
        }

        foreach (var mode in Enum.GetValues<Padra.Core.Configuration.GpuMemoryMode>())
        {
            Assert.Contains($"gpumode.{mode.ToString().ToLowerInvariant()}", known);
        }

        foreach (var source in Enum.GetValues<Padra.Core.Configuration.EndpointSource>())
        {
            var slug = source.ToString().ToLowerInvariant();
            Assert.Contains($"serve.{slug}", known);
            Assert.Contains($"hint.serve.{slug}", known);
        }

        foreach (var type in Padra.Core.Configuration.RuntimeOptions.KvCacheTypes)
        {
            Assert.False(string.IsNullOrWhiteSpace(type), "an empty cache type would render blank");
        }
    }

    [Fact]
    public void The_markup_carries_no_hardcoded_visible_text()
    {
        foreach (var file in Directory.EnumerateFiles(ProjectRoot, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var tag in new[] { "h1", "h2", "h3", "legend", "caption" })
            {
                foreach (Match match in Regex.Matches(text, $"<{tag}\\b[^>]*>(.*?)</{tag}>",
                             RegexOptions.Singleline))
                {
                    var body = match.Groups[1].Value.Trim();
                    Assert.True(body.StartsWith("@", StringComparison.Ordinal),
                        $"{Path.GetFileName(file)}: <{tag}> has literal text: {body}");
                }
            }
        }
    }

    [Fact]
    public void The_stylesheet_uses_logical_properties_only()
    {
        var css = File.ReadAllText(Path.Combine(ProjectRoot, "wwwroot", "css", "panel.css"));

        Assert.Empty(Regex.Matches(css, @"(?<![-\w])(margin|padding|border)-(left|right)\s*:"));
        Assert.Empty(Regex.Matches(css, @"(?<![-\w])(left|right)\s*:\s*\d"));
        Assert.DoesNotContain("text-align: left", css, StringComparison.Ordinal);
        Assert.DoesNotContain("text-align: right", css, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- formatting #
    [Fact]
    public void Persian_uses_its_own_digits_and_the_jalali_calendar()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureFormat.Build("fa-IR");

            var number = CultureFormat.Number(262144);
            Assert.DoesNotContain('2', number);
            Assert.Contains('۲', number);

            var moment = new DateTimeOffset(2026, 8, 12, 10, 30, 0, TimeSpan.Zero);
            var formatted = CultureFormat.DateTime(moment);
            Assert.Contains("۱۴۰۵", formatted);   // the Jalali year, not 2026
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void English_keeps_latin_digits_and_the_gregorian_calendar()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureFormat.Build("en-US");

            Assert.Equal("262,144", CultureFormat.Number(262144));
            Assert.Contains("2026",
                CultureFormat.DateTime(new DateTimeOffset(2026, 8, 12, 10, 30, 0, TimeSpan.Zero)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(0, "unit.byte")]
    [InlineData(2048, "unit.kib")]
    [InlineData(5L * 1024 * 1024, "unit.mib")]
    [InlineData(7L * 1024 * 1024 * 1024, "unit.gib")]
    public void Sizes_pick_the_right_unit(long bytes, string expectedKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        using var provider = services.BuildServiceProvider();
        var localizer = provider.GetRequiredService<IStringLocalizer<UiStrings>>();

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureFormat.Build("en-US");
            var expected = localizer[expectedKey, "X"].Value.Replace("X", string.Empty,
                StringComparison.Ordinal).Trim();
            Assert.EndsWith(expected, CultureFormat.Bytes(bytes, localizer), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // ------------------------------------------------------------ fixtures #
    private static string FindProjectRoot()
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

        throw new DirectoryNotFoundException("src/Padra.Web was not found above the test output");
    }

    private static Dictionary<string, string> Entries(string file) =>
        XDocument.Load(Path.Combine(ProjectRoot, file))
            .Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value,
                StringComparer.Ordinal);

    private static HashSet<string> Keys(string file) =>
        Entries(file).Keys.ToHashSet(StringComparer.Ordinal);
}
