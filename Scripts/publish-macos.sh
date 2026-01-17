#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: Publishes Koware CLI for macOS and creates a DMG installer.

set -e

# Configuration
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-osx-arm64}"  # osx-arm64 for Apple Silicon, osx-x64 for Intel, universal for both
SELF_CONTAINED="${SELF_CONTAINED:-true}"
COPY_TO_DESKTOP="${COPY_TO_DESKTOP:-false}"
COPY_TO_REPO_ROOT="${COPY_TO_REPO_ROOT:-false}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/publish/macos}"
DMG_OUTPUT_DIR="${DMG_OUTPUT_DIR:-$REPO_ROOT/publish}"

# App metadata
APP_NAME="Koware"
APP_VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj" || echo "0.0.0")
VOLUME_NAME="Koware Installer"

# Colors for output
info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; }

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
        *) err "Unknown option: $1"; exit 1 ;;
    esac
done

info "Publishing Koware CLI for macOS"
info "  Runtime: $RUNTIME"
info "  Configuration: $CONFIGURATION"
info "  Self-contained: $SELF_CONTAINED"
info "  Version: $APP_VERSION"

if [[ "$RUNTIME" == "universal" ]] && ! command -v lipo >/dev/null 2>&1; then
    err "lipo (Xcode Command Line Tools) is required for universal builds."
fi

DMG_NAME="Koware-${APP_VERSION}-${RUNTIME}.dmg"

# Clean output directory
if [ -d "$OUTPUT_DIR" ]; then
    info "Cleaning $OUTPUT_DIR"
    rm -rf "$OUTPUT_DIR"
fi
mkdir -p "$OUTPUT_DIR"

# Publish CLI
CLI_PROJ="$REPO_ROOT/Koware.Cli/Koware.Cli.csproj"
info "Publishing Koware.Cli..."

TARGET_RIDS=("$RUNTIME")
if [[ "$RUNTIME" == "universal" ]]; then
    TARGET_RIDS=("osx-arm64" "osx-x64")
fi

if [[ "$RUNTIME" == "universal" ]]; then
    BUILD_DIR="$OUTPUT_DIR/cli-build"
    CLI_DIR="$OUTPUT_DIR/cli"
    rm -rf "$BUILD_DIR" "$CLI_DIR"
    mkdir -p "$BUILD_DIR" "$CLI_DIR"

    for rid in "${TARGET_RIDS[@]}"; do
        PUBLISH_ARGS=(
            "publish" "$CLI_PROJ"
            "-c" "$CONFIGURATION"
            "-r" "$rid"
            "-o" "$BUILD_DIR/$rid"
            "/p:PublishSingleFile=true"
            "/p:IncludeNativeLibrariesForSelfExtract=true"
            "/p:EnableCompressionInSingleFile=true"
        )

        if [ "$SELF_CONTAINED" = "true" ]; then
            PUBLISH_ARGS+=("--self-contained" "true")
        else
            PUBLISH_ARGS+=("--self-contained" "false")
        fi

        echo "dotnet ${PUBLISH_ARGS[*]}"
        dotnet "${PUBLISH_ARGS[@]}"
    done

    lipo -create "$BUILD_DIR/osx-arm64/Koware.Cli" "$BUILD_DIR/osx-x64/Koware.Cli" -output "$CLI_DIR/koware"
    chmod +x "$CLI_DIR/koware"

    if [ -f "$BUILD_DIR/osx-arm64/appsettings.json" ]; then
        cp "$BUILD_DIR/osx-arm64/appsettings.json" "$CLI_DIR/"
    elif [ -f "$BUILD_DIR/osx-x64/appsettings.json" ]; then
        cp "$BUILD_DIR/osx-x64/appsettings.json" "$CLI_DIR/"
    fi

    rm -rf "$BUILD_DIR"
    info "CLI executable ready: $CLI_DIR/koware"
else
    PUBLISH_ARGS=(
        "publish" "$CLI_PROJ"
        "-c" "$CONFIGURATION"
        "-r" "$RUNTIME"
        "-o" "$OUTPUT_DIR/cli"
        "/p:PublishSingleFile=true"
        "/p:IncludeNativeLibrariesForSelfExtract=true"
        "/p:EnableCompressionInSingleFile=true"
    )

    if [ "$SELF_CONTAINED" = "true" ]; then
        PUBLISH_ARGS+=("--self-contained" "true")
    else
        PUBLISH_ARGS+=("--self-contained" "false")
    fi

    echo "dotnet ${PUBLISH_ARGS[*]}"
    dotnet "${PUBLISH_ARGS[@]}"

    # Rename the executable to 'koware' for macOS convention
    CLI_EXE="$OUTPUT_DIR/cli/Koware.Cli"
    if [ -f "$CLI_EXE" ]; then
        mv "$CLI_EXE" "$OUTPUT_DIR/cli/koware"
        chmod +x "$OUTPUT_DIR/cli/koware"
        info "CLI executable ready: $OUTPUT_DIR/cli/koware"
    else
        err "CLI executable not found at $CLI_EXE"
        exit 1
    fi
fi

# Create DMG staging directory
DMG_STAGING="$OUTPUT_DIR/dmg-staging"
mkdir -p "$DMG_STAGING"

# Copy CLI to staging
cp -r "$OUTPUT_DIR/cli/"* "$DMG_STAGING/"

# Create a simple install script
cat > "$DMG_STAGING/install.sh" << 'EOF'
#!/bin/bash
# Koware macOS Installer
# This script installs koware to /usr/local/bin

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/usr/local/bin"

echo "Installing Koware to $INSTALL_DIR..."

# Check if we have write permission
if [ ! -w "$INSTALL_DIR" ]; then
    echo "Administrator privileges required. Please enter your password:"
    sudo mkdir -p "$INSTALL_DIR"
    sudo cp "$SCRIPT_DIR/koware" "$INSTALL_DIR/"
    sudo chmod +x "$INSTALL_DIR/koware"
else
    mkdir -p "$INSTALL_DIR"
    cp "$SCRIPT_DIR/koware" "$INSTALL_DIR/"
    chmod +x "$INSTALL_DIR/koware"
fi

# Copy appsettings.json to a config location
CONFIG_DIR="$HOME/.config/koware"
mkdir -p "$CONFIG_DIR"
if [ -f "$SCRIPT_DIR/appsettings.json" ]; then
    cp "$SCRIPT_DIR/appsettings.json" "$CONFIG_DIR/"
fi

echo ""
echo "✓ Koware installed successfully!"
echo ""
echo "You can now run 'koware' from any terminal."
echo "Try 'koware --help' to get started."
EOF
chmod +x "$DMG_STAGING/install.sh"

# Create README for the DMG
cat > "$DMG_STAGING/README.txt" << EOF
Koware - Version $APP_VERSION
================================

INSTALLATION OPTIONS:

Option 1: Run the installer (recommended)
  Double-click 'install.sh' or run it from Terminal:
  $ ./install.sh

Option 2: Manual installation
  Copy 'koware' to a directory in your PATH:
  $ sudo cp koware /usr/local/bin/
  $ sudo chmod +x /usr/local/bin/koware

Option 3: Run from anywhere
  You can run koware directly from this folder:
  $ ./koware --help

USAGE:
  koware --help          Show help
  koware --version       Show version

For more information, visit the project repository.
EOF

# Create DMG
info "Creating DMG: $DMG_NAME"
mkdir -p "$DMG_OUTPUT_DIR"
DMG_PATH="$DMG_OUTPUT_DIR/$DMG_NAME"

# Remove existing DMG if present
[ -f "$DMG_PATH" ] && rm -f "$DMG_PATH"

# Create DMG using hdiutil
hdiutil create \
    -volname "$VOLUME_NAME" \
    -srcfolder "$DMG_STAGING" \
    -ov \
    -format UDZO \
    "$DMG_PATH"

info "DMG created: $DMG_PATH"

# Copy to repo root if requested
if [ "$COPY_TO_REPO_ROOT" = "true" ]; then
    TARGET="$REPO_ROOT/$DMG_NAME"
    cp "$DMG_PATH" "$TARGET"
    info "Copied DMG to repo root: $TARGET"
fi

# Copy to Desktop if requested
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

# Show output summary
echo ""
info "Build complete!"
echo "  DMG: $DMG_PATH"
echo "  Size: $(du -h "$DMG_PATH" | cut -f1)"
echo ""
info "To install, open the DMG and run install.sh"
