using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Padra.Core.Clients;
using Padra.Core.Configuration;
using Padra.Core.Discovery;
using Padra.Core.Runtime;
using Padra.Web.Components;
using Padra.Web.Endpoints;
using Padra.Web.Services;

namespace Padra.Web;

/// <summary>How a host instance should be started.</summary>
public sealed class PadraHostOptions
{
    public required string ConfigPath { get; init; }

    public string? HostOverride { get; init; }

    public int? PortOverride { get; init; }

    public FleetMode? ModeOverride { get; init; }

    public bool ForceLan { get; init; }

    public bool ForcePreload { get; init; }

    /// <summary>Mirror the activity log to this file. Set when there is no console.</summary>
    public string? LogFile { get; init; }
}

/// <summary>
/// Builds the gateway + control panel application. The desktop shell hosts this
/// in-process; <c>Program.cs</c> runs the same thing headless.
/// </summary>
public static class PadraHost
{
    public static readonly string[] SupportedCultures = { "en-US", "fa-IR" };

    public static WebApplication Build(PadraHostOptions options)
    {
        var configPath = Path.GetFullPath(options.ConfigPath);
        var log = new ActivityLog();
        if (!string.IsNullOrWhiteSpace(options.LogFile))
        {
            log.UseFile(options.LogFile!);
        }

        var fleetOptions = LoadFleet(configPath, log);
        ApplyOverrides(fleetOptions, options);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PadraHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.UseUrls(
            $"http://{Core.Networking.NetworkHosts.ForUrl(fleetOptions.Gateway.Host)}:{fleetOptions.Gateway.Port}");

        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton(UserSettings.Load(
            Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "launcher.settings.json")));
        builder.Services.AddSingleton(serviceProvider =>
            new ModelFleet(fleetOptions, configPath, serviceProvider.GetRequiredService<ActivityLog>()));
        builder.Services.AddSingleton<ModelLibrary>();
        builder.Services.AddSingleton<ShutdownSignal>();
        builder.Services.AddHostedService<IdleReaper>();

        builder.Services.AddHttpClient(ProxyForwarder.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
                Core.Networking.DirectHttp.CreateHandler(allowAutoRedirect: false))
            .ConfigureHttpClient(client =>
                client.Timeout = TimeSpan.FromSeconds(Math.Max(30, fleetOptions.Gateway.RequestTimeout)));

        // The catalog is the one client that really does leave this machine, so it
        // is also the one that keeps the system proxy.
        builder.Services.AddHttpClient<HuggingFaceCatalog>(client =>
            client.Timeout = TimeSpan.FromSeconds(30));

        // Probing Unsloth's own server must never hang the panel, and must never go
        // through a proxy: that server is on loopback or on the LAN, and a proxy
        // meant for the internet swallows both. Liveness is bounded separately, so a
        // slow model catalog cannot make a healthy server look unreachable.
        builder.Services.AddHttpClient(EndpointResolver.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Core.Networking.DirectHttp.CreateHandler())
            .ConfigureHttpClient(client => client.Timeout = UnslothServer.ProbeTimeout);

        // Loading a model through that server's API answers only once the weights
        // are resident; the caller bounds it with the fleet's start timeout instead.
        builder.Services.AddHttpClient(EndpointResolver.LongClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Core.Networking.DirectHttp.CreateHandler())
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<EndpointResolver>();

        // The .resx files are embedded as Padra.Web.UiStrings, i.e. the type name
        // itself, so no ResourcesPath prefix is configured.
        builder.Services.AddLocalization();
        builder.Services.Configure<RequestLocalizationOptions>(o =>
        {
            var cultures = SupportedCultures.Select(Services.CultureFormat.Build).ToList();
            o.DefaultRequestCulture = new RequestCulture(cultures[0]);
            o.SupportedCultures = cultures;
            o.SupportedUICultures = cultures;
            o.RequestCultureProviders = new List<IRequestCultureProvider>
            {
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider(),
            };
        });

        // The default encoder escapes every Persian character as a numeric entity,
        // which triples the page size and makes the markup unreadable. The renderer
        // resolves its HtmlEncoder from the container, so replace it here.
        builder.Services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(
            System.Text.Unicode.UnicodeRanges.All));

        builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("X-Unsloth-Backend", "X-Unsloth-Served-Model")));

        // Keep the data-protection keys beside the configuration: without this the
        // framework warns on every start and Blazor circuits break after a restart.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(
                Path.Combine(Path.GetDirectoryName(configPath) ?? ".", ".padra-keys")))
            .SetApplicationName("padra-platform-launcher");

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();

        // Touch the fleet so backends exist (and orphans are swept) before serving.
        var fleet = app.Services.GetRequiredService<ModelFleet>();
        var settings = app.Services.GetRequiredService<UserSettings>();
        var library = app.Services.GetRequiredService<ModelLibrary>();

        // Lets the fleet size a model against this machine before it starts one.
        fleet.WeightsSource = library.Weights;

        app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
        app.UseCors();
        app.UseMiddleware<GatewayAuthMiddleware>();
        app.UseStaticFiles();
        app.UseAntiforgery();

        // A browser cannot change its own culture cookie from an interactive circuit,
        // so the picker navigates here and comes straight back.
        app.MapGet("/culture", (HttpContext http, string culture, string? redirect) =>
        {
            if (SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
            {
                http.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions
                    {
                        IsEssential = true,
                        SameSite = SameSiteMode.Strict,
                        MaxAge = TimeSpan.FromDays(365),
                    });
                settings.Update(s => s.Culture = culture);
            }

            // Only a path on this panel may be bounced to: anything else would turn
            // the culture switch into an open redirect.
            var target = string.IsNullOrWhiteSpace(redirect) ? "/" : redirect!.Trim();
            if (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal) ||
                target.StartsWith("/\\", StringComparison.Ordinal))
            {
                target = "/";
            }

            return Results.LocalRedirect(target);
        });

        app.MapGatewayEndpoints();
        app.MapControlEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            log.Write($"mode={fleet.Gateway.Mode.ToString().ToLowerInvariant()} " +
                      $"default={(fleet.DefaultAlias.Length > 0 ? fleet.DefaultAlias : "—")} " +
                      $"models={(fleet.Backends.Count > 0 ? string.Join(", ", fleet.Backends.Select(b => b.Alias).OrderBy(a => a, StringComparer.Ordinal)) : "—")}");
            if (fleet.ApiKey.Length == 0)
            {
                log.Write("WARNING: no gateway API key is set — LAN clients will be refused");
            }

            if (fleet.Gateway.AllowLan || fleet.Gateway.IsShared)
            {
                log.Write("LAN endpoint: " + Core.Networking.NetworkInfo.PreferredLanUrl(
                    fleet.Gateway.Host, fleet.Gateway.Port));
            }

            log.Write($"listening on http://{Core.Networking.NetworkHosts.ForUrl(fleet.Gateway.Host)}:{fleet.Gateway.Port}/v1");

            // Warm the disk scan so the first load already knows what fits.
            _ = library.WarmAsync();

            if (fleet.Gateway.Preload && fleet.Backends.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await fleet.EnsureAsync(fleet.DefaultBackend()).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is BackendStartException or NoModelsConfiguredException)
                    {
                        log.Write($"preload failed: {ex.Message}");
                    }
                });
            }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            log.Write("stopping — unloading every model");
            fleet.ShutdownAsync().GetAwaiter().GetResult();
        });

        return app;
    }

    public static FleetOptions LoadFleet(string configPath, ActivityLog log)
    {
        if (!File.Exists(configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ".");
            File.WriteAllText(configPath, StarterConfig.Yaml);
            log.Write($"created {configPath}");
        }

        var options = FleetFile.Load(configPath);
        var changed = FleetFile.EnsureApiKey(options);
        if (changed)
        {
            log.Write($"a new gateway API key was generated and saved to {Path.GetFileName(configPath)}");
        }

        if (options.Models.Count == 0 && options.Gateway.AutoImport)
        {
            changed |= ModelFleet.AutoImport(options, log);
        }

        if (changed)
        {
            FleetFile.Save(configPath, options);
        }

        return options;
    }

    private static void ApplyOverrides(FleetOptions options, PadraHostOptions host)
    {
        if (host.ModeOverride is { } mode)
        {
            options.Gateway.Mode = mode;
        }

        if (host.ForcePreload)
        {
            options.Gateway.Preload = true;
        }

        if (host.ForceLan)
        {
            options.Gateway.Host = "0.0.0.0";
            options.Gateway.AllowLan = true;
        }

        if (!string.IsNullOrWhiteSpace(host.HostOverride))
        {
            options.Gateway.Host = host.HostOverride!.Trim();
            options.Gateway.AllowLan = options.Gateway.AllowLan || options.Gateway.IsShared;
        }

        if (host.PortOverride is { } port)
        {
            options.Gateway.Port = port;
        }
    }
}

/// <summary>Lets any endpoint ask the host to stop cleanly.</summary>
public sealed class ShutdownSignal(IHostApplicationLifetime lifetime, ActivityLog log)
{
    public void Request()
    {
        log.Write("shutdown requested");
        lifetime.StopApplication();
    }
}

/// <summary>Frees VRAM held by models nobody has used for <c>idleTimeout</c> seconds.</summary>
public sealed class IdleReaper(ModelFleet fleet, ActivityLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await fleet.ReapIdleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Write($"idle sweep failed: {ex.Message}");
            }
        }
    }
}
