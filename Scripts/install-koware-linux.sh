#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: One-line installer for Koware on Linux.
# Usage: curl -fsSL https://raw.githubusercontent.com/S1mplector/Koware/main/Scripts/install-koware-linux.sh | bash
#    or: wget -qO- https://raw.githubusercontent.com/S1mplector/Koware/main/Scripts/install-koware-linux.sh | bash

set -e

# Configuration
GITHUB_OWNER="S1mplector"
GITHUB_REPO="Koware"
INSTALL_DIR="${KOWARE_INSTALL_DIR:-$HOME/.local/share/koware}"
BIN_DIR="${KOWARE_BIN_DIR:-$HOME/.local/bin}"
CONFIG_DIR="$HOME/.config/koware"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

info()    { echo -e "${CYAN}[INFO]${NC} $1"; }
success() { echo -e "${GREEN}[OK  ]${NC} $1"; }
warn()    { echo -e "${YELLOW}[WARN]${NC} $1"; }
err()     { echo -e "${RED}[ERR ]${NC} $1"; }

# Print banner
echo ""
echo -e "${CYAN}╔════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║${NC}         ${GREEN}Koware Linux Installer${NC}                    ${CYAN}║${NC}"
echo -e "${CYAN}║${NC}         Console-first anime/manga aggregator        ${CYAN}║${NC}"
echo -e "${CYAN}╚════════════════════════════════════════════════════╝${NC}"
echo ""

# Detect architecture
detect_arch() {
    local arch=$(uname -m)
    case "$arch" in
        x86_64|amd64)
            echo "x64"
            ;;
        aarch64|arm64)
            echo "arm64"
            ;;
        armv7l|armhf)
            echo "arm"
            ;;
        *)
            err "Unsupported architecture: $arch"
            exit 1
            ;;
    esac
}

# Detect if running on musl (Alpine, etc.)
detect_libc() {
    if ldd --version 2>&1 | grep -q musl; then
        echo "musl"
    else
        echo "glibc"
    fi
}

# Check for required tools
check_requirements() {
    local missing=()
    
    if ! command -v curl &> /dev/null && ! command -v wget &> /dev/null; then
        missing+=("curl or wget")
    fi
    
    if ! command -v tar &> /dev/null; then
        missing+=("tar")
    fi
    
    if [ ${#missing[@]} -gt 0 ]; then
        err "Missing required tools: ${missing[*]}"
        echo "Please install them and try again."
        exit 1
    fi
}

# Download function that works with curl or wget
download() {
    local url="$1"
    local output="$2"
    
    if command -v curl &> /dev/null; then
        curl -fsSL "$url" -o "$output"
    elif command -v wget &> /dev/null; then
        wget -q "$url" -O "$output"
    else
        err "Neither curl nor wget found"
        exit 1
    fi
}

# Download JSON from URL
download_json() {
    local url="$1"
    
    if command -v curl &> /dev/null; then
        curl -fsSL "$url"
    elif command -v wget &> /dev/null; then
        wget -qO- "$url"
    fi
}

# Get latest release info from GitHub
get_latest_release() {
    local api_url="https://api.github.com/repos/${GITHUB_OWNER}/${GITHUB_REPO}/releases?per_page=1"
    local release_json
    
    info "Checking for latest release..."
    
    release_json=$(download_json "$api_url")
    
    if [ -z "$release_json" ]; then
        err "Failed to fetch release information"
        exit 1
    fi
    
    # Parse JSON (basic grep-based parsing for portability)
    RELEASE_TAG=$(echo "$release_json" | grep -o '"tag_name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | cut -d'"' -f4)
    RELEASE_NAME=$(echo "$release_json" | grep -o '"name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | cut -d'"' -f4)
    
    if [ -z "$RELEASE_TAG" ]; then
        err "Could not determine latest version"
        exit 1
    fi
    
    info "Latest version: $RELEASE_TAG"
}

# Find the appropriate asset for this platform
find_asset_url() {
    local arch="$1"
    local libc="$2"
    local api_url="https://api.github.com/repos/${GITHUB_OWNER}/${GITHUB_REPO}/releases?per_page=1"
    local release_json
    
    release_json=$(download_json "$api_url")
    
    # Build expected asset name patterns
    local patterns=(
        "linux-${arch}.tar.gz"
        "linux-${arch}"
    )
    
    if [ "$libc" = "musl" ]; then
        patterns=("linux-musl-${arch}.tar.gz" "${patterns[@]}")
    fi
    
    # Try to find matching asset
    for pattern in "${patterns[@]}"; do
        local url=$(echo "$release_json" | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*'"$pattern"'[^"]*"' | head -1 | cut -d'"' -f4)
        if [ -n "$url" ]; then
            ASSET_URL="$url"
            ASSET_NAME=$(basename "$url")
            return 0
        fi
    done
    
    # Fallback: try any linux tarball
    ASSET_URL=$(echo "$release_json" | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*linux[^"]*\.tar\.gz"' | head -1 | cut -d'"' -f4)
    if [ -n "$ASSET_URL" ]; then
        ASSET_NAME=$(basename "$ASSET_URL")
        warn "Using fallback asset: $ASSET_NAME"
        return 0
    fi
    
    return 1
}

# Install from source (build locally)
install_from_source() {
    info "No pre-built binary found. Building from source..."
    
    # Check for .NET SDK
    if ! command -v dotnet &> /dev/null; then
        err ".NET SDK not found. Please install it first:"
        echo ""
        echo "  # Ubuntu/Debian:"
        echo "  sudo apt-get update && sudo apt-get install -y dotnet-sdk-9.0"
        echo ""
        echo "  # Fedora:"
        echo "  sudo dnf install dotnet-sdk-9.0"
        echo ""
        echo "  # Arch:"
        echo "  sudo pacman -S dotnet-sdk"
        echo ""
        echo "  # Or visit: https://dotnet.microsoft.com/download"
        exit 1
    fi
    
    local tmp_dir=$(mktemp -d)
    trap "rm -rf $tmp_dir" EXIT
    
    info "Cloning repository..."
    git clone --depth 1 "https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}.git" "$tmp_dir/koware"
    
    info "Building..."
    cd "$tmp_dir/koware"
    
    # Run the Linux publish script if it exists
    if [ -f "Scripts/publish-linux.sh" ]; then
        chmod +x Scripts/publish-linux.sh
        ./Scripts/publish-linux.sh --output "$tmp_dir/output"
        
        # Install from the built package
        if [ -f "$tmp_dir/output/cli/koware" ]; then
            mkdir -p "$INSTALL_DIR"
            cp "$tmp_dir/output/cli/koware" "$INSTALL_DIR/"
            chmod +x "$INSTALL_DIR/koware"
            [ -f "$tmp_dir/output/cli/appsettings.json" ] && cp "$tmp_dir/output/cli/appsettings.json" "$INSTALL_DIR/"
        fi
    else
        # Manual build
        dotnet publish Koware.Cli/Koware.Cli.csproj \
            -c Release \
            -r "linux-$(detect_arch)" \
            -o "$tmp_dir/output" \
            /p:PublishSingleFile=true \
            --self-contained true
        
        mkdir -p "$INSTALL_DIR"
        cp "$tmp_dir/output/Koware.Cli" "$INSTALL_DIR/koware"
        chmod +x "$INSTALL_DIR/koware"
        [ -f "$tmp_dir/output/appsettings.json" ] && cp "$tmp_dir/output/appsettings.json" "$INSTALL_DIR/"
    fi
}

# Install from pre-built binary
install_from_binary() {
    local url="$1"
    local filename="$2"
    local tmp_dir=$(mktemp -d)
    trap "rm -rf $tmp_dir" EXIT
    
    info "Downloading $filename..."
    download "$url" "$tmp_dir/$filename"
    
    info "Extracting..."
    cd "$tmp_dir"
    tar -xzf "$filename"
    
    # Find the koware executable
    local koware_exe=$(find . -name "koware" -type f -executable 2>/dev/null | head -1)
    if [ -z "$koware_exe" ]; then
        koware_exe=$(find . -name "koware" -type f 2>/dev/null | head -1)
    fi
    
    if [ -z "$koware_exe" ]; then
        err "Could not find koware executable in archive"
        exit 1
    fi
    
    # Create installation directory
    mkdir -p "$INSTALL_DIR"
    
    # Copy files
    cp "$koware_exe" "$INSTALL_DIR/koware"
    chmod +x "$INSTALL_DIR/koware"
    
    # Copy appsettings if present
    local settings=$(find . -name "appsettings.json" -type f 2>/dev/null | head -1)
    if [ -n "$settings" ]; then
        cp "$settings" "$INSTALL_DIR/"
    fi
    
    success "Installed koware to $INSTALL_DIR"
}

# Setup PATH and symlinks
setup_path() {
    mkdir -p "$BIN_DIR"
    
    # Create symlink
    ln -sf "$INSTALL_DIR/koware" "$BIN_DIR/koware"
    info "Created symlink: $BIN_DIR/koware -> $INSTALL_DIR/koware"
    
    # Check if BIN_DIR is in PATH
    if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
        warn "$BIN_DIR is not in your PATH"
        echo ""
        
        # Detect shell and config file
        local shell_name=$(basename "$SHELL")
        local config_file=""
        
        case "$shell_name" in
            bash)
                if [ -f "$HOME/.bashrc" ]; then
                    config_file="$HOME/.bashrc"
                elif [ -f "$HOME/.bash_profile" ]; then
                    config_file="$HOME/.bash_profile"
                fi
                ;;
            zsh)
                config_file="$HOME/.zshrc"
                ;;
            fish)
                config_file="$HOME/.config/fish/config.fish"
                ;;
            *)
                config_file="$HOME/.profile"
                ;;
        esac
        
        local path_export='export PATH="$HOME/.local/bin:$PATH"'
        if [ "$shell_name" = "fish" ]; then
            path_export='set -gx PATH $HOME/.local/bin $PATH'
        fi
        
        echo "Add the following to your shell config ($config_file):"
        echo ""
        echo "  $path_export"
        echo ""
        
        # Interactive mode - offer to add automatically
        if [ -t 0 ]; then
            read -p "Add to $config_file automatically? [y/N] " -n 1 -r
            echo
            if [[ $REPLY =~ ^[Yy]$ ]]; then
                echo "" >> "$config_file"
                echo "# Koware - added by installer" >> "$config_file"
                echo "$path_export" >> "$config_file"
                success "Added to $config_file"
            fi
        fi
    fi
}

# Setup config directory
setup_config() {
    mkdir -p "$CONFIG_DIR"
    
    # Copy default config if not exists
    if [ ! -f "$CONFIG_DIR/appsettings.user.json" ]; then
        if [ -f "$INSTALL_DIR/appsettings.json" ]; then
            cp "$INSTALL_DIR/appsettings.json" "$CONFIG_DIR/appsettings.user.json"
            info "Created default config: $CONFIG_DIR/appsettings.user.json"
        fi
    fi
}

# Create desktop entry
create_desktop_entry() {
    local desktop_dir="$HOME/.local/share/applications"
    mkdir -p "$desktop_dir"
    
    cat > "$desktop_dir/koware.desktop" << EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=Koware
Comment=Console-first anime/manga link aggregator
Exec=$BIN_DIR/koware %u
Terminal=true
Categories=AudioVideo;Video;Network;
Keywords=anime;manga;stream;player;
MimeType=x-scheme-handler/koware;
EOF
    
    # Update desktop database if available
    if command -v update-desktop-database &> /dev/null; then
        update-desktop-database "$desktop_dir" 2>/dev/null || true
    fi
}

# Create uninstall script
create_uninstall_script() {
    cat > "$INSTALL_DIR/uninstall.sh" << 'EOF'
#!/bin/bash
set -e

echo "Uninstalling Koware..."

# Remove symlink
rm -f "$HOME/.local/bin/koware"

# Remove installation
rm -rf "$HOME/.local/share/koware"

# Remove desktop entry
rm -f "$HOME/.local/share/applications/koware.desktop"

echo ""
echo "Koware uninstalled."
echo "Your config is preserved at: ~/.config/koware"
echo "To remove it: rm -rf ~/.config/koware"
EOF
    chmod +x "$INSTALL_DIR/uninstall.sh"
}

# Verify installation
verify_installation() {
    if [ -x "$INSTALL_DIR/koware" ]; then
        local version=$("$INSTALL_DIR/koware" --version 2>/dev/null || echo "unknown")
        success "Koware installed successfully!"
        echo ""
        echo "  Version:  $version"
        echo "  Location: $INSTALL_DIR/koware"
        echo "  Symlink:  $BIN_DIR/koware"
        echo "  Config:   $CONFIG_DIR/"
        return 0
    else
        err "Installation verification failed"
        return 1
    fi
}

# Print post-install instructions
print_instructions() {
    echo ""
    echo -e "${GREEN}════════════════════════════════════════════════════${NC}"
    echo -e "${GREEN}  Installation Complete!${NC}"
    echo -e "${GREEN}════════════════════════════════════════════════════${NC}"
    echo ""
    echo "Get started:"
    echo ""
    echo "  # If PATH was updated, reload your shell first:"
    echo "  source ~/.bashrc  # or ~/.zshrc"
    echo ""
    echo "  # Configure providers (required before first use):"
    echo "  koware provider autoconfig"
    echo ""
    echo "  # Test connectivity:"
    echo "  koware provider test"
    echo ""
    echo "  # Search for anime:"
    echo "  koware search \"your anime\""
    echo ""
    echo "  # Get help:"
    echo "  koware --help"
    echo ""
    echo "To uninstall: $INSTALL_DIR/uninstall.sh"
    echo ""
}

# Main installation flow
main() {
    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case $1 in
            --source)
                FORCE_SOURCE=true
                shift
                ;;
            --help|-h)
                echo "Koware Linux Installer"
                echo ""
                echo "Usage: $0 [options]"
                echo ""
                echo "Options:"
                echo "  --source    Force building from source"
                echo "  --help      Show this help"
                echo ""
                echo "Environment variables:"
                echo "  KOWARE_INSTALL_DIR  Installation directory (default: ~/.local/share/koware)"
                echo "  KOWARE_BIN_DIR      Binary directory (default: ~/.local/bin)"
                exit 0
                ;;
            *)
                warn "Unknown option: $1"
                shift
                ;;
        esac
    done
    
    check_requirements
    
    local arch=$(detect_arch)
    local libc=$(detect_libc)
    info "Detected: $arch ($libc)"
    
    get_latest_release
    
    if [ "${FORCE_SOURCE:-false}" = "true" ]; then
        install_from_source
    elif find_asset_url "$arch" "$libc"; then
        install_from_binary "$ASSET_URL" "$ASSET_NAME"
    else
        warn "No pre-built binary found for linux-$arch"
        install_from_source
    fi
    
    setup_path
    setup_config
    create_desktop_entry
    create_uninstall_script
    verify_installation
    print_instructions
}

main "$@"
