using System.Net;
using Microsoft.AspNetCore.Http;
using Padra.Core.Networking;
using Padra.Core.Runtime;

namespace Padra.Web.Services;

/// <summary>
/// One authentication rule for the whole application.
/// <para>
/// API clients (<c>/v1</c>, <c>/admin</c>) present the gateway key as a bearer
/// token or <c>x-api-key</c>. A browser cannot add headers to a navigation, so
/// the control panel accepts the key once as <c>?key=…</c> and stores it in a
/// strict same-site cookie. Loopback callers are trusted, which keeps the
/// desktop window and local CLIs working with no key at all.
/// </para>
/// </summary>
public sealed class GatewayAuthMiddleware(RequestDelegate next)
{
    public const string CookieName = "padra_key";
    public const string QueryName = "key";

    public async Task InvokeAsync(HttpContext context, ModelFleet fleet)
    {
        var path = context.Request.Path;
        var key = fleet.ApiKey;

        // Liveness probes stay open: Claude Code sends HEAD / before anything else.
        if (path == "/healthz" || (HttpMethods.IsHead(context.Request.Method) && path == "/"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var loopback = IsLoopback(context);
        var isApiSurface = path.StartsWithSegments("/v1") || path.StartsWithSegments("/admin");

        if (key.Length == 0)
        {
            if (loopback)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            await WriteProblem(context, HttpStatusCode.Forbidden,
                "This launcher has no API key, so it only answers local clients.",
                "authentication_error").ConfigureAwait(false);
            return;
        }

        if (isApiSurface)
        {
            if (KeysMatch(HeaderToken(context), key))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            await WriteProblem(context, HttpStatusCode.Unauthorized, "Invalid API key.",
                "authentication_error").ConfigureAwait(false);
            return;
        }

        // Control panel and its API.
        if (loopback || KeysMatch(HeaderToken(context), key) ||
            KeysMatch(context.Request.Cookies[CookieName], key))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.Query.TryGetValue(QueryName, out var supplied) &&
            KeysMatch(supplied.ToString(), key))
        {
            context.Response.Cookies.Append(CookieName, key, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
            });

            var target = context.Request.Path + RemoveKey(context.Request.QueryString.Value);
            context.Response.Redirect(target.Length > 0 ? target : "/");
            return;
        }

        await WriteProblem(context, HttpStatusCode.Unauthorized,
            "Invalid API key. Open the panel with ?key=<gateway key> once to authorise this browser.",
            "authentication_error").ConfigureAwait(false);
    }

    public static bool IsLoopback(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return true;   // in-process calls (tests, the desktop shell)
        }

        return IPAddress.IsLoopback(remote) ||
               NetworkHosts.Loopback.Contains(remote.ToString());
    }

    /// <summary>Constant-time comparison, so a key cannot be guessed byte by byte.</summary>
    public static bool KeysMatch(string? supplied, string key)
    {
        if (supplied is null || supplied.Length == 0 || key.Length == 0)
        {
            return false;
        }

        var left = System.Text.Encoding.UTF8.GetBytes(supplied);
        var right = System.Text.Encoding.UTF8.GetBytes(key);
        return left.Length == right.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static string? HeaderToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var apiKey = context.Request.Headers["x-api-key"].ToString();
        return apiKey.Length > 0 ? apiKey.Trim() : null;
    }

    private static string RemoveKey(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var kept = QueryHelpersLite.Parse(query)
            .Where(pair => !string.Equals(pair.Key, QueryName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")
            .ToList();
        return kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty;
    }

    private static async Task WriteProblem(HttpContext context, HttpStatusCode status,
        string message, string type)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new { message, type },
        }).ConfigureAwait(false);
    }
}

internal static class QueryHelpersLite
{
    public static IEnumerable<KeyValuePair<string, string>> Parse(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            yield return index < 0
                ? new KeyValuePair<string, string>(Uri.UnescapeDataString(part), string.Empty)
                : new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(part[..index]),
                    Uri.UnescapeDataString(part[(index + 1)..]));
        }
    }
}
