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

mkdir -p "$STAGE/share/applications" "$STAGE/share/icons/hicolor/256x256/apps"
cp "$ROOT/src/Ussh.App/Assets/zssh.png" "$STAGE/share/icons/hicolor/256x256/apps/zssh.png"
cat > "$STAGE/share/applications/zssh.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=zSSH
Comment=SSH client with stable long-running sessions
Exec=zssh
Icon=zssh
Terminal=false
Categories=Network;RemoteAccess;System;
EOF

cat > "$STAGE/install.sh" <<'EOF'
#!/usr/bin/env bash
# Installs for the current user into ~/.local
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p ~/.local/lib ~/.local/bin ~/.local/share
rm -rf ~/.local/lib/zssh && cp -r "$HERE/lib" ~/.local/lib/zssh
ln -sf ~/.local/lib/zssh/zssh ~/.local/bin/zssh
cp -r "$HERE/share/." ~/.local/share/
# Remove an install from before the rename (uSSH). Its data folder is moved on first run.
rm -rf ~/.local/lib/ussh ~/.local/bin/ussh ~/.local/share/applications/ussh.desktop \
  ~/.local/share/icons/hicolor/256x256/apps/ussh.png
echo "Installed. Run 'zssh' or find it in your applications menu."
EOF
chmod +x "$STAGE/install.sh"

tar -C "$OUT" -czf "$STAGE.tar.gz" "$(basename "$STAGE")"
echo "Created $STAGE.tar.gz"
