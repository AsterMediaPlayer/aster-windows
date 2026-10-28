#!/bin/sh
# Installs Aster for the current user from the unpacked release:
# the app into ~/.local/opt/aster, and its menu entry and icon.
# Run again to update; ./install.sh --uninstall removes it (sign-ins and
# settings, in the Secret Service and ~/.local/share/Aster, are left alone).
set -eu

here=$(cd "$(dirname "$0")" && pwd)
appdir="$HOME/.local/opt/aster"
entry="$HOME/.local/share/applications/aster.desktop"
icon="$HOME/.local/share/icons/hicolor/256x256/apps/aster.png"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$appdir" "$entry" "$icon"
    echo "Aster removed."
    exit 0
fi

if ! ldconfig -p 2>/dev/null | grep -q 'libmpv\.so\.2'; then
    echo "Note: Aster needs the mpv library. On Debian or Ubuntu: sudo apt install libmpv2" >&2
    echo "      (Fedora: sudo dnf install mpv-libs; Arch: sudo pacman -S mpv)" >&2
fi

rm -rf "$appdir"
mkdir -p "$appdir" "$(dirname "$entry")" "$(dirname "$icon")"
cp -R "$here/aster/." "$appdir/"
chmod +x "$appdir/Aster.Desktop"
cp "$here/aster.png" "$icon"
sed "s|@APPDIR@|$appdir|" "$here/aster.desktop" > "$entry"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$(dirname "$entry")" || true
echo "Aster installed: find it in your applications menu, or run $appdir/Aster.Desktop"
