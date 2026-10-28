// Redirects that can't carry the sign-in elsewhere. .NET follows redirects
// itself and drops only the standard Authorization header when one goes to
// another host; custom headers such as X-Emby-Authorization and X-Plex-Token
// go along to wherever the server (or anything between) points. So the
// clients that send a sign-in follow redirects here instead: within the same
// origin with every header, to anywhere else without the credential headers.

using System.Net;

namespace Aster.Core.Net;

public sealed class CredentialSafeRedirects : DelegatingHandler
{
    /// <summary>The headers that carry a sign-in, never sent to another origin.</summary>
    public static readonly IReadOnlySet<string> CredentialHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "X-Emby-Authorization",
        "X-Emby-Token",
        "X-MediaBrowser-Token",
        "X-Plex-Token",
    };

    /// <summary>As .NET's own limit.</summary>
    public const int MaxRedirects = 10;

    public CredentialSafeRedirects(HttpMessageHandler inner) : base(inner) { }

    /// <summary>
    /// A client for requests that carry a sign-in: <paramref name="handler"/>'s
    /// own redirects turned off, this following them instead.
    /// </summary>
    public static HttpClient Client(SocketsHttpHandler handler)
    {
        handler.AllowAutoRedirect = false;
        return new HttpClient(new CredentialSafeRedirects(handler));
    }

    /// <summary>Same scheme, host and port: an https address sent to http, or another port, is another origin.</summary>
    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var origin = request.RequestUri ?? throw new InvalidOperationException("A request needs an address.");
        var current = request;
        var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
        for (var hops = 0; hops < MaxRedirects && IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hops++)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) break;
            var next = Follow(current, response.StatusCode, target, keepsCredentials: SameOrigin(origin, target));
            response.Dispose();
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = next;
            response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// The request to the next address. 301, 302 and 303 make a POST a GET
    /// (as browsers, and .NET, do), its body left behind; 307 and 308 keep
    /// the method and body.
    /// </summary>
    private static HttpRequestMessage Follow(HttpRequestMessage previous, HttpStatusCode status, Uri target, bool keepsCredentials)
    {
        var keepsMethod = status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            || (status != HttpStatusCode.SeeOther && previous.Method != HttpMethod.Post)
            || (status == HttpStatusCode.SeeOther && previous.Method == HttpMethod.Head);
        var next = new HttpRequestMessage(keepsMethod ? previous.Method : HttpMethod.Get, target)
        {
            Version = previous.Version,
            VersionPolicy = previous.VersionPolicy,
        };
        foreach (var (name, values) in previous.Headers)
        {
            if (!keepsCredentials && CredentialHeaders.Contains(name)) continue;
            next.Headers.TryAddWithoutValidation(name, values);
        }
        if (keepsMethod && previous.Content is { } content)
        {
            // Moved, not shared: the request left behind is disposed, and its body with it.
            next.Content = content;
            previous.Content = null;
        }
        return next;
    }
}
