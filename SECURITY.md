# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security tab → Report a vulnerability** on this repository. Include what you found, how to reproduce it, the Aster
version (Settings ▸ About), whether it's Windows or Linux, and the server and its version.

Aster is maintained by one person, so a reply can take a few days. You'll hear back that the report arrived, then
what will be done about it. A fix is released before the details are made public, and you'll be credited in the
advisory unless you'd rather not be.

## What counts

Aster signs in to media servers, keeps those sign-ins on your computer, and plays files and menus that come from
servers and from other people. These are in scope:

- **Sign-ins and tokens:** anything that exposes a server's access token, a password or a Plex sign-in, for example
  through a log, a crash report, a file other users can read, or a request sent somewhere other than the server it
  belongs to. Sign-ins are kept in Windows Credential Manager on Windows, and on Linux in the desktop's Secret Service
  (GNOME Keyring, KWallet), or, where there is none, in a file under Aster's data folder that only your user can read.
- **Server responses:** a malicious or compromised server, or anyone between you and it, making the app run code,
  read or write files outside Aster's own folders, or reach other addresses on your network.
- **Media and menus:** a file, subtitle track or disc menu that makes the app run code, crash in a way an attacker can
  steer, or load something the menu isn't allowed to name. Aster plays media through libmpv; on Windows the libmpv
  build is pinned and checked against its hash when it's fetched (`tools/fetch-libmpv.ps1`).
- **Installing and updating:** anything in the installer, the zip or the Linux tarball and `install.sh` that lets
  another user or program on the same computer change what Aster runs.

Not in scope here:

- **Problems in Jellyfin, Emby or Plex themselves:** report those to their projects. Problems in Aster's server
  plugins go to [aster-companion-plugins](https://github.com/AsterMediaPlayer/aster-companion-plugins/security).
- **Vulnerabilities in your Linux distribution's libmpv or FFmpeg:** on Linux, Aster uses the system's copy, so
  updates come from your distribution. Do tell us if Aster uses it in a way that makes one exploitable.
- **Someone with full control of your user account:** they can already read what that account can.

## Getting Aster safely

Download Aster only from this repository's [Releases](https://github.com/AsterMediaPlayer/aster-windows/releases).
The Windows builds aren't code-signed yet, so Windows SmartScreen may warn the first time you run one; that is
expected for a download from Releases, and a reason to be wary of a copy from anywhere else.

## Supported versions

Aster is in beta. Fixes go to `main` and ship in the next release, so please use the latest one.
