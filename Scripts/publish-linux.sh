#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: Publishes Koware CLI for Linux and creates distributable packages.

set -e

# Configuration
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-linux-x64}"  # linux-x64, linux-arm64, linux-musl-x64
SELF_CONTAINED="${SELF_CONTAINED:-true}"
BUNDLE_PLAYER="${BUNDLE_PLAYER:-true}"
CREATE_TARBALL="${CREATE_TARBALL:-true}"
CREATE_DEB="${CREATE_DEB:-false}"
CREATE_APPIMAGE="${CREATE_APPIMAGE:-false}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/publish/linux}"
DIST_OUTPUT_DIR="${DIST_OUTPUT_DIR:-$REPO_ROOT/publish}"

# App metadata
APP_NAME="koware"
APP_VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj" 2>/dev/null || echo "1.0.0")
APP_DESCRIPTION="Console-first anime/manga link aggregator"
APP_MAINTAINER="Ilgaz Mehmetoğlu"

# Colors for output
info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; }
success() { echo -e "\033[32m[OK  ]\033[0m $1"; }

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --runtime) RUNTIME="$2"; shift 2 ;;
        --config) CONFIGURATION="$2"; shift 2 ;;
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        --no-self-contained) SELF_CONTAINED="false"; shift ;;
        --no-player) BUNDLE_PLAYER="false"; shift ;;
        --no-tarball) CREATE_TARBALL="false"; shift ;;
        --deb) CREATE_DEB="true"; shift ;;
        --appimage) CREATE_APPIMAGE="true"; shift ;;
        --all-packages) CREATE_TARBALL="true"; CREATE_DEB="true"; CREATE_APPIMAGE="true"; shift ;;
        --help)
            echo "Usage: $0 [options]"
            echo ""
            echo "Build and package Koware for Linux distributions."
            echo ""
            echo "Options:"
            echo "  --runtime <rid>       Runtime identifier. Default: linux-x64"
            echo "                        Supported: linux-x64, linux-arm64, linux-musl-x64"
            echo "  --config <cfg>        Build configuration (Release, Debug). Default: Release"
            echo "  --output <dir>        Output directory. Default: ./publish/linux"
            echo "  --no-self-contained   Framework-dependent deployment (requires .NET runtime)"
            echo "  --no-player           Skip bundled Avalonia player (CLI-only build)"
            echo "  --no-tarball          Skip creating .tar.gz archive"
            echo "  --deb                 Create .deb package (requires dpkg-deb)"
            echo "  --appimage            Create AppImage (requires appimagetool)"
            echo "  --all-packages        Create all package types"
            echo ""
            echo "Examples:"
            echo "  $0                           # Build for linux-x64, create tarball"
            echo "  $0 --runtime linux-arm64     # Build for ARM64"
            echo "  $0 --deb                     # Also create .deb package"
            echo "  $0 --all-packages            # Create tarball, .deb, and AppImage"
            exit 0
            ;;
        *) err "Unknown option: $1"; exit 1 ;;
    esac
done

info "Publishing Koware CLI for Linux"
info "  Runtime: $RUNTIME"
info "  Configuration: $CONFIGURATION"
info "  Self-contained: $SELF_CONTAINED"
info "  Bundle player: $BUNDLE_PLAYER"
info "  Version: $APP_VERSION"

# Validate runtime
case "$RUNTIME" in
    linux-x64|linux-arm64|linux-musl-x64|linux-arm) ;;
    *) warn "Non-standard runtime: $RUNTIME. Proceeding anyway." ;;
esac

# Determine architecture for package names
case "$RUNTIME" in
    linux-x64|linux-musl-x64) ARCH="amd64"; ARCH_SHORT="x64" ;;
    linux-arm64) ARCH="arm64"; ARCH_SHORT="arm64" ;;
    linux-arm) ARCH="armhf"; ARCH_SHORT="arm" ;;
    *) ARCH="unknown"; ARCH_SHORT="unknown" ;;
esac

TARBALL_NAME="${APP_NAME}-${APP_VERSION}-linux-${ARCH_SHORT}.tar.gz"
DEB_NAME="${APP_NAME}_${APP_VERSION}_${ARCH}.deb"

# Clean output directory
if [ -d "$OUTPUT_DIR" ]; then
    info "Cleaning $OUTPUT_DIR"
    rm -rf "$OUTPUT_DIR"
fi
mkdir -p "$OUTPUT_DIR"

# Publish CLI
CLI_PROJ="$REPO_ROOT/Koware.Cli/Koware.Cli.csproj"
info "Publishing Koware.Cli..."

PUBLISH_ARGS=(
    "publish" "$CLI_PROJ"
    "-c" "$CONFIGURATION"
    "-r" "$RUNTIME"
    "-o" "$OUTPUT_DIR/cli"
    "/p:PublishSingleFile=true"
    "/p:IncludeNativeLibrariesForSelfExtract=true"
)

if [ "$SELF_CONTAINED" = "true" ]; then
    PUBLISH_ARGS+=("--self-contained" "true")
    PUBLISH_ARGS+=("/p:EnableCompressionInSingleFile=true")
else
    PUBLISH_ARGS+=("--self-contained" "false")
fi

echo "dotnet ${PUBLISH_ARGS[*]}"
dotnet "${PUBLISH_ARGS[@]}"

# Rename the executable to 'koware' for Linux convention
CLI_EXE="$OUTPUT_DIR/cli/Koware.Cli"
if [ -f "$CLI_EXE" ]; then
    mv "$CLI_EXE" "$OUTPUT_DIR/cli/koware"
    chmod +x "$OUTPUT_DIR/cli/koware"
    success "CLI executable ready: $OUTPUT_DIR/cli/koware"
else
    err "CLI executable not found at $CLI_EXE"
    exit 1
fi

# Publish bundled Avalonia player for watch-together sync.
PLAYER_PROJ="$REPO_ROOT/Koware.Player/Koware.Player.csproj"
if [ "$BUNDLE_PLAYER" = "true" ]; then
    if [ ! -f "$PLAYER_PROJ" ]; then
        err "Koware.Player project not found at $PLAYER_PROJ"
        exit 1
    fi

    info "Publishing Koware.Player (Avalonia)..."
    PLAYER_ARGS=(
        "publish" "$PLAYER_PROJ"
        "-c" "$CONFIGURATION"
        "-r" "$RUNTIME"
        "-o" "$OUTPUT_DIR/player"
    )

    if [ "$SELF_CONTAINED" = "true" ]; then
        PLAYER_ARGS+=("--self-contained" "true")
    else
        PLAYER_ARGS+=("--self-contained" "false")
    fi

    echo "dotnet ${PLAYER_ARGS[*]}"
    dotnet "${PLAYER_ARGS[@]}"

    if [ ! -f "$OUTPUT_DIR/player/Koware.Player" ]; then
        err "Koware.Player publish completed, but executable was not found at $OUTPUT_DIR/player/Koware.Player"
        exit 1
    fi

    chmod +x "$OUTPUT_DIR/player/Koware.Player"
    success "Player executable ready: $OUTPUT_DIR/player/Koware.Player"
else
    warn "Skipping bundled Avalonia player; watch-together sync will not work from this package"
fi

# Create staging directory for distribution
STAGING="$OUTPUT_DIR/staging"
mkdir -p "$STAGING/koware"

# Copy files to staging
cp "$OUTPUT_DIR/cli/koware" "$STAGING/koware/"
[ -f "$OUTPUT_DIR/cli/appsettings.json" ] && cp "$OUTPUT_DIR/cli/appsettings.json" "$STAGING/koware/"
if [ -f "$OUTPUT_DIR/player/Koware.Player" ]; then
    mkdir -p "$STAGING/koware/player"
    cp -R "$OUTPUT_DIR/player/"* "$STAGING/koware/player/"
    chmod +x "$STAGING/koware/player/Koware.Player" 2>/dev/null || true
elif [ "$BUNDLE_PLAYER" = "true" ]; then
    err "Bundled player is required but was not found in $OUTPUT_DIR/player"
    exit 1
fi

# Create install script for the package
cat > "$STAGING/koware/install.sh" << 'INSTALL_SCRIPT'
#!/bin/bash
# Koware Linux Installer
# Installs koware to ~/.local/share/koware with PATH integration

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$HOME/.local/share/koware"
BIN_DIR="$HOME/.local/bin"

# Colors
info() { echo -e "\033[36m[INFO]\033[0m $1"; }
success() { echo -e "\033[32m[OK  ]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err() { echo -e "\033[31m[ERR ]\033[0m $1"; }

get_shell_config_file() {
    local shell_name
    shell_name=$(basename "${SHELL:-bash}")

    case "$shell_name" in
        bash)
            echo "$HOME/.bashrc"
            ;;
        zsh)
            echo "$HOME/.zshrc"
            ;;
        fish)
            echo "$HOME/.config/fish/config.fish"
            ;;
        *)
            echo "$HOME/.profile"
            ;;
    esac
}

path_config_line() {
    local shell_name
    local bin_dir_for_config="$BIN_DIR"
    shell_name=$(basename "${SHELL:-bash}")

    if [[ "$bin_dir_for_config" == "$HOME"* ]]; then
        bin_dir_for_config="\$HOME${bin_dir_for_config#$HOME}"
    fi

    if [ "$shell_name" = "fish" ]; then
        echo "set -gx PATH \"$bin_dir_for_config\" \$PATH"
    else
        echo "export PATH=\"$bin_dir_for_config:\$PATH\""
    fi
}

ensure_path_configured() {
    if [ "${KOWARE_SKIP_PATH_UPDATE:-false}" = "true" ]; then
        warn "Skipping shell PATH update because KOWARE_SKIP_PATH_UPDATE is set"
        return 0
    fi

    local config_file
    local path_line
    config_file=$(get_shell_config_file)
    path_line=$(path_config_line)

    mkdir -p "$(dirname "$config_file")"
    touch "$config_file"

    if grep -Fq "$path_line" "$config_file"; then
        info "PATH entry already present in $config_file"
        return 0
    fi

    {
        echo ""
        echo "# Koware - added by installer"
        echo "$path_line"
    } >> "$config_file"

    success "Added $BIN_DIR to PATH in $config_file"
}

libvlc_available() {
    if command -v vlc &> /dev/null; then
        return 0
    fi

    if command -v ldconfig &> /dev/null && ldconfig -p 2>/dev/null | grep -q "libvlc\\.so"; then
        return 0
    fi

    find /usr/lib /usr/local/lib -name "libvlc.so*" -print -quit 2>/dev/null | grep -q .
}

warn_if_libvlc_missing() {
    if [ -x "$INSTALL_DIR/player/Koware.Player" ] && ! libvlc_available; then
        warn "Bundled Koware.Player was installed, but native LibVLC was not detected."
        echo "Install VLC runtime libraries before using the bundled player:"
        echo ""
        echo "  sudo apt update && sudo apt install -y vlc libvlc5 vlc-plugin-base"
        echo ""
    fi
}

echo ""
echo "╔══════════════════════════════════════╗"
echo "║       Koware Linux Installer         ║"
echo "╚══════════════════════════════════════╝"
echo ""

# Check if running with --system flag for system-wide install
SYSTEM_INSTALL=false
if [ "$1" = "--system" ] || [ "$1" = "-s" ]; then
    SYSTEM_INSTALL=true
    INSTALL_DIR="/opt/koware"
    BIN_DIR="/usr/local/bin"
fi

if [ "$SYSTEM_INSTALL" = true ]; then
    info "System-wide installation to $INSTALL_DIR"
    if [ "$EUID" -ne 0 ]; then
        echo "System-wide installation requires root privileges."
        echo "Please run: sudo ./install.sh --system"
        exit 1
    fi
else
    info "User installation to $INSTALL_DIR"
fi

# Create directories
mkdir -p "$INSTALL_DIR"
mkdir -p "$BIN_DIR"

# Copy files
info "Installing koware..."
cp "$SCRIPT_DIR/koware" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/koware"

if [ -f "$SCRIPT_DIR/appsettings.json" ]; then
    cp "$SCRIPT_DIR/appsettings.json" "$INSTALL_DIR/"
fi

if [ -f "$SCRIPT_DIR/player/Koware.Player" ]; then
    info "Installing bundled player..."
    rm -rf "$INSTALL_DIR/player"
    mkdir -p "$INSTALL_DIR/player"
    cp -R "$SCRIPT_DIR/player/"* "$INSTALL_DIR/player/"
    chmod +x "$INSTALL_DIR/player/Koware.Player" 2>/dev/null || true
elif [ "${KOWARE_REQUIRE_PLAYER:-true}" = "true" ]; then
    err "Bundled player missing from package. Watch-together sync requires Koware.Player."
    exit 1
else
    warn "Bundled player missing from package; watch-together sync will not work"
fi

# Create symlink in bin directory
info "Creating symlink in $BIN_DIR..."
ln -sf "$INSTALL_DIR/koware" "$BIN_DIR/koware"

# Create config directory
CONFIG_DIR="$HOME/.config/koware"
if [ "$SYSTEM_INSTALL" = true ]; then
    # For system install, still use user config
    CONFIG_DIR="$HOME/.config/koware"
fi
mkdir -p "$CONFIG_DIR"

# Copy default config if user config doesn't exist
if [ ! -f "$CONFIG_DIR/appsettings.user.json" ] && [ -f "$SCRIPT_DIR/appsettings.json" ]; then
    cp "$SCRIPT_DIR/appsettings.json" "$CONFIG_DIR/appsettings.user.json"
    info "Created default config at $CONFIG_DIR/appsettings.user.json"
fi

# Check if ~/.local/bin is in PATH
if [ "$SYSTEM_INSTALL" = false ]; then
    if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
        warn "$BIN_DIR is not in your PATH"
        echo ""
        ensure_path_configured
        echo ""
        echo "Reload your shell before running 'koware' directly:"
        echo ""
        echo "  source $(get_shell_config_file)"
        echo ""
        echo "For this terminal only, you can also run:"
        echo ""
        echo "  export PATH=\"$BIN_DIR:\$PATH\""
        echo ""
        echo "Or run Koware by full path now:"
        echo ""
        echo "  $BIN_DIR/koware --help"
        echo ""
    fi
fi

echo ""
success "Koware installed successfully!"
echo ""
echo "  Installation: $INSTALL_DIR/koware"
if [ -f "$INSTALL_DIR/player/Koware.Player" ]; then
echo "  Player:       $INSTALL_DIR/player/Koware.Player"
fi
echo "  Symlink:      $BIN_DIR/koware"
echo "  Config:       $CONFIG_DIR/"
echo ""
warn_if_libvlc_missing
echo "Get started:"
echo "  koware --help              Show help"
echo "  koware provider autoconfig Configure providers"
echo "  koware search \"anime\"      Search for anime"
echo ""
INSTALL_SCRIPT
chmod +x "$STAGING/koware/install.sh"

# Create uninstall script
cat > "$STAGING/koware/uninstall.sh" << 'UNINSTALL_SCRIPT'
#!/bin/bash
# Koware Uninstaller

set -e

info() { echo -e "\033[36m[INFO]\033[0m $1"; }
success() { echo -e "\033[32m[OK  ]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }

echo ""
echo "Koware Uninstaller"
echo "=================="
echo ""

# Check for system install
SYSTEM_INSTALL=false
if [ "$1" = "--system" ] || [ "$1" = "-s" ]; then
    SYSTEM_INSTALL=true
fi

if [ "$SYSTEM_INSTALL" = true ]; then
    INSTALL_DIR="/opt/koware"
    BIN_DIR="/usr/local/bin"
    if [ "$EUID" -ne 0 ]; then
        echo "System uninstall requires root privileges."
        echo "Run: sudo ./uninstall.sh --system"
        exit 1
    fi
else
    INSTALL_DIR="$HOME/.local/share/koware"
    BIN_DIR="$HOME/.local/bin"
fi

echo "This will remove:"
echo "  - $INSTALL_DIR"
echo "  - $BIN_DIR/koware (symlink)"
echo ""
echo "Your configuration in ~/.config/koware will be preserved."
echo ""

read -p "Continue? [y/N] " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Cancelled."
    exit 0
fi

# Remove symlink
if [ -L "$BIN_DIR/koware" ]; then
    rm "$BIN_DIR/koware"
    info "Removed symlink $BIN_DIR/koware"
fi

# Remove installation directory
if [ -d "$INSTALL_DIR" ]; then
    rm -rf "$INSTALL_DIR"
    info "Removed $INSTALL_DIR"
fi

echo ""
success "Koware uninstalled."
echo ""
echo "Your config files are still in ~/.config/koware"
echo "To remove them: rm -rf ~/.config/koware"
UNINSTALL_SCRIPT
chmod +x "$STAGING/koware/uninstall.sh"

# Create README
cat > "$STAGING/koware/README.txt" << EOF
Koware - Version $APP_VERSION
================================

A console-first anime/manga link aggregator for Linux.

INSTALLATION
------------

Option 1: User install (recommended)
  ./install.sh
  
  Installs to ~/.local/share/koware with symlink in ~/.local/bin

Option 2: System-wide install
  sudo ./install.sh --system
  
  Installs to /opt/koware with symlink in /usr/local/bin

Option 3: Manual installation
  # Copy to a directory in your PATH
  sudo cp koware /usr/local/bin/
  sudo chmod +x /usr/local/bin/koware
  
  # Create config directory
  mkdir -p ~/.config/koware

QUICK START
-----------

  koware --help              Show all commands
  koware provider autoconfig Auto-configure providers
  koware provider test       Test provider connectivity
  koware search "anime"      Search for anime
  koware watch-together create "anime" --episode 1

CONFIGURATION
-------------

Config file location: ~/.config/koware/appsettings.user.json

UNINSTALLATION
--------------

  ./uninstall.sh             Remove user installation
  sudo ./uninstall.sh -s     Remove system installation

For more information, visit: https://github.com/S1mplector/Koware
EOF

# Create desktop entry file for optional GUI integration
cat > "$STAGING/koware/koware.desktop" << EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=Koware
Comment=$APP_DESCRIPTION
Exec=koware %u
Icon=koware
Terminal=true
Categories=AudioVideo;Video;Network;
Keywords=anime;manga;stream;player;
MimeType=x-scheme-handler/koware;
EOF

# Create tarball
if [ "$CREATE_TARBALL" = "true" ]; then
    info "Creating tarball: $TARBALL_NAME"
    mkdir -p "$DIST_OUTPUT_DIR"
    TARBALL_PATH="$DIST_OUTPUT_DIR/$TARBALL_NAME"
    
    # Create tarball from staging directory
    tar -czf "$TARBALL_PATH" -C "$STAGING" koware
    
    success "Tarball created: $TARBALL_PATH"
    echo "  Size: $(du -h "$TARBALL_PATH" | cut -f1)"
fi

# Create .deb package
if [ "$CREATE_DEB" = "true" ]; then
    if ! command -v dpkg-deb &> /dev/null; then
        warn "dpkg-deb not found, skipping .deb creation"
    else
        info "Creating .deb package: $DEB_NAME"
        
        DEB_ROOT="$OUTPUT_DIR/deb-build"
        rm -rf "$DEB_ROOT"
        mkdir -p "$DEB_ROOT/DEBIAN"
        mkdir -p "$DEB_ROOT/opt/koware"
        mkdir -p "$DEB_ROOT/opt/koware/player"
        mkdir -p "$DEB_ROOT/usr/local/bin"
        mkdir -p "$DEB_ROOT/usr/share/applications"
        
        # Copy files
        cp "$OUTPUT_DIR/cli/koware" "$DEB_ROOT/opt/koware/"
        [ -f "$OUTPUT_DIR/cli/appsettings.json" ] && cp "$OUTPUT_DIR/cli/appsettings.json" "$DEB_ROOT/opt/koware/"
        if [ -f "$OUTPUT_DIR/player/Koware.Player" ]; then
            cp -R "$OUTPUT_DIR/player/"* "$DEB_ROOT/opt/koware/player/"
        fi
        
        # Create symlink (relative for packaging)
        ln -s /opt/koware/koware "$DEB_ROOT/usr/local/bin/koware"
        
        # Desktop file
        cp "$STAGING/koware/koware.desktop" "$DEB_ROOT/usr/share/applications/"
        
        # Control file
        cat > "$DEB_ROOT/DEBIAN/control" << EOF
Package: koware
Version: $APP_VERSION
Section: video
Priority: optional
Architecture: $ARCH
Maintainer: $APP_MAINTAINER
EOF
        if [ -f "$OUTPUT_DIR/player/Koware.Player" ]; then
            echo "Depends: libvlc5, vlc-plugin-base" >> "$DEB_ROOT/DEBIAN/control"
        fi
        cat >> "$DEB_ROOT/DEBIAN/control" << EOF
Description: $APP_DESCRIPTION
 Koware is a standalone, console-first link/stream aggregator that helps
 you search for anime/manga and open streams in a video player from your
 terminal. The bundled Linux player uses LibVLC for playback.
Homepage: https://github.com/S1mplector/Koware
EOF

        # Post-install script
        cat > "$DEB_ROOT/DEBIAN/postinst" << 'EOF'
#!/bin/bash
set -e
chmod +x /opt/koware/koware
chmod +x /opt/koware/player/Koware.Player 2>/dev/null || true
echo "Koware installed! Run 'koware --help' to get started."
EOF
        chmod 755 "$DEB_ROOT/DEBIAN/postinst"

        # Post-remove script
        cat > "$DEB_ROOT/DEBIAN/postrm" << 'EOF'
#!/bin/bash
set -e
if [ "$1" = "purge" ]; then
    rm -rf /opt/koware
fi
EOF
        chmod 755 "$DEB_ROOT/DEBIAN/postrm"

        # Build .deb
        DEB_PATH="$DIST_OUTPUT_DIR/$DEB_NAME"
        dpkg-deb --build "$DEB_ROOT" "$DEB_PATH"
        
        success ".deb package created: $DEB_PATH"
        echo "  Size: $(du -h "$DEB_PATH" | cut -f1)"
        echo "  Install with: sudo dpkg -i $DEB_NAME"
    fi
fi

# Create AppImage
if [ "$CREATE_APPIMAGE" = "true" ]; then
    if ! command -v appimagetool &> /dev/null; then
        warn "appimagetool not found, skipping AppImage creation"
        echo "  Install from: https://github.com/AppImage/AppImageKit/releases"
    else
        info "Creating AppImage..."
        
        APPDIR="$OUTPUT_DIR/Koware.AppDir"
        rm -rf "$APPDIR"
        mkdir -p "$APPDIR/usr/bin"
        mkdir -p "$APPDIR/usr/share/applications"
        mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
        
        # Copy executable
        cp "$OUTPUT_DIR/cli/koware" "$APPDIR/usr/bin/"
        [ -f "$OUTPUT_DIR/cli/appsettings.json" ] && cp "$OUTPUT_DIR/cli/appsettings.json" "$APPDIR/usr/bin/"
        if [ -f "$OUTPUT_DIR/player/Koware.Player" ]; then
            mkdir -p "$APPDIR/usr/bin/player"
            cp -R "$OUTPUT_DIR/player/"* "$APPDIR/usr/bin/player/"
            chmod +x "$APPDIR/usr/bin/player/Koware.Player" 2>/dev/null || true
        fi
        
        # Desktop file
        cp "$STAGING/koware/koware.desktop" "$APPDIR/"
        cp "$STAGING/koware/koware.desktop" "$APPDIR/usr/share/applications/"
        
        # AppRun
        cat > "$APPDIR/AppRun" << 'EOF'
#!/bin/bash
SELF=$(readlink -f "$0")
HERE=${SELF%/*}
exec "$HERE/usr/bin/koware" "$@"
EOF
        chmod +x "$APPDIR/AppRun"
        
        # Create a simple icon (placeholder)
        # For now, create a minimal SVG icon
        cat > "$APPDIR/koware.svg" << 'EOF'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <rect width="256" height="256" rx="32" fill="#1a1a2e"/>
  <text x="128" y="160" font-family="monospace" font-size="120" fill="#00d9ff" text-anchor="middle">K</text>
</svg>
EOF
        cp "$APPDIR/koware.svg" "$APPDIR/usr/share/icons/hicolor/256x256/apps/"
        
        # Build AppImage
        APPIMAGE_NAME="Koware-${APP_VERSION}-${ARCH_SHORT}.AppImage"
        APPIMAGE_PATH="$DIST_OUTPUT_DIR/$APPIMAGE_NAME"
        
        ARCH="$ARCH_SHORT" appimagetool "$APPDIR" "$APPIMAGE_PATH"
        
        success "AppImage created: $APPIMAGE_PATH"
        echo "  Size: $(du -h "$APPIMAGE_PATH" | cut -f1)"
        echo "  Run with: chmod +x $APPIMAGE_NAME && ./$APPIMAGE_NAME"
    fi
fi

# Summary
echo ""
echo "════════════════════════════════════════"
success "Build complete!"
echo "════════════════════════════════════════"
echo ""
echo "Output directory: $OUTPUT_DIR"
echo ""
echo "Created packages:"
[ "$CREATE_TARBALL" = "true" ] && [ -f "$DIST_OUTPUT_DIR/$TARBALL_NAME" ] && echo "  - $TARBALL_NAME"
[ "$CREATE_DEB" = "true" ] && [ -f "$DIST_OUTPUT_DIR/$DEB_NAME" ] && echo "  - $DEB_NAME"
[ "$CREATE_APPIMAGE" = "true" ] && [ -f "$DIST_OUTPUT_DIR/$APPIMAGE_NAME" ] && echo "  - $APPIMAGE_NAME"
echo ""
echo "Installation:"
echo "  tar -xzf $TARBALL_NAME && cd koware && ./install.sh"
echo ""
