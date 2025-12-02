#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: Creates a styled DMG installer for Koware using only native macOS tools.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# Configuration
APP_NAME="Koware"
APP_VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj" 2>/dev/null || \
              sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj")
RUNTIME="${RUNTIME:-osx-arm64}"
SOURCE_DIR="${SOURCE_DIR:-$REPO_ROOT/publish/macos/dmg-staging}"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/publish}"
DMG_NAME="Koware-${APP_VERSION}-${RUNTIME}.dmg"
VOLUME_NAME="Koware"
DMG_TEMP="$OUTPUT_DIR/dmg-temp"

info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; exit 1; }

# Check if source exists
if [ ! -d "$SOURCE_DIR" ]; then
    err "Source directory not found: $SOURCE_DIR\nRun publish-macos.sh first."
fi

info "Creating DMG for Koware v$APP_VERSION"

# Clean up any previous temp files
rm -rf "$DMG_TEMP"
mkdir -p "$DMG_TEMP"
mkdir -p "$OUTPUT_DIR"

# Copy contents
cp -r "$SOURCE_DIR/"* "$DMG_TEMP/"

# Create a styled DMG
DMG_PATH="$OUTPUT_DIR/$DMG_NAME"
rm -f "$DMG_PATH"

# Create initial writable DMG (larger size for styling)
TEMP_DMG="$OUTPUT_DIR/temp-rw.dmg"
rm -f "$TEMP_DMG"

info "Creating writable DMG..."
hdiutil create -srcfolder "$DMG_TEMP" -volname "$VOLUME_NAME" -fs HFS+ \
    -fsargs "-c c=64,a=16,e=16" -format UDRW -size 200m "$TEMP_DMG"

# Mount the writable DMG
info "Mounting DMG for styling..."
MOUNT_POINT=$(hdiutil attach -readwrite -noverify -noautoopen "$TEMP_DMG" | \
    grep -E '^/dev/' | tail -1 | awk '{print $NF}')

if [ -z "$MOUNT_POINT" ]; then
    err "Failed to mount DMG"
fi

info "Mounted at: $MOUNT_POINT"

# Apply Finder window settings using AppleScript
info "Applying window styling..."
osascript << EOF
tell application "Finder"
    tell disk "$VOLUME_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set bounds of container window to {400, 100, 900, 450}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 72
        -- Position items
        set position of item "koware" of container window to {125, 180}
        set position of item "install.sh" of container window to {375, 180}
        set position of item "README.txt" of container window to {250, 280}
        close
        open
        update without registering applications
        delay 2
    end tell
end tell
EOF

# Wait for Finder to finish
sync
sleep 2

# Unmount
info "Unmounting..."
hdiutil detach "$MOUNT_POINT" -quiet || hdiutil detach "$MOUNT_POINT" -force

# Convert to compressed read-only DMG
info "Compressing DMG..."
hdiutil convert "$TEMP_DMG" -format UDZO -imagekey zlib-level=9 -o "$DMG_PATH"

# Clean up
rm -f "$TEMP_DMG"
rm -rf "$DMG_TEMP"

info "DMG created successfully: $DMG_PATH"
echo "  Size: $(du -h "$DMG_PATH" | cut -f1)"
