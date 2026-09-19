using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Padra.Core.Configuration;
using Padra.Core.Runtime;

namespace Padra.Web.Endpoints;

/// <summary>
/// The OpenAI/Anthropic-compatible surface plus the admin routes scripts use.
/// </summary>
public static class GatewayEndpoints
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        // Claude Code sends this before its first request.
        app.MapMethods("/", new[] { "HEAD" }, () => Results.Ok());

        app.MapGet("/healthz", (ModelFleet fleet) => Results.Json(new
        {
            status = "ok",
            mode = fleet.Gateway.Mode.ToString().ToLowerInvariant(),
            default_model = fleet.DefaultAlias,
            models = fleet.Backends.Select(b => b.Alias).OrderBy(a => a, StringComparer.Ordinal).ToList(),
            loaded = fleet.Backends.Where(b => b.IsRunning).Select(b => b.Alias).ToList(),
        }));

        app.MapGet("/v1/models", (ModelFleet fleet) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return Results.Json(new
            {
                @object = "list",
                data = fleet.Backends.Select(b => new
                {
                    id = b.Alias,
                    @object = "model",
                    created = now,
                    owned_by = "unsloth",
                    root = b.Options.ModelSpec,
                    display_name = b.Alias,
                    type = "model",
                    context_length = b.Options.ContextLength,
                    max_input_tokens = b.Options.ContextLength,
                    loaded = b.IsRunning,
                }).ToList(),
            });
        });

        app.MapGet("/admin/status", (ModelFleet fleet) => Results.Json(StatusPayload(fleet)));

        app.MapPost("/admin/load/{alias}", async (string alias, ModelFleet fleet,
            CancellationToken token) =>
        {
            ModelBackend backend;
            try
            {
                backend = fleet.Resolve(alias, fallback: false);
            }
            catch (Exception ex) when (ex is UnknownModelException or NoModelsConfiguredException)
            {
                return Problem(HttpStatusCode.NotFound, ex.Message, "invalid_request_error");
            }

            try
            {
                await fleet.EnsureAsync(backend, token).ConfigureAwait(false);
            }
            catch (BackendStartException ex)
            {
                return Problem(HttpStatusCode.ServiceUnavailable, ex.Message,
                    "backend_start_error");
            }

            return Results.Json(backend.Status());
        });

        app.MapPost("/admin/unload/{alias}", async (string alias, ModelFleet fleet,
            CancellationToken token) =>
        {
            ModelBackend backend;
            try
            {
                backend = fleet.Resolve(alias, fallback: false);
            }
            catch (Exception ex) when (ex is UnknownModelException or NoModelsConfiguredException)
            {
                return Problem(HttpStatusCode.NotFound, ex.Message, "invalid_request_error");
            }

            await fleet.UnloadAsync(backend, token).ConfigureAwait(false);
            return Results.Json(backend.Status());
        });

        app.MapPost("/admin/mode/{mode}", async (string mode, ModelFleet fleet,
            CancellationToken token) =>
        {
            if (!Enum.TryParse<FleetMode>(mode, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                return Problem(HttpStatusCode.BadRequest,
                    "mode must be `exclusive` or `concurrent`", "invalid_request_error");
            }

            await fleet.SetModeAsync(parsed, token).ConfigureAwait(false);
            return Results.Json(new { mode = parsed.ToString().ToLowerInvariant() });
        });

        app.MapPost("/admin/reload", (ModelFleet fleet) =>
        {
            try
            {
                var stopped = fleet.Reload();
                return Results.Json(new
                {
                    ok = true,
                    stopped,
                    models = fleet.Backends.Select(b => b.Alias)
                        .OrderBy(a => a, StringComparer.Ordinal).ToList(),
                });
            }
            catch (FleetFileException ex)
            {
                return Problem(HttpStatusCode.BadRequest, ex.Message, "invalid_request_error");
            }
        });

        app.MapPost("/admin/shutdown", (ShutdownSignal shutdown) =>
        {
            shutdown.Request();
            return Results.Json(new { ok = true });
        });

        app.Map("/v1/{**path}", ProxyForwarder.ForwardAsync);
        return app;
    }

    public static object StatusPayload(ModelFleet fleet) => new
    {
        mode = fleet.Gateway.Mode.ToString().ToLowerInvariant(),
        default_model = fleet.DefaultAlias,
        backends = fleet.Backends.Select(b => b.Status()).ToList(),
    };

    public static IResult Problem(HttpStatusCode status, string message, string type,
        object? extra = null) =>
        Results.Json(new { error = Merge(message, type, extra) }, statusCode: (int)status);

    private static IDictionary<string, object?> Merge(string message, string type, object? extra)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = message,
            ["type"] = type,
        };

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                payload[property.Name] = property.GetValue(extra);
            }
        }

        return payload;
    }
}

/// <summary>Streams a request through to the backend that serves the requested model.</summary>
public static class ProxyForwarder
{
    public const string ClientName = "padra-upstream";

    private static readonly IReadOnlySet<string> HopRequestHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "host", "content-length", "connection", "keep-alive", "accept-encoding",
            "transfer-encoding", "authorization", "x-api-key", "proxy-authorization",
            "upgrade", "te", "trailer", "expect", "cookie",
        };

    private static readonly IReadOnlySet<string> HopResponseHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "content-length", "content-encoding", "transfer-encoding", "connection",
            "keep-alive", "upgrade", "trailer",
        };

    public static async Task ForwardAsync(HttpContext context, string path,
        ModelFleet fleet, IHttpClientFactory factory, ActivityLog log)
    {
        var route = path.Trim('/');
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            body = buffer.ToArray();
        }

        JsonObject? payload = null;
        if (body.Length > 0)
        {
            try
            {
                payload = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        // A client may send any JSON value here; only a string names a model, and
        // anything else must not become an unhandled 500 in the forwarder.
        var requested = payload?["model"] is System.Text.Json.Nodes.JsonValue node &&
                        node.TryGetValue<string>(out var name)
            ? name
            : null;

        ModelBackend backend;
        try
        {
            backend = fleet.Resolve(requested);
            await fleet.EnsureAsync(backend, context.RequestAborted).ConfigureAwait(false);
        }
        catch (NoModelsConfiguredException ex)
        {
            await WriteError(context, HttpStatusCode.ServiceUnavailable, ex.Message,
                "no_models_configured").ConfigureAwait(false);
            return;
        }
        catch (UnknownModelException ex)
        {
            await WriteError(context, HttpStatusCode.NotFound, ex.Message,
                "invalid_request_error", new { param = "model", available = ex.Available })
                .ConfigureAwait(false);
            return;
        }
        catch (BackendStartException ex)
        {
            await WriteError(context, HttpStatusCode.ServiceUnavailable, ex.Message,
                "backend_start_error").ConfigureAwait(false);
            return;
        }

        if (payload is not null && (payload.ContainsKey("model") || InferenceRoute(route)))
        {
            payload["model"] = backend.ServedId;
            body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        }

        // From here to the last byte of the response this backend is in use: the
        // idle reaper and the exclusive-mode swap must leave it alone.
        backend.BeginRequest();
        try
        {
            await SendUpstreamAsync(context, path, backend, factory, log, body).ConfigureAwait(false);
        }
        finally
        {
            backend.EndRequest();
        }
    }

    /// <summary>The forwarding itself, bracketed by the caller's in-flight tracking.</summary>
    private static async Task SendUpstreamAsync(HttpContext context, string path,
        ModelBackend backend, IHttpClientFactory factory, ActivityLog log, byte[] body)
    {
        var target = new UriBuilder($"{backend.BaseUrl}/v1/{path}")
        {
            Query = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value!.TrimStart('?')
                : string.Empty,
        }.Uri;

        using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (body.Length > 0)
        {
            upstream.Content = new ByteArrayContent(body);
            var contentType = context.Request.ContentType ?? "application/json";
            if (MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                upstream.Content.Headers.ContentType = parsed;
            }
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopRequestHeaders.Contains(header.Key) ||
                header.Key.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            upstream.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (!string.IsNullOrEmpty(backend.ApiKey))
        {
            upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backend.ApiKey);
            upstream.Headers.TryAddWithoutValidation("x-api-key", backend.ApiKey);
        }

        var client = factory.CreateClient(ClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !context.RequestAborted.IsCancellationRequested)
        {
            log.Write($"{backend.Alias}: upstream request failed: {ex.Message}");
            await WriteError(context, HttpStatusCode.BadGateway,
                $"Backend `{backend.Alias}` is unreachable at {backend.BaseUrl}: {ex.Message}",
                "upstream_error").ConfigureAwait(false);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response.Headers, context.Response);
            CopyHeaders(response.Content.Headers, context.Response);
            context.Response.Headers["X-Unsloth-Backend"] = backend.Alias;
            context.Response.Headers["X-Unsloth-Served-Model"] = backend.ServedId;
            context.Response.Headers.Remove("Content-Length");

            // Streaming matters: Claude Code aborts a response that arrives in one lump.
            var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            feature?.DisableBuffering();

            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted)
                .ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted)
                    .ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Routes where a <c>model</c> field is always meaningful.</summary>
    private static bool InferenceRoute(string route) =>
        route is "chat/completions" or "completions" or "responses" or "messages"
            or "messages/count_tokens";

    private static void CopyHeaders(HttpHeaders headers, HttpResponse response)
    {
        foreach (var header in headers)
        {
            if (HopResponseHeaders.Contains(header.Key))
            {
                continue;
            }

            response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static async Task WriteError(HttpContext context, HttpStatusCode status,
        string message, string type, object? extra = null)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = message,
            ["type"] = type,
        };

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                payload[property.Name] = property.GetValue(extra);
            }
        }

        await context.Response.WriteAsJsonAsync(new { error = payload }).ConfigureAwait(false);
    }
}
