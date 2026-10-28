using Aster.Core.Jellyfin;
using Aster.Core.Plex;

namespace Aster.Core.Server;

public static class MediaServers
{
    /// <summary>The client for a saved sign-in, whichever server it's for.</summary>
    public static IMediaServer For(StoredSession session) => session.Kind == ServerKind.Plex
        ? new PlexClient(session.ServerUrl, session.AccessToken, session.UserId, session.ServerName)
        : new JellyfinClient(session.ServerUrl, session.AccessToken, session.UserId, kind: session.Kind);
}
