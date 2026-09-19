using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Padra.Core.Configuration;
using Padra.Web;

namespace Padra.Tests;

/// <summary>A minimal stand-in for `unsloth run`: /v1/models plus a streaming endpoint.</summary>
public sealed class StubBackend : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubBackend(WebApplication app, int port, string servedId)
    {
        _app = app;
        Port = port;
        ServedId = servedId;
    }

    public int Port { get; }

    public string ServedId { get; }

    public List<StubRequest> Received { get; } = new();

    public static async Task<StubBackend> StartAsync(string servedId = "stub/model:Q4")
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        var backend = new StubBackend(app, port, servedId);

        app.MapGet("/v1/models", () => Results.Json(new
        {
            data = new[] { new { id = servedId } },
        }));

        app.MapPost("/v1/{**path}", async (HttpContext context, string path) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            backend.Received.Add(new StubRequest(
                path,
                raw.Length > 0 ? JsonNode.Parse(raw)!.AsObject() : new JsonObject(),
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase),
                context.Request.QueryString.Value ?? string.Empty));

            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"delta\":\"hi\"}\n\n");
            await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync("data: [DONE]\n\n");
        });

        await app.StartAsync();
        return backend;
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed record StubRequest(string Path, JsonObject Body,
    Dictionary<string, string> Headers, string Query);

/// <summary>A real gateway process-in-process, reachable over real HTTP.</summary>
public sealed class GatewayFixture : IAsyncDisposable
{
    public const string Key = "sk-padra-test-key";

    private readonly TestEnvironment _environment;
    private readonly WebApplication _app;

    private GatewayFixture(TestEnvironment environment, WebApplication app, StubBackend backend,
        int port)
    {
        _environment = environment;
        _app = app;
        Backend = backend;
        Origin = $"http://127.0.0.1:{port}";
        Client = new HttpClient { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(30) };
        Authorised = new HttpClient { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(30) };
        Authorised.DefaultRequestHeaders.Add("Authorization", $"Bearer {Key}");
    }

    public StubBackend Backend { get; }

    public string Origin { get; }

    public string ConfigPath => _environment.ConfigPath;

    /// <summary>No credentials: used to prove the endpoints that must refuse.</summary>
    public HttpClient Client { get; }

    public HttpClient Authorised { get; }

    public static async Task<GatewayFixture> StartAsync(
        Action<FleetOptions>? configure = null, bool secondModel = true, string? apiKey = Key)
    {
        var environment = new TestEnvironment("gateway");
        var backend = await StubBackend.StartAsync();
        var port = StubBackend.FreePort();

        environment.WriteFleet(options =>
        {
            options.Gateway.ApiKey = apiKey ?? string.Empty;
            options.Gateway.Port = port;
            options.Gateway.StartTimeout = 5;
            FleetFile.AddModel(options, "unsloth/Demo-GGUF", "Q4_K_M", port: backend.Port,
                contextLength: 4096);
            if (secondModel)
            {
                FleetFile.AddModel(options, "unsloth/Other-GGUF", "Q8_0",
                    port: StubBackend.FreePort());
            }

            configure?.Invoke(options);
        });

        var app = PadraHost.Build(new PadraHostOptions { ConfigPath = environment.ConfigPath });
        await app.StartAsync();
        return new GatewayFixture(environment, app, backend, port);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Authorised.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Backend.DisposeAsync();
        _environment.Dispose();
    }
}
