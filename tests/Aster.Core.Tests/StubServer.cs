using System.Net;
using System.Text;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>Answers a client's requests in-process, and records them.</summary>
sealed class StubServer : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri Url, string? Authorization, string? Body);

    private readonly Func<Request, (HttpStatusCode Status, string Body)> respond;

    public List<Request> Requests { get; } = [];

    public StubServer(Func<Request, (HttpStatusCode, string)> respond) => this.respond = respond;

    public JellyfinClient Client(string? token = "token", string? userId = "user", ServerKind kind = ServerKind.Jellyfin) =>
        new(new Uri("https://jellyfin.example/base/"), token, userId, new HttpClient(this), kind);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        var body = message.Content is null ? null : await message.Content.ReadAsStringAsync(ct);
        var request = new Request(
            message.Method,
            message.RequestUri!,
            message.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null,
            body);
        Requests.Add(request);
        var (status, text) = respond(request);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
