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

# Step 2: Create the Installer.app bundle
APP_BUNDLE="$BUILD_DIR/Koware Installer.app"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

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

Install location: $INSTALL_DIR/koware
Config location: $CONFIG_DIR/

Click Install to set up Koware on your Mac.
You may be prompted for your administrator password." buttons $buttons default button "$default_btn" with title "Koware Installer v$VERSION" with icon note
EOF
}

# ============== INSTALL ==============
do_install() {
    # Use AppleScript to get admin privileges and install
    osascript << EOF 2>/dev/null
do shell script "mkdir -p '$INSTALL_DIR' && cp '$RESOURCES_DIR/koware' '$INSTALL_DIR/' && chmod +x '$INSTALL_DIR/koware'" with administrator privileges
EOF
    
    if [ $? -ne 0 ]; then
        osascript -e 'display dialog "Installation cancelled or failed." buttons {"OK"} with title "Koware Installer" with icon stop'
        return 1
    fi

    # Note: Config directory will be created by koware on first run with correct permissions

    osascript << 'EOF'
display dialog "Koware installed successfully!

Open Terminal and run:
    koware --help

to get started." buttons {"OK"} default button "OK" with title "Koware Installer" with icon note
EOF
    return 0
}

# ============== UNINSTALL ==============
do_uninstall() {
    # Confirm removal
    local result=$(osascript << 'EOF'
display dialog "This will remove Koware from this machine, including its files. Your config at ~/.config/koware/ will be preserved.

Do you want to continue?" buttons {"Cancel", "Remove"} default button "Cancel" with title "Remove Koware" with icon caution
EOF
)
    
    if [[ "$result" != *"Remove"* ]]; then
        return 1
    fi

    # Remove with admin privileges
    osascript << EOF 2>/dev/null
do shell script "rm -f '$INSTALL_DIR/koware'" with administrator privileges
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

# Step 3: Create DMG staging
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
