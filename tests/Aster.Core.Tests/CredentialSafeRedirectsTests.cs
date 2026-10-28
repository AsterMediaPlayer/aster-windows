using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Aster.Core.Jellyfin;
using Aster.Core.Net;
using Aster.Core.Plex;

namespace Aster.Core.Tests;

/// <summary>
/// A server (or anything between) redirecting a signed-in request to another
/// host mustn't get the sign-in sent there: .NET itself drops only the
/// standard Authorization header, not X-Emby-Authorization or X-Plex-Token.
/// Two local listeners on different ports stand for two origins.
/// </summary>
public class CredentialSafeRedirectsTests
{
    private const string Token = "dummy-token-1234";

    /// <summary>A listener answering every request with <paramref name="answer"/>, keeping the headers it got by path.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly HttpListener listener = new();
        public ConcurrentDictionary<string, WebHeaderCollection> Got { get; } = new();
        public ConcurrentDictionary<string, string> Bodies { get; } = new();
        public Uri Root { get; }

        public Listener(Action<HttpListenerContext> answer)
        {
            var port = FreePort();
            Root = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(Root.AbsoluteUri);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    Got[context.Request.Url!.AbsolutePath] = new WebHeaderCollection { context.Request.Headers };
                    using (var reader = new StreamReader(context.Request.InputStream)) Bodies[context.Request.Url.AbsolutePath] = await reader.ReadToEndAsync();
                    answer(context);
                    context.Response.Close();
                }
            });
        }

        public void Dispose() => listener.Close();

        private static int FreePort()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }
    }

    private static void Json(HttpListenerContext context, string json = "{}")
    {
        var data = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.OutputStream.Write(data);
    }

    /// <summary>The other host: answers, keeping what it was sent.</summary>
    private static Listener Elsewhere() => new(context => Json(context));

    /// <summary>The server: sends every request on to <paramref name="target"/>.</summary>
    private static Listener RedirectingTo(Func<Uri> target, HttpStatusCode status = HttpStatusCode.Found) => new(context =>
    {
        context.Response.StatusCode = (int)status;
        context.Response.RedirectLocation = target().AbsoluteUri;
    });

    private static readonly string[] Credentials = ["Authorization", "X-Emby-Authorization", "X-Plex-Token"];

    [Fact]
    public async Task JellyfinsSignInStaysOnTheServer()
    {
        using var elsewhere = new Listener(context => Json(context, """{"Id":"user","Name":"Someone"}"""));
        using var server = RedirectingTo(() => new Uri(elsewhere.Root, "landed"));
        var client = new JellyfinClient(server.Root, Token, "user");

        await client.MeAsync();

        var got = elsewhere.Got["/landed"];
        Assert.All(Credentials, header => Assert.Null(got[header]));
        Assert.DoesNotContain(Token, string.Join("\n", got.AllKeys.Select(k => got[k])));
    }

    [Fact]
    public async Task PlexsTokenStaysOnTheServer()
    {
        using var elsewhere = Elsewhere();
        using var server = RedirectingTo(() => new Uri(elsewhere.Root, "landed"));
        var client = new PlexClient(server.Root, Token, "user");

        await client.ItemIdsAsync(["1"], "Movie");

        var got = elsewhere.Got["/landed"];
        Assert.Null(got["X-Plex-Token"]);
        Assert.DoesNotContain(Token, string.Join("\n", got.AllKeys.Select(k => got[k])));
    }

    private static HttpClient SafeClient() => CredentialSafeRedirects.Client(new SocketsHttpHandler());

    [Fact]
    public async Task OtherHeadersGoAlong()
    {
        using var elsewhere = Elsewhere();
        using var server = RedirectingTo(() => new Uri(elsewhere.Root, "landed"));
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Root);
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", $"MediaBrowser Token=\"{Token}\"");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "device");

        using var response = await SafeClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(elsewhere.Got["/landed"]["X-Emby-Authorization"]);
        Assert.Equal("device", elsewhere.Got["/landed"]["X-Plex-Client-Identifier"]);
    }

    /// <summary>A redirect within the server (a moved path, a trailing slash) keeps the sign-in.</summary>
    [Fact]
    public async Task TheSameOriginKeepsTheSignIn()
    {
        Listener? server = null;
        server = new Listener(context =>
        {
            if (context.Request.Url!.AbsolutePath == "/moved")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MovedPermanently;
                context.Response.RedirectLocation = new Uri(server!.Root, "here").AbsoluteUri;
                return;
            }
            Json(context);
        });
        using (server)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Root, "moved"));
            request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
            using var response = await SafeClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Token, server.Got["/here"]["X-Plex-Token"]);
        }
    }

    /// <summary>307 keeps a POST and its body (without the sign-in, elsewhere); 302 makes it a GET.</summary>
    [Fact]
    public async Task ATemporaryRedirectKeepsThePost()
    {
        using var elsewhere = Elsewhere();
        using var server = RedirectingTo(() => new Uri(elsewhere.Root, "landed"), HttpStatusCode.TemporaryRedirect);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Root) { Content = new StringContent("hello") };
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", Token);

        using var response = await SafeClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", elsewhere.Bodies["/landed"]);
        Assert.Null(elsewhere.Got["/landed"]["X-Emby-Authorization"]);
    }

    [Theory]
    [InlineData("https://server:8920/a", "https://server:8920/b", true)]
    [InlineData("https://server:8920/a", "https://SERVER:8920/b", true)]
    [InlineData("https://server:8920/a", "http://server:8920/b", false)]
    [InlineData("https://server:8920/a", "https://server:8096/b", false)]
    [InlineData("https://server/a", "https://cdn.example.com/b", false)]
    public void WhatCountsAsTheSameOrigin(string from, string to, bool same) =>
        Assert.Equal(same, CredentialSafeRedirects.SameOrigin(new Uri(from), new Uri(to)));
}
