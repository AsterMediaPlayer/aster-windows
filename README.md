<p align="center">
  <img src="docs/screenshots/home.jpg" alt="Aster Media Player Home screen" width="100%">
</p>

# Aster Media Player for Windows

A client for Jellyfin, Emby and Plex on Windows, made for the couch: a game controller works everywhere, and it runs full screen from Steam Big Picture. Written in C# with Avalonia, as a port of the [Apple app](https://github.com/AsterMediaPlayer/aster-apple). Aster for Linux has its own home, at [aster-linux](https://github.com/AsterMediaPlayer/aster-linux).

Aster covers movies, TV, anime, music, audiobooks and live TV, and can turn your playlists into TV-style channels. Title pages are laid out like the Apple TV app, with theme music, theme videos and trailers, and Aster plays DVD/Blu-ray-style disc menus. Videos play in **mpv**, from the original file, with every audio and subtitle track switchable on the spot.

With Aster's server plugin you can discover and request new titles through Seerr, and keep Simkl, AniList, MyAnimeList and others up to date. With Bonfire profiles or a Plex Home, a household shares one account, with a profile and a PIN each, and a lock. Emby and Plex servers work too: see [Servers](#servers) for what each one gets.

> Aster is in beta (0.11). Expect rough edges, and please report what you find (Settings ▸ About ▸ Send Debug Report helps).

## Get Aster

Aster Media Player is free, and always will be: on every platform, with every feature, and no paid version. It's open source. Download the latest from [Releases](https://github.com/AsterMediaPlayer/aster-windows/releases), for Windows 10 (1809) or later, 64-bit:

- **Installer:** `…-windows-x64-setup.exe` installs it for you alone (no administrator needed), with a Start menu shortcut.
- **Portable:** the `.zip` runs from any folder.
- **Build it yourself:** see [Building](#building) below.

The app isn't signed yet, so SmartScreen may warn the first time: choose **More info → Run anyway**. Trailers play in the app with WebView2. Updating or uninstalling keeps your sign-ins and settings.

## Screenshots

Full screen at 1920×1080, from a demo Jellyfin server of openly licensed films, shows and music.

| | |
| --- | --- |
| ![Home](docs/screenshots/home.jpg) | ![Player](docs/screenshots/player.jpg) |
| **Home**: featured banner, Continue Watching, Up Next, recently added | **Player**: mpv plays the original file, with every audio and subtitle track |
| ![Music](docs/screenshots/music.jpg) | ![Disc menu](docs/screenshots/disc-menu.jpg) |
| **Music**: albums, artists, lyrics, Now Playing | **Disc menus**: menus from the Aster Disc plugin, with transitions and sounds |
| ![Channels](docs/screenshots/channels.jpg) | ![Channel row](docs/screenshots/channel-row.jpg) |
| **Channels**: playlists as TV channels, movies and shows in the same guide | **Channel row**: press Up while watching to flick through channels, or Go to Episode |

## Platforms

| | Desktop | Steam Big Picture |
| --- | :---: | :---: |
| Browsing, title pages, search, Live TV, Channels, music and audiobooks | ✓ | ✓ |
| mpv player, with hardware decoding (D3D11) | ✓ | ✓ |
| Game controllers, everywhere in the app | ✓ | ✓ |
| Full screen for good, sized for the TV | | ✓ |
| Windows' media controls (the overlay by the volume flyout, and the lock screen) | ✓ | ✓ |
| Trailers in the app (WebView2) | ✓ | ✓ |
| Surround sound to a receiver (Dolby, DTS, Atmos), and Sonos for music | ✓ | ✓ |

Downloads for x64: an installer and a portable zip.

## Features

### Watching
- **Home**: a featured banner, then shelves for Continue Watching, Up Next, recently added Movies, TV Shows and Anime, and one shelf per library. Play or Resume on the banner starts the title straight away, and Back lands on its page. Home shows last time's at once, then the server's.
- **Title pages**: theme music and muted, looping theme videos (episodes and seasons share their show's), and the trailer, in YouTube's own player in the app. A show's page has tabs for Episodes, Music, Extras, Related and Details; an episode's lists the rest of its season.
- **Scores**: the server's Rotten Tomatoes and community scores, with IMDb, Rotten Tomatoes' audience score, Metacritic, Letterboxd and TMDB from MDBList if you add an MDBList API key (Settings ▸ Title Pages ▸ Ratings).
- **Libraries**: sort by name, date added, release date or rating, with an A–Z index down the side. A library of thousands scrolls smoothly.
- **Search**: movies, shows, episodes and music in one place, with a keyboard on the screen for the couch.
- **Upcoming**: episodes airing soon from the shows in your library, day by day.
- **Up Next**: at the end of an episode an Up Next card counts down to the next one. Skip Intro, Skip Credits and Play Next Episode come from the server's media segments, and intros can be skipped automatically.
- **Playlists**: Add to Playlist from any title, into a playlist you have or a new one. Video playlists have Play All and Shuffle, and you can reorder or remove items.

### The player
- **mpv** plays the original file, so nothing is transcoded unless it has to be: every audio and subtitle track switches instantly, PGS and VobSub picture subtitles are drawn as they are, and ASS/SSA keeps its styling and fonts.
- **When the file is too much:** over the quality limit, or with a lower quality picked from the Quality menu (or the card that comes up when playback keeps stalling), the server converts it. A stream that fails steps down by itself: the server's own choice, then a plain H.264 encode.
- **Controls:** Space or K to pause, ← → to skip 10 seconds, ↑ ↓ for volume, A and S for the next audio and subtitle track, F for full screen, and Fill Screen to zoom a wide film to the edges. A controller's buttons do the same, and Y sends a video to a mini player in the corner while you browse.
- **Chapters and previews:** chapters are marked on the timeline and listed in a menu, and scrubbing shows the frame, from Jellyfin's trickplay images or Emby's BIF previews.
- **Pause screen:** the title's logo and details, with a long description scrolling slowly; on a channel, what's on and what's next. A video also pauses when Aster goes behind another window.
- **System media controls:** the video, song or book playing shows in Windows' media overlay (by the volume flyout, and on the lock screen), and its buttons and a Bluetooth headset's work it.
- **Sound:** Dolby and DTS (Atmos and DTS:X included) pass through to a receiver or soundbar as they are. Or even out the volume, or mix surround down to stereo with the dialogue clear.

### Subtitles
- **ASS/SSA** drawn as the release intended: signs where they belong, drawings, and the fonts embedded in the file. Turn it off for plain text in Settings ▸ Video ▸ Audio & Subtitles.
- **Subtitle style** for plain subtitles: size, colour, and an outline or a dark box behind them.

### Anime
- **Anime tab:** libraries matched by Shoko/AniDB get their own tab and are left out of Movies and TV Shows. Your music video libraries (AMVs) can have shelves there too.
- **Openings and endings:** a show's Music tab has its openings and endings, to watch on their own or play back to back in a Now Playing panel, with Watch OP and Watch ED on its page. They come from the Aster OP-ED plugin, in the TV size, full length or as a music video, or from chapter markers without it.
- **Song cards:** while an opening or ending plays, a card shows the song, artists and series. Without the plugin (and on Emby and Plex), the song and artists come from [AnimeThemes](https://animethemes.moe), kept on your computer once found; Settings ▸ Anime can turn the lookups off.

### Music
- **Library:** albums, artists, playlists and songs, with Shuffle and Instant Mix on albums and playlists.
- **The player:** shuffle, repeat (all or one), synced lyrics, Up Next, and a sleep timer.
- **Gapless playback**, on by default, and a streaming quality of its own (Settings ▸ Music).
- **Save upcoming songs:** the next five songs are kept on your computer as you listen, so music plays on through a dropped connection. They're deleted once heard.
- **Sonos:** play music and audiobooks on Sonos rooms, grouped to play in sync, from Play On in the player.

### Audiobooks
- **Books from your files:** a library's files are put together into books, with authors and series taken from the folders (`Author/Series/Book/`).
- **Listening:** chapters, skip back 15 seconds or ahead 30, speeds remembered per book, a sleep timer, and progress synced with your server.
- **Back to the player from anywhere:** a bar at the foot of the sidebar while a song or book plays, and the keyboard's media keys.

### Live TV
- What's on now, a programme guide, a channel list, and shelves of what's on in movies, sports, kids and news, plus your favourite channels.
- **Recordings:** record a programme, or set series rules (which episodes, how many to keep, and padding), and delete a recording from its page.
- **Changing channels:** press Up (on the keyboard or a controller) for the channel row and go left or right; Enter tunes. Page Up and Page Down change channel straight away.
- **A mini player** keeps the channel playing in the header after you leave the player.
- **Channel groups:** with the Aster Channels plugin, channels are grouped into sports, news, kids and more, in the guide and as you flip.

### Channels
Channels turn your own movies and shows into TV you can just put on. Each channel runs around the clock on a schedule, with a guide like Live TV's. Tune in and you join whatever's on, partway through, the way real TV does, and every device, Apple TV, Mac or PC, shows the same thing at the same moment.

**Make your first channel** (you need to be a server administrator): open the **Channels** tab (or **Settings ▸ Channels**) and choose **Edit Channels**, then under **New Channel** pick **From a Show**, and a show. That's it: the channel is playing. You can also make a channel from a genre, a network, a collection, or one of your playlists, add commercial breaks, and choose how it picks what to play (Rotation, Blocks, Shuffle, In Order, Air Date). The [Apple app's README](https://github.com/AsterMediaPlayer/aster-apple#channels) walks through it all, and it works the same here; the tags are listed in [docs/channel-tags.md](https://github.com/AsterMediaPlayer/aster-apple/blob/main/docs/channel-tags.md).

Watching a channel doesn't change resume points, watched marks or Continue Watching. To watch something properly, press Up for the channel row, then Up again for **Go to Episode**.

### Disc menus
- Aster renders DVD/Blu-ray-style menus from the **Aster Disc** plugin (menus in the JellyDisc format): moving backgrounds, chapter and trailer screens, theme songs, and fade, slide, rise, zoom or wipe transitions, with menu music and sounds.
- A title with a menu has a Disc Menu button, and Settings ▸ Video ▸ Disc Menus can open the menu when you press Play, like putting in the disc.
- The menu rules are checked against the plugin's published conformance vectors.

### Discover and requests
With the **Aster Discovery and Tracking** plugin on your Jellyfin server, the Discover tab has rows the server builds for you (what to carry on with, recommendations, "because you watched", your watchlist, trending, new seasons), and, when the server is linked to [Seerr](https://github.com/seerr-team/seerr), Movies, Series and Requests: browse with filters, open a title to see its seasons, cast and trailer, and **Request** it (a season at a time, or in 4K where the server has it). Aster never signs in to Seerr itself: everything goes through the plugin, as you.

### Tracking and watchlists
- **Through the server** (with the Aster Discovery and Tracking plugin): link Simkl, AniList, MyAnimeList, Last.fm, ListenBrainz and Hardcover once in Settings ▸ Tracking, and the server sends what you watch and listen to from then on, from every app. Each service has a switch and a list of what was sent lately.
- **Add to Watchlist** on title pages, Seerr's pages and Discover: Simkl for anything, and AniList and MyAnimeList for anime.

### Household profiles
For a household on one account: profiles from the [Bonfire](https://github.com/AHouseOfBards/Bonfire-JellyProfiles) plugin on Jellyfin, or a Plex Home.
- **Who's watching?** Pick a profile from the picture at the top of the sidebar, or Settings ▸ Account ▸ Switch Profile. A profile with a PIN asks for it on a PIN pad you can use with a controller.
- **Asking as Aster opens:** Settings ▸ Account ▸ Ask on Opening: As the Server Is Set (Bonfire's own "Ask on startup"), Every Time or Never. A profile with a PIN always asks for it as Aster opens.
- **Adding profiles** (Bonfire, from the main account): Add Profile on the picker, with a name, a picture, a PIN (which can be skipped on the home network), a viewing limit and the libraries it sees.
- **PIN lock:** after five minutes away from Aster or with nothing pressed (Settings ▸ Account ▸ Ask for PIN After, up to an hour, or never), the profile's PIN is asked again before anything plays. A video playing doesn't count as idle. Anyone else at the lock can pick their own profile.

### Couch and Steam
- **Game controllers** drive the whole app (Xbox, PlayStation, Switch Pro, 8BitDo and most others): the d-pad or left stick moves, A opens, B goes back, X plays or pauses, Y sends a video to the mini player, and the shoulder buttons skip songs, chapters or channels. A Nintendo-style layout swaps A and B, X and Y (Settings ▸ General).
- **Steam:** add Aster as a non-Steam game. In Big Picture it runs full screen for good, sized for the TV.
- **The keyboard** works like a TV remote: the arrows move, Enter chooses, Esc goes back, Ctrl+F searches.

### Everywhere
- **Sign-in:** password or Quick Connect on Jellyfin, password on Emby, and a plex.tv link code on Plex (then a Plex Home profile, with its PIN, and a server). Several servers and users can be saved, each can ask for its password or PIN before switching to it, and each can lock after a while away. Sign-ins are kept in Windows Credential Manager.
- **Welcome:** the first time on a Jellyfin server, a few cards show what Aster does, and a server administrator can install Aster's companion plugins from there.
- **Settings that follow you** (Jellyfin and Emby): Up Next, skipping intros, styled subtitles, disc menus, theme music and videos, the opening sound and song cards are kept on your server, so they match on every device, the Apple app included.
- **Server plugins** (Settings ▸ Server Plugins, Jellyfin): see which of Aster's plugins your server has; an administrator can install them, turn them on or off, and restart the server.
- **Links:** `aster://item/{id}` opens a title page, `aster://play/{id}` plays it and `aster://channel/{id}` tunes a channel, in the Aster already open.
- **Debug reports:** Settings ▸ About ▸ Send Debug Report sends what happened, with Aster's recent log, settings and last crash, and gives you a report number to quote in a GitHub issue. Passwords, tokens, addresses, names and file paths are taken out first. Aster offers to send one after it closes unexpectedly.

## Servers

| | Jellyfin | Emby | Plex |
| --- | :---: | :---: | :---: |
| Home, libraries, title pages, search | ✓ | ✓ | ✓ |
| Playback with resume and watched | ✓ | ✓ | ✓ |
| Skip Intro and Skip Credits | ✓ | ✓ (chapter markers) | ✓ (Plex Pass markers, or chapters) |
| Anime tab and song cards | ✓ | ✓ | ✓ |
| Channels | ✓ | ✓ | ✓ (tags in the playlist's description) |
| Household profiles, with PINs and a PIN lock | ✓ (Bonfire) | | ✓ (Plex Home) |
| Music, audiobooks, Live TV, playlists, Upcoming | ✓ | ✓ | |
| Favourites, scrubbing previews, lyrics, settings that follow you | ✓ | ✓ | |
| Quick Connect, disc menus, Discover, Seerr, tracking | ✓ | | |

### Jellyfin

Aster works with a recent Jellyfin server. All plugins are optional, and features that depend on them turn on when they're present:

| Plugin | Enables |
| --- | --- |
| Aster Channels | Live TV channel groups, and editing Aster's channels from Jellyfin's dashboard |
| Aster Disc | Disc menus |
| Aster OP-ED | Anime openings and endings with song details |
| Aster Discovery and Tracking | The Discover tab, Seerr requests, tracking through the server, Add to Watchlist |
| [Bonfire](https://github.com/AHouseOfBards/Bonfire-JellyProfiles) (JellyProfiles) | Household profiles: switching, PINs, adding profiles |
| Intro Skipper (or chapters / EDL) | Skip Intro and Skip Credits |
| Shoko | Detecting Anime libraries |

Aster's own plugins are published in the [Aster Companion Plugins](https://github.com/AsterMediaPlayer/aster-companion-plugins) repository, which Aster can add to your server for you. Theme songs and theme videos use Jellyfin's standard theme media.

### Emby and Plex

- **Emby:** sign in with the server's address and your password, as on Jellyfin. Live TV needs Emby Premiere on the server; scrubbing previews need the library's thumbnail extraction turned on.
- **Plex:** choose **Sign in with Plex** and enter the code shown at plex.tv/link, then pick a Plex Home profile and a server. Intro and credits markers need Plex Pass on the server; without them, chapters named Opening and Ending are used. Away from home, the server needs Remote Access turned on, and Aster finds it again when its address changes.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`). The first build downloads libmpv (about 120 MB) into `native/` with `tools/fetch-libmpv.ps1`, pinned to one build and checked against its hash.

```powershell
git clone https://github.com/AsterMediaPlayer/aster-windows.git
cd aster-windows
dotnet run --project src/Aster.Desktop
dotnet test
```

A self-contained build (no .NET needed where it runs) is `dotnet publish src/Aster.Desktop -c Release -r win-x64 --self-contained -o publish/win-x64`.

A release is made by pushing a version tag (`git tag v0.11.0-beta`, then `git push origin v0.11.0-beta`): the Release workflow builds the installer (`installer/aster.iss`, with Inno Setup) and the zip, and publishes them, as a pre-release when the version has a suffix. Run by hand from the Actions tab, it only builds them.

How the app is put together screen by screen, and the switches for trying it against a server, are in [docs/development.md](docs/development.md).

### Project layout

| Folder | Contents |
| --- | --- |
| `src/Aster.Core` | The server-neutral `IMediaServer` every screen asks; the Jellyfin and Emby client, the Plex client and plex.tv sign-in; models, settings and their sync; household profiles and the Bonfire client; channels, disc menus, anime openings and endings, audiobooks, Seerr and tracking, ratings, debug reports |
| `src/Aster.Mpv` | Bindings for libmpv: the player, its events, and OpenGL rendering into the app |
| `src/Aster.Desktop` | The Avalonia app: every screen, the video player, the music, audiobook and theme players, Sonos, game controllers, the system's media controls |
| `tests/Aster.Core.Tests` | Unit tests, the disc menu and channel tag conformance vectors, and libmpv against a clip made by ffmpeg |
| `installer` | The Windows installer (Inno Setup) |
| `tools` | Fetching libmpv for the build, and making the bundled icon font |

## Known limitations

- **HDR** is tone-mapped to SDR: the video is drawn through OpenGL so the controls can sit on top of it.
- **No downloads** for watching or listening offline yet.
- **Chinese titles** sort under # in the A–Z index: .NET has no pinyin table.
- **x64 only:** there's no ARM64 download yet.
- **Profiles:** a profile's picture is one of Bonfire's own, not one of yours yet. Plex Home profiles are switched between here, but not added or changed: do that at plex.tv.
- **Anime without the OP-ED plugin:** openings and endings are found from chapter names, and their songs from AnimeThemes by the show's name and year, which can miss or pick the wrong season.
- **Plex:** music, audiobooks, Live TV, playlists, Upcoming, favourites and scrubbing previews aren't built yet, so they're hidden on Plex.

## Open-source components

| Component | License |
| --- | --- |
| [mpv](https://mpv.io) (shinchiro's build of libmpv, included) | GPL-2.0-or-later / LGPL-2.1-or-later |
| ↳ [FFmpeg](https://ffmpeg.org), [libass](https://github.com/libass/libass), [libplacebo](https://code.videolan.org/videolan/libplacebo) | LGPL-2.1 / GPL (FFmpeg), ISC (libass), LGPL-2.1 (libplacebo) |
| [Avalonia](https://avaloniaui.net), [SkiaSharp and HarfBuzzSharp](https://github.com/mono/SkiaSharp) | MIT |
| [ANGLE](https://angleproject.org) | BSD-3-Clause |
| [.NET](https://dotnet.microsoft.com) | MIT |
| [SDL](https://libsdl.org) and [SDL3-CS](https://github.com/ppy/SDL3-CS) | Zlib, MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [C#/WinRT](https://github.com/microsoft/CsWinRT), MicroCom | MIT |
| [Inter](https://rsms.me/inter) | SIL Open Font License 1.1 |
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) | MIT |

Every license text ships in the app, under Settings ▸ About ▸ Open Source & Licenses; they're in [src/Aster.Desktop/Licenses](src/Aster.Desktop/Licenses).

## License

Aster is licensed under the [Mozilla Public License 2.0](LICENSE). You're free to use, modify and redistribute it. If you distribute a modified version, the files of Aster you changed must stay available under the MPL. New files you add can use any license. The components above keep their own licenses.

The Aster name and app icon aren't covered by the license. If you publish your own build, give it a different name and icon so people don't confuse it with the official app.

Aster isn't affiliated with or endorsed by the Jellyfin project.
