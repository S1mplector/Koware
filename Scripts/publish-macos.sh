#!/bin/bash
# Author: Ilgaz Mehmetoglu
# Summary: Publishes Koware for macOS as a folder-based runtime bundle and creates a DMG installer.

set -euo pipefail

# Configuration
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-osx-arm64}"  # osx-arm64, osx-x64, or universal
SELF_CONTAINED="${SELF_CONTAINED:-true}"
COPY_TO_DESKTOP="${COPY_TO_DESKTOP:-false}"
COPY_TO_REPO_ROOT="${COPY_TO_REPO_ROOT:-false}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/publish/macos}"
DMG_OUTPUT_DIR="${DMG_OUTPUT_DIR:-$REPO_ROOT/publish}"

# App metadata
APP_NAME="Koware"
APP_VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj")
VOLUME_NAME="Koware Installer"

# Colors for output
info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; exit 1; }

publish_bundle() {
    local rid="$1"
    local target_root="$2"
    local cli_proj="$REPO_ROOT/Koware.Cli/Koware.Cli.csproj"
    local reader_proj="$REPO_ROOT/Koware.Reader/Koware.Reader.csproj"
    local cli_dir="$target_root/$rid"
    local reader_dir="$cli_dir/reader"

    mkdir -p "$cli_dir" "$reader_dir"

    local cli_args=(
        publish "$cli_proj"
        -c "$CONFIGURATION"
        -r "$rid"
        -o "$cli_dir"
    )

    local reader_args=(
        publish "$reader_proj"
        -c "$CONFIGURATION"
        -r "$rid"
        -o "$reader_dir"
    )

    if [ "$SELF_CONTAINED" = "true" ]; then
        cli_args+=("--self-contained" "true")
        reader_args+=("--self-contained" "true")
    else
        cli_args+=("--self-contained" "false")
        reader_args+=("--self-contained" "false")
    fi

    info "Publishing CLI bundle for $rid"
    echo "dotnet ${cli_args[*]}"
    dotnet "${cli_args[@]}"

    info "Publishing reader bundle for $rid"
    echo "dotnet ${reader_args[*]}"
    dotnet "${reader_args[@]}"
}

write_portable_launcher() {
    local output_path="$1"

    cat > "$output_path" << 'EOF'
#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_ROOT="$SCRIPT_DIR/lib/koware"
ARCH="$(uname -m)"

case "$ARCH" in
    arm64|aarch64) PRIMARY_RID="osx-arm64"; FALLBACK_RID="osx-x64" ;;
    x86_64) PRIMARY_RID="osx-x64"; FALLBACK_RID="osx-arm64" ;;
    *) PRIMARY_RID=""; FALLBACK_RID="" ;;
esac

for rid in "$PRIMARY_RID" "$FALLBACK_RID"; do
    if [ -n "$rid" ] && [ -x "$APP_ROOT/$rid/Koware.Cli" ]; then
        exec "$APP_ROOT/$rid/Koware.Cli" "$@"
    fi
done

echo "No compatible Koware runtime bundle found in $APP_ROOT." >&2
exit 1
EOF

    chmod +x "$output_path"
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --runtime) RUNTIME="$2"; shift 2 ;;
        --config) CONFIGURATION="$2"; shift 2 ;;
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        --no-self-contained) SELF_CONTAINED="false"; shift ;;
        --copy-to-desktop) COPY_TO_DESKTOP="true"; shift ;;
        --copy-to-repo-root) COPY_TO_REPO_ROOT="true"; shift ;;
        --help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  --runtime <rid>       Runtime identifier (osx-arm64, osx-x64, universal). Default: osx-arm64"
            echo "  --config <cfg>        Build configuration (Release, Debug). Default: Release"
            echo "  --output <dir>        Output directory. Default: ./publish/macos"
            echo "  --no-self-contained   Framework-dependent deployment"
            echo "  --copy-to-desktop     Copy the DMG to ~/Desktop after build"
            echo "  --copy-to-repo-root   Copy the DMG to the repository root after build"
            exit 0
            ;;
        *) err "Unknown option: $1" ;;
    esac
done

info "Publishing Koware for macOS"
info "  Runtime: $RUNTIME"
info "  Configuration: $CONFIGURATION"
info "  Self-contained: $SELF_CONTAINED"
info "  Version: $APP_VERSION"

TARGET_RIDS=("$RUNTIME")
if [[ "$RUNTIME" == "universal" ]]; then
    TARGET_RIDS=("osx-arm64" "osx-x64")
fi

DMG_NAME="${APP_NAME}-${APP_VERSION}-${RUNTIME}.dmg"
BUNDLE_ROOT="$OUTPUT_DIR/bundle"

if [ -d "$OUTPUT_DIR" ]; then
    info "Cleaning $OUTPUT_DIR"
    rm -rf "$OUTPUT_DIR"
fi

mkdir -p "$OUTPUT_DIR"

for rid in "${TARGET_RIDS[@]}"; do
    publish_bundle "$rid" "$BUNDLE_ROOT"
done

DMG_STAGING="$OUTPUT_DIR/dmg-staging"
mkdir -p "$DMG_STAGING/lib"
cp -R "$BUNDLE_ROOT" "$DMG_STAGING/lib/koware"

write_portable_launcher "$DMG_STAGING/koware"

cat > "$DMG_STAGING/install.sh" << 'EOF'
#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_ROOT="$SCRIPT_DIR/lib/koware"
INSTALL_ROOT="/usr/local/lib/koware"
BIN_DIR="/usr/local/bin"
TEMP_WRAPPER="$(mktemp)"

cat > "$TEMP_WRAPPER" << 'WRAPPER'
#!/bin/bash
set -euo pipefail

APP_ROOT="/usr/local/lib/koware"
ARCH="$(uname -m)"

case "$ARCH" in
    arm64|aarch64) PRIMARY_RID="osx-arm64"; FALLBACK_RID="osx-x64" ;;
    x86_64) PRIMARY_RID="osx-x64"; FALLBACK_RID="osx-arm64" ;;
    *) PRIMARY_RID=""; FALLBACK_RID="" ;;
esac

for rid in "$PRIMARY_RID" "$FALLBACK_RID"; do
    if [ -n "$rid" ] && [ -x "$APP_ROOT/$rid/Koware.Cli" ]; then
        exec "$APP_ROOT/$rid/Koware.Cli" "$@"
    fi
done

echo "No compatible Koware runtime bundle found in $APP_ROOT." >&2
exit 1
WRAPPER

chmod +x "$TEMP_WRAPPER"

copy_payload() {
    mkdir -p "$INSTALL_ROOT" "$BIN_DIR"
    rm -rf "$INSTALL_ROOT"
    mkdir -p "$INSTALL_ROOT"
    cp -R "$SOURCE_ROOT"/. "$INSTALL_ROOT/"
    cp "$TEMP_WRAPPER" "$BIN_DIR/koware"
    chmod +x "$BIN_DIR/koware"
}

echo "Installing Koware to $INSTALL_ROOT..."

if [ -w "/usr/local" ]; then
    copy_payload
else
    echo "Administrator privileges required. Please enter your password:"
    sudo mkdir -p "$INSTALL_ROOT" "$BIN_DIR"
    sudo rm -rf "$INSTALL_ROOT"
    sudo mkdir -p "$INSTALL_ROOT"
    sudo cp -R "$SOURCE_ROOT"/. "$INSTALL_ROOT/"
    sudo cp "$TEMP_WRAPPER" "$BIN_DIR/koware"
    sudo chmod +x "$BIN_DIR/koware"
fi

rm -f "$TEMP_WRAPPER"

echo ""
echo "Koware installed successfully."
echo ""
echo "You can now run 'koware' from any terminal."
echo "Try 'koware --help' to get started."
EOF
chmod +x "$DMG_STAGING/install.sh"

cat > "$DMG_STAGING/README.txt" << EOF
Koware - Version $APP_VERSION
================================

INSTALLATION OPTIONS:

Option 1: Run the installer (recommended)
  Double-click 'install.sh' or run it from Terminal:
  $ ./install.sh

Option 2: Run from this DMG folder
  $ ./koware --help

USAGE:
  koware --help          Show help
  koware --version       Show version
  koware read "<title>"  Uses the bundled reader on macOS

For more information, visit the project repository.
EOF

info "Creating DMG: $DMG_NAME"
mkdir -p "$DMG_OUTPUT_DIR"
DMG_PATH="$DMG_OUTPUT_DIR/$DMG_NAME"

[ -f "$DMG_PATH" ] && rm -f "$DMG_PATH"

hdiutil create \
    -volname "$VOLUME_NAME" \
    -srcfolder "$DMG_STAGING" \
    -ov \
    -format UDZO \
    "$DMG_PATH"

info "DMG created: $DMG_PATH"

if [ "$COPY_TO_REPO_ROOT" = "true" ]; then
    TARGET="$REPO_ROOT/$DMG_NAME"
    cp "$DMG_PATH" "$TARGET"
    info "Copied DMG to repo root: $TARGET"
fi

if [ "$COPY_TO_DESKTOP" = "true" ]; then
    DESKTOP="$HOME/Desktop"
    if [ -d "$DESKTOP" ]; then
        TARGET="$DESKTOP/$DMG_NAME"
        cp "$DMG_PATH" "$TARGET"
        info "Copied DMG to Desktop: $TARGET"
    else
        warn "Desktop folder not found; skipping Desktop copy."
    fi
fi

info "DMG packaging complete."
