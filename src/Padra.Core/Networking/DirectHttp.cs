namespace Padra.Core.Networking;

/// <summary>
/// HTTP clients for the model servers this launcher talks to.
/// <para>
/// Those servers are this machine's own backends or an Unsloth server on the local
/// network, and .NET reads <c>HTTP_PROXY</c>/<c>NO_PROXY</c> in preference to
/// Windows' own bypass list. A proxy meant for the open internet therefore swallows
/// a request to <c>192.168.x.x</c>, and the server is reported unreachable when it
/// is answering perfectly well. Nothing built here goes through a proxy. Traffic
/// that really does leave the machine — the Hugging Face catalog — keeps the
/// default one.
/// </para>
/// </summary>
public static class DirectHttp
{
    /// <summary>A handler that ignores every proxy setting, system or environment.</summary>
    public static HttpClientHandler CreateHandler(bool allowAutoRedirect = true) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        UseProxy = false,
        Proxy = null,
    };

    /// <summary>A proxy-free client bounded by <paramref name="timeout"/>.</summary>
    public static HttpClient CreateClient(TimeSpan timeout) =>
        new(CreateHandler(), disposeHandler: true) { Timeout = timeout };
}
