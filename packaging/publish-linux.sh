#!/usr/bin/env bash
# Builds a self-contained Linux tarball: artifacts/linux/ussh-<version>-linux-<arch>.tar.gz
# Usage: packaging/publish-linux.sh [version] [x64|arm64]
set -euo pipefail

VERSION="${1:-0.1.0}"
ARCH="${2:-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/linux"
STAGE="$OUT/ussh-$VERSION-linux-$ARCH"

rm -rf "$STAGE"
dotnet publish "$ROOT/src/Ussh.App/Ussh.App.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:Version="$VERSION" -o "$STAGE/lib"

mkdir -p "$STAGE/share/applications" "$STAGE/share/icons/hicolor/256x256/apps"
cp "$ROOT/src/Ussh.App/Assets/ussh.png" "$STAGE/share/icons/hicolor/256x256/apps/ussh.png"
cat > "$STAGE/share/applications/ussh.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ussh
Comment=SSH client with stable long-running sessions
Exec=ussh
Icon=ussh
Terminal=false
Categories=Network;RemoteAccess;System;
EOF

cat > "$STAGE/install.sh" <<'EOF'
#!/usr/bin/env bash
# Installs for the current user into ~/.local
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p ~/.local/lib ~/.local/bin ~/.local/share
rm -rf ~/.local/lib/ussh && cp -r "$HERE/lib" ~/.local/lib/ussh
ln -sf ~/.local/lib/ussh/ussh ~/.local/bin/ussh
cp -r "$HERE/share/." ~/.local/share/
echo "Installed. Run 'ussh' or find it in your applications menu."
EOF
chmod +x "$STAGE/install.sh"

tar -C "$OUT" -czf "$STAGE.tar.gz" "$(basename "$STAGE")"
echo "Created $STAGE.tar.gz"
