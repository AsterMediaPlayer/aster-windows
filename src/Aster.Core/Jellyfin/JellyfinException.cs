namespace Aster.Core.Jellyfin;

public enum JellyfinErrorKind
{
    InvalidUrl,
    Http,
    NotSignedIn,
    Decoding,
}

/// <summary>
/// A failed Jellyfin request, with a message fit to show the user.
/// </summary>
public sealed class JellyfinException : Exception
{
    public JellyfinErrorKind Kind { get; }

    /// <summary>The HTTP status, for <see cref="JellyfinErrorKind.Http"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>The start of the server's response body, when it sent one.</summary>
    public string? ServerMessage { get; }

    private JellyfinException(JellyfinErrorKind kind, string message, int? statusCode = null, string? serverMessage = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        ServerMessage = serverMessage;
    }

    public static JellyfinException InvalidUrl() =>
        new(JellyfinErrorKind.InvalidUrl, "That server address is not valid.");

    public static JellyfinException Http(int code, string? message)
    {
        var text = code == 401 ? "Incorrect user name or password."
            : !string.IsNullOrEmpty(message) ? $"Server error {code}: {message}"
            : $"Server error {code}.";
        return new(JellyfinErrorKind.Http, text, code, message);
    }

    /// <summary>A failure another kind of server (Plex) explains in its own words.</summary>
    public static JellyfinException Explained(int? code, string message) =>
        new(JellyfinErrorKind.Http, message, code);

    public static JellyfinException NotSignedIn() =>
        new(JellyfinErrorKind.NotSignedIn, "You are not signed in.");

    public static JellyfinException Decoding(string detail, Exception? inner = null) =>
        new(JellyfinErrorKind.Decoding, $"Unexpected server response: {detail}", inner: inner);
}
