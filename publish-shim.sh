#!/bin/bash
# Publish Koware CLI for Linux
# Usage: ./publish-linux.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${HOME}/.local/share/koware"

echo "Publishing Koware CLI..."
dotnet publish "$SCRIPT_DIR/Koware.Cli" -c Release -o "$INSTALL_DIR"

# Create shim script
cat > "$INSTALL_DIR/koware" << 'EOF'
#!/bin/bash
REAL_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
SCRIPT_DIR="$(dirname "$REAL_PATH")"
exec dotnet "$SCRIPT_DIR/Koware.Cli.dll" "$@"
EOF
chmod +x "$INSTALL_DIR/koware"

# Symlink to PATH
mkdir -p "${HOME}/.local/bin"
ln -sf "$INSTALL_DIR/koware" "${HOME}/.local/bin/koware"

echo "[+] Published to $INSTALL_DIR"
echo "[+] Symlinked to ~/.local/bin/koware"
