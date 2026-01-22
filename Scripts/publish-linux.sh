#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: Publishes Koware CLI for Linux and creates distributable packages.

set -e

# Configuration
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-linux-x64}"  # linux-x64, linux-arm64, linux-musl-x64
SELF_CONTAINED="${SELF_CONTAINED:-true}"
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
    "/p:EnableCompressionInSingleFile=true"
)

if [ "$SELF_CONTAINED" = "true" ]; then
    PUBLISH_ARGS+=("--self-contained" "true")
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

# Create staging directory for distribution
STAGING="$OUTPUT_DIR/staging"
mkdir -p "$STAGING/koware"

# Copy files to staging
cp "$OUTPUT_DIR/cli/koware" "$STAGING/koware/"
[ -f "$OUTPUT_DIR/cli/appsettings.json" ] && cp "$OUTPUT_DIR/cli/appsettings.json" "$STAGING/koware/"

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
    if [[ ":$PATH:" != *":$HOME/.local/bin:"* ]]; then
        warn "$BIN_DIR is not in your PATH"
        echo ""
        echo "Add the following to your shell configuration (~/.bashrc, ~/.zshrc, etc.):"
        echo ""
        echo '  export PATH="$HOME/.local/bin:$PATH"'
        echo ""
        echo "Then restart your shell or run: source ~/.bashrc"
        echo ""
        
        # Offer to add to PATH automatically
        read -p "Would you like to add this to ~/.bashrc automatically? [y/N] " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            echo '' >> "$HOME/.bashrc"
            echo '# Koware - added by installer' >> "$HOME/.bashrc"
            echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.bashrc"
            success "Added to ~/.bashrc"
            echo "Run 'source ~/.bashrc' to apply changes."
        fi
    fi
fi

echo ""
success "Koware installed successfully!"
echo ""
echo "  Installation: $INSTALL_DIR/koware"
echo "  Symlink:      $BIN_DIR/koware"
echo "  Config:       $CONFIG_DIR/"
echo ""
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
        mkdir -p "$DEB_ROOT/usr/local/bin"
        mkdir -p "$DEB_ROOT/usr/share/applications"
        
        # Copy files
        cp "$OUTPUT_DIR/cli/koware" "$DEB_ROOT/opt/koware/"
        [ -f "$OUTPUT_DIR/cli/appsettings.json" ] && cp "$OUTPUT_DIR/cli/appsettings.json" "$DEB_ROOT/opt/koware/"
        
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
Description: $APP_DESCRIPTION
 Koware is a standalone, console-first link/stream aggregator that helps
 you search for anime/manga and open streams in a video player from your
 terminal. It requires no external dependencies.
Homepage: https://github.com/S1mplector/Koware
EOF

        # Post-install script
        cat > "$DEB_ROOT/DEBIAN/postinst" << 'EOF'
#!/bin/bash
set -e
chmod +x /opt/koware/koware
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
        
        # Create a simple icon (placeholder - in production you'd include real icons)
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
