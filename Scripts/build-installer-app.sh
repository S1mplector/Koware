#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: Creates a clickable Installer.app for Koware matching Windows installer UX.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# Configuration
RUNTIME="${RUNTIME:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"
APP_VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj")
COPY_TO_DESKTOP="${COPY_TO_DESKTOP:-false}"

# Output paths
BUILD_DIR="$REPO_ROOT/publish/macos-app"
OUTPUT_DIR="$REPO_ROOT/publish"
DMG_NAME="Koware-Installer-${APP_VERSION}-${RUNTIME}.dmg"

info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; exit 1; }

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --runtime) RUNTIME="$2"; shift 2 ;;
        --copy-to-desktop) COPY_TO_DESKTOP="true"; shift ;;
        --help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  --runtime <rid>       Runtime identifier (osx-arm64, osx-x64). Default: osx-arm64"
            echo "  --copy-to-desktop     Copy the DMG to ~/Desktop after build"
            exit 0
            ;;
        *) err "Unknown option: $1" ;;
    esac
done

info "Building Koware Installer App v$APP_VERSION ($RUNTIME)"

# Clean
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Step 1: Publish CLI
info "Publishing Koware CLI..."
PUBLISH_DIR="$BUILD_DIR/cli"
dotnet publish "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    -o "$PUBLISH_DIR" \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:EnableCompressionInSingleFile=true \
    --self-contained true

mv "$PUBLISH_DIR/Koware.Cli" "$PUBLISH_DIR/koware"
chmod +x "$PUBLISH_DIR/koware"

# Step 1b: Publish Avalonia Player
info "Publishing Koware Player (Avalonia)..."
PLAYER_DIR="$BUILD_DIR/player"
dotnet publish "$REPO_ROOT/Koware.Player/Koware.Player.csproj" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    -o "$PLAYER_DIR" \
    --self-contained true

chmod +x "$PLAYER_DIR/Koware.Player" 2>/dev/null || true

# Step 1c: Publish Avalonia Reader
info "Publishing Koware Reader (Avalonia)..."
READER_DIR="$BUILD_DIR/reader"
dotnet publish "$REPO_ROOT/Koware.Reader/Koware.Reader.csproj" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    -o "$READER_DIR" \
    --self-contained true

chmod +x "$READER_DIR/Koware.Reader" 2>/dev/null || true

# Step 1d: Publish Avalonia Browser
info "Publishing Koware Browser (Avalonia)..."
BROWSER_DIR="$BUILD_DIR/browser"
dotnet publish "$REPO_ROOT/Koware.Browser/Koware.Browser.csproj" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    -o "$BROWSER_DIR" \
    --self-contained true

chmod +x "$BROWSER_DIR/Koware.Browser" 2>/dev/null || true

# Step 2: Create macOS icon from PNG
info "Creating app icon..."
LOGO_PNG="$REPO_ROOT/Assets/Logo/logo.png"
ICONSET_DIR="$BUILD_DIR/AppIcon.iconset"

if [ -f "$LOGO_PNG" ]; then
    mkdir -p "$ICONSET_DIR"
    
    # Generate all required icon sizes using sips
    sips -z 16 16     "$LOGO_PNG" --out "$ICONSET_DIR/icon_16x16.png" 2>/dev/null
    sips -z 32 32     "$LOGO_PNG" --out "$ICONSET_DIR/icon_16x16@2x.png" 2>/dev/null
    sips -z 32 32     "$LOGO_PNG" --out "$ICONSET_DIR/icon_32x32.png" 2>/dev/null
    sips -z 64 64     "$LOGO_PNG" --out "$ICONSET_DIR/icon_32x32@2x.png" 2>/dev/null
    sips -z 128 128   "$LOGO_PNG" --out "$ICONSET_DIR/icon_128x128.png" 2>/dev/null
    sips -z 256 256   "$LOGO_PNG" --out "$ICONSET_DIR/icon_128x128@2x.png" 2>/dev/null
    sips -z 256 256   "$LOGO_PNG" --out "$ICONSET_DIR/icon_256x256.png" 2>/dev/null
    sips -z 512 512   "$LOGO_PNG" --out "$ICONSET_DIR/icon_256x256@2x.png" 2>/dev/null
    sips -z 512 512   "$LOGO_PNG" --out "$ICONSET_DIR/icon_512x512.png" 2>/dev/null
    sips -z 1024 1024 "$LOGO_PNG" --out "$ICONSET_DIR/icon_512x512@2x.png" 2>/dev/null
    
    # Convert iconset to icns
    iconutil -c icns "$ICONSET_DIR" -o "$BUILD_DIR/AppIcon.icns" 2>/dev/null
    
    if [ -f "$BUILD_DIR/AppIcon.icns" ]; then
        info "App icon created successfully"
    else
        warn "Failed to create .icns icon, app will use default icon"
    fi
else
    warn "Logo not found at $LOGO_PNG, app will use default icon"
fi

# Step 3: Create the Installer.app bundle
APP_BUNDLE="$BUILD_DIR/Koware Installer.app"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy icon if created
if [ -f "$BUILD_DIR/AppIcon.icns" ]; then
    cp "$BUILD_DIR/AppIcon.icns" "$APP_BUNDLE/Contents/Resources/"
fi

# Create Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>installer</string>
    <key>CFBundleIdentifier</key>
    <string>com.koware.installer</string>
    <key>CFBundleName</key>
    <string>Koware Installer</string>
    <key>CFBundleDisplayName</key>
    <string>Koware Installer</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleVersion</key>
    <string>$APP_VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$APP_VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

# Copy the CLI binary and config to Resources
cp "$PUBLISH_DIR/koware" "$APP_BUNDLE/Contents/Resources/"
if [ -f "$PUBLISH_DIR/appsettings.json" ]; then
    cp "$PUBLISH_DIR/appsettings.json" "$APP_BUNDLE/Contents/Resources/"
fi

# Copy Player and Reader to Resources
if [ -d "$PLAYER_DIR" ]; then
    mkdir -p "$APP_BUNDLE/Contents/Resources/player"
    cp -r "$PLAYER_DIR/"* "$APP_BUNDLE/Contents/Resources/player/"
fi
if [ -d "$READER_DIR" ]; then
    mkdir -p "$APP_BUNDLE/Contents/Resources/reader"
    cp -r "$READER_DIR/"* "$APP_BUNDLE/Contents/Resources/reader/"
fi
if [ -d "$BROWSER_DIR" ]; then
    mkdir -p "$APP_BUNDLE/Contents/Resources/browser"
    cp -r "$BROWSER_DIR/"* "$APP_BUNDLE/Contents/Resources/browser/"
fi

# Copy Usage Notice
if [ -f "$REPO_ROOT/Usage-Notice.md" ]; then
    cp "$REPO_ROOT/Usage-Notice.md" "$APP_BUNDLE/Contents/Resources/"
fi

# Create the main installer script (matches Windows installer flow)
cat > "$APP_BUNDLE/Contents/MacOS/installer" << 'SCRIPT'
#!/bin/bash
# Koware Installer for macOS
# Matches Windows installer UX

SCRIPT_DIR="$(dirname "$0")"
RESOURCES_DIR="$SCRIPT_DIR/../Resources"
INSTALL_DIR="/usr/local/bin"
CONFIG_DIR="$HOME/.config/koware"
VERSION="__VERSION__"

# Check if already installed
is_installed() {
    [ -f "$INSTALL_DIR/koware" ]
}

get_installed_version() {
    if is_installed; then
        "$INSTALL_DIR/koware" --version 2>/dev/null | head -1 || echo "unknown"
    fi
}

# ============== WELCOME SCREEN ==============
show_welcome() {
    local installed_info=""
    if is_installed; then
        local ver=$(get_installed_version)
        installed_info="

⚠️ Koware is already installed (version: $ver)"
    fi

    osascript << EOF
display dialog "Welcome to Koware

This installer sets up Koware CLI with a single click.

What you'll get:
• Fresh Release build of Koware CLI
• PATH ready for 'koware' command
• Configuration at ~/.config/koware/$installed_info

Copyright © Ilgaz Mehmetoglu
Koware is distributed under the license bundled with this installer." buttons {"Close", "Continue"} default button "Continue" with title "Koware Installer v$VERSION" with icon note
EOF
}

# ============== USAGE NOTICE ==============
show_usage_notice() {
    local notice_file="$RESOURCES_DIR/Usage-Notice.md"
    local notice_text="Koware usage notice could not be loaded. Please see the repository for details."
    
    if [ -f "$notice_file" ]; then
        # Strip markdown formatting for display
        notice_text=$(cat "$notice_file" | sed 's/^#.*//g' | sed 's/^- /• /g' | tr -s '\n' | head -c 1000)
    fi

    osascript << EOF
display dialog "Usage Notice

Koware is a tool that automates fetching information and streams that are already publicly available on the internet. It does not host or distribute content.

• Koware does NOT include or support bypassing paywalls or DRM
• Do not use Koware to circumvent technical protection measures
• All usage is at your own risk and responsibility
• Ensure you comply with your local laws and site terms of service

By clicking Accept, you acknowledge that you are solely responsible for how you access and use publicly available resources." buttons {"Decline", "Accept"} default button "Accept" with title "Usage Notice" with icon caution
EOF
}

# ============== MAIN INSTALLER SCREEN ==============
show_main_screen() {
    local buttons
    local default_btn
    
    if is_installed; then
        buttons='{"Close", "Remove", "Re-install"}'
        default_btn="Re-install"
    else
        buttons='{"Close", "Install"}'
        default_btn="Install"
    fi

    osascript << EOF
display dialog "Koware Installer

This will install:
• Koware.app → /Applications
• koware CLI → $INSTALL_DIR

Config location: $CONFIG_DIR/

Click Install to set up Koware on your Mac.
You may be prompted for your administrator password." buttons $buttons default button "$default_btn" with title "Koware Installer v$VERSION" with icon note
EOF
}

# ============== INSTALL ==============
do_install() {
    # Create temp script for complex installation
    local INSTALL_SCRIPT=$(mktemp)
    cat > "$INSTALL_SCRIPT" << 'INSTALLSCRIPT'
#!/bin/bash
RESOURCES_DIR="$1"
INSTALL_DIR="$2"
APP_DIR="/Applications/Koware.app"

# 1. Install CLI to /usr/local/bin
mkdir -p "$INSTALL_DIR"
cp "$RESOURCES_DIR/koware" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/koware"

# 2. Create Koware.app bundle in /Applications
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

# Copy Browser as main app
if [ -d "$RESOURCES_DIR/browser" ]; then
    cp -r "$RESOURCES_DIR/browser/"* "$APP_DIR/Contents/MacOS/"
    chmod +x "$APP_DIR/Contents/MacOS/Koware.Browser"
    # Create launcher script
    cat > "$APP_DIR/Contents/MacOS/Koware" << 'LAUNCHER'
#!/bin/bash
DIR="$(dirname "$0")"
exec "$DIR/Koware.Browser" "$@"
LAUNCHER
    chmod +x "$APP_DIR/Contents/MacOS/Koware"
fi

# Copy Player and Reader into app bundle
if [ -d "$RESOURCES_DIR/player" ]; then
    mkdir -p "$APP_DIR/Contents/Resources/player"
    cp -r "$RESOURCES_DIR/player/"* "$APP_DIR/Contents/Resources/player/"
    chmod +x "$APP_DIR/Contents/Resources/player/Koware.Player" 2>/dev/null
fi
if [ -d "$RESOURCES_DIR/reader" ]; then
    mkdir -p "$APP_DIR/Contents/Resources/reader"
    cp -r "$RESOURCES_DIR/reader/"* "$APP_DIR/Contents/Resources/reader/"
    chmod +x "$APP_DIR/Contents/Resources/reader/Koware.Reader" 2>/dev/null
fi

# Copy CLI into app bundle for reference
cp "$RESOURCES_DIR/koware" "$APP_DIR/Contents/Resources/"

# Create Info.plist
cat > "$APP_DIR/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Koware</string>
    <key>CFBundleDisplayName</key>
    <string>Koware</string>
    <key>CFBundleIdentifier</key>
    <string>com.ilgazmehmetoglu.koware</string>
    <key>CFBundleVersion</key>
    <string>0.7.0</string>
    <key>CFBundleShortVersionString</key>
    <string>0.7.0-beta</string>
    <key>CFBundleExecutable</key>
    <string>Koware</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSRequiresAquaSystemAppearance</key>
    <false/>
</dict>
</plist>
PLIST

echo "Installation complete"
INSTALLSCRIPT
    chmod +x "$INSTALL_SCRIPT"

    # Use AppleScript to get admin privileges and run install script
    osascript << EOF 2>/dev/null
do shell script "bash '$INSTALL_SCRIPT' '$RESOURCES_DIR' '$INSTALL_DIR'" with administrator privileges
EOF
    local install_result=$?
    rm -f "$INSTALL_SCRIPT"
    
    if [ $install_result -ne 0 ]; then
        osascript -e 'display dialog "Installation cancelled or failed." buttons {"OK"} with title "Koware Installer" with icon stop'
        return 1
    fi

    osascript << 'EOF'
display dialog "Koware installed successfully!

✓ Koware.app installed to /Applications
✓ CLI available as 'koware' in Terminal

You can now:
• Open Koware from Applications
• Run 'koware --help' in Terminal" buttons {"OK"} default button "OK" with title "Koware Installer" with icon note
EOF
    return 0
}

# ============== UNINSTALL ==============
do_uninstall() {
    # Confirm removal
    local result=$(osascript << 'EOF'
display dialog "This will remove Koware from this machine:
• /Applications/Koware.app
• /usr/local/bin/koware

Your config at ~/.config/koware/ will be preserved.

Do you want to continue?" buttons {"Cancel", "Remove"} default button "Cancel" with title "Remove Koware" with icon caution
EOF
)
    
    if [[ "$result" != *"Remove"* ]]; then
        return 1
    fi

    # Remove with admin privileges
    osascript << EOF 2>/dev/null
do shell script "rm -f '$INSTALL_DIR/koware' && rm -rf '$INSTALL_DIR/koware-apps' && rm -rf '/Applications/Koware.app'" with administrator privileges
EOF

    if [ $? -ne 0 ]; then
        osascript -e 'display dialog "Removal cancelled or failed." buttons {"OK"} with title "Koware Installer" with icon stop'
        return 1
    fi

    osascript << 'EOF'
display dialog "Koware was removed from this machine.

Your configuration at ~/.config/koware/ was preserved." buttons {"OK"} default button "OK" with title "Koware Installer" with icon note
EOF
    return 0
}

# ============== MAIN FLOW ==============
main() {
    # Step 1: Welcome screen
    local result=$(show_welcome)
    if [[ "$result" != *"Continue"* ]]; then
        exit 0
    fi

    # Step 2: Usage notice (must accept)
    result=$(show_usage_notice)
    if [[ "$result" != *"Accept"* ]]; then
        osascript -e 'display dialog "You must accept the Usage Notice to install Koware." buttons {"OK"} with title "Usage Notice" with icon caution'
        exit 0
    fi

    # Step 3: Main installer screen (loop until close)
    while true; do
        result=$(show_main_screen)
        
        if [[ "$result" == *"Install"* ]] || [[ "$result" == *"Re-install"* ]]; then
            do_install
            break
        elif [[ "$result" == *"Remove"* ]]; then
            if do_uninstall; then
                break
            fi
            # If uninstall was cancelled, show main screen again
        else
            # Close
            break
        fi
    done
}

main
SCRIPT

# Replace version placeholder
sed -i '' "s/__VERSION__/$APP_VERSION/g" "$APP_BUNDLE/Contents/MacOS/installer"
chmod +x "$APP_BUNDLE/Contents/MacOS/installer"

# Step 4: Create DMG staging
info "Creating DMG..."
DMG_STAGING="$BUILD_DIR/dmg-staging"
mkdir -p "$DMG_STAGING"

# Copy the installer app
cp -r "$APP_BUNDLE" "$DMG_STAGING/"

# Create a simple README
cat > "$DMG_STAGING/README.txt" << EOF
Koware $APP_VERSION - macOS Installer
=====================================

Double-click "Koware Installer" to install, reinstall, or remove Koware.

After installation, open Terminal and run:
    koware --help

Install location: /usr/local/bin/koware
Config location:  ~/.config/koware/

For more information, visit the project repository.
EOF

# Add volume icon (shows when DMG is mounted)
if [ -f "$BUILD_DIR/AppIcon.icns" ]; then
    cp "$BUILD_DIR/AppIcon.icns" "$DMG_STAGING/.VolumeIcon.icns"
    # Set custom icon flag on the folder
    SetFile -a C "$DMG_STAGING" 2>/dev/null || true
fi

# Create DMG
DMG_PATH="$OUTPUT_DIR/$DMG_NAME"
rm -f "$DMG_PATH"

hdiutil create \
    -volname "Koware Installer" \
    -srcfolder "$DMG_STAGING" \
    -ov \
    -format UDZO \
    "$DMG_PATH"

info "DMG created: $DMG_PATH"

# Set custom icon on DMG file (requires Xcode Command Line Tools)
if [ -f "$BUILD_DIR/AppIcon.icns" ] && command -v Rez &>/dev/null; then
    info "Setting DMG file icon..."
    # Create Rez script to embed icon
    echo "read 'icns' (-16455) \"$BUILD_DIR/AppIcon.icns\";" > "$BUILD_DIR/icon.r"
    Rez -append "$BUILD_DIR/icon.r" -o "$DMG_PATH" 2>/dev/null && \
        SetFile -a C "$DMG_PATH" 2>/dev/null || true
    rm -f "$BUILD_DIR/icon.r" 2>/dev/null
fi

# Copy to Desktop if requested
if [ "$COPY_TO_DESKTOP" = "true" ]; then
    DESKTOP="$HOME/Desktop"
    if [ -d "$DESKTOP" ]; then
        cp "$DMG_PATH" "$DESKTOP/"
        info "Copied DMG to Desktop: $DESKTOP/$DMG_NAME"
    fi
fi

# Cleanup
rm -rf "$BUILD_DIR"

echo ""
info "Build complete!"
echo "  DMG: $DMG_PATH"
echo "  Size: $(du -h "$DMG_PATH" | cut -f1)"
echo ""
info "Open the DMG and double-click 'Koware Installer' to install/reinstall/remove."
