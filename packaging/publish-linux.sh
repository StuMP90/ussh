#!/usr/bin/env bash
# Builds a self-contained Linux tarball: artifacts/linux/zssh-<version>-linux-<arch>.tar.gz
# Usage: packaging/publish-linux.sh [version] [x64|arm64]
set -euo pipefail

VERSION="${1:-0.1.0}"
ARCH="${2:-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/linux"
STAGE="$OUT/zssh-$VERSION-linux-$ARCH"

rm -rf "$STAGE"
dotnet publish "$ROOT/src/Ussh.App/Ussh.App.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:Version="$VERSION" -o "$STAGE/lib"

# Licence texts for everything shipped (shown in the app under Help → Licences).
python3 "$ROOT/tools/generate-third-party-notices.py" --rid "linux-$ARCH" --output "$STAGE/lib/THIRD-PARTY-NOTICES.txt"
cp "$ROOT/LICENSE" "$STAGE/lib/LICENSE.txt"
cp "$STAGE/lib/THIRD-PARTY-NOTICES.txt" "$STAGE/lib/LICENSE.txt" "$STAGE/"

cp "$ROOT/src/Ussh.App/Assets/zssh.png" "$STAGE/zssh.png"

cat > "$STAGE/install.sh" <<'EOF'
#!/usr/bin/env bash
# Installs for the current user into ~/.local
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APPS=~/.local/share/applications
ICONS=~/.local/share/icons/hicolor/256x256/apps
mkdir -p ~/.local/lib ~/.local/bin "$APPS" "$ICONS"
rm -rf ~/.local/lib/zssh && cp -r "$HERE/lib" ~/.local/lib/zssh
ln -sf ~/.local/lib/zssh/zssh ~/.local/bin/zssh

# Remove an install from before the rename (uSSH). Its data folder is moved on first run.
rm -rf ~/.local/lib/ussh ~/.local/bin/ussh "$APPS/ussh.desktop" "$ICONS/ussh.png"

# The icon goes in first: the desktop reads a new menu entry at once, and if its icon isn't there
# yet it shows a generic one until the next login. Exec and Icon are absolute, so the entry works
# even when ~/.local/bin isn't on the session's PATH (often the case on a first install).
cp "$HERE/zssh.png" "$ICONS/zssh.png"
touch ~/.local/share/icons/hicolor
cat > "$APPS/zssh.desktop.tmp" <<DESKTOP
[Desktop Entry]
Type=Application
Name=zSSH
Comment=SSH client with stable long-running sessions
Exec=$HOME/.local/lib/zssh/zssh
Icon=$ICONS/zssh.png
StartupWMClass=zssh
Terminal=false
Categories=Network;RemoteAccess;
Keywords=ssh;sftp;s3;terminal;tunnel;
DESKTOP
mv "$APPS/zssh.desktop.tmp" "$APPS/zssh.desktop"
if command -v update-desktop-database >/dev/null; then update-desktop-database "$APPS" 2>/dev/null || true; fi
echo "Installed. Run 'zssh' or find zSSH in your applications menu."
EOF
chmod +x "$STAGE/install.sh"

tar -C "$OUT" -czf "$STAGE.tar.gz" "$(basename "$STAGE")"
echo "Created $STAGE.tar.gz"
