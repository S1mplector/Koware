#!/bin/bash
# Author: Ilgaz Mehmetoğlu
# Summary: Low-level helper that turns an existing staging directory into a styled DMG.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
source "$SCRIPT_DIR/lib/macos-packaging.sh"

# Configuration
APP_VERSION="$(macos_read_app_version "$REPO_ROOT")"
RUNTIME="${RUNTIME:-osx-arm64}"
SOURCE_DIR="${SOURCE_DIR:-$REPO_ROOT/publish/macos/dmg-staging}"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/publish}"
VOLUME_NAME="Koware"
OUTPUT_PATH="${OUTPUT_PATH:-}"

info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; exit 1; }

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --source) SOURCE_DIR="$2"; shift 2 ;;
        --output) OUTPUT_PATH="$2"; shift 2 ;;
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        --runtime) RUNTIME="$2"; shift 2 ;;
        --volume-name) VOLUME_NAME="$2"; shift 2 ;;
        --help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  --source <dir>        Staging directory to package. Default: publish/macos/dmg-staging"
            echo "  --output <path>       Final DMG path. Default: publish/Koware-<version>-<runtime>.dmg"
            echo "  --output-dir <dir>    Directory for generated DMG and temp files. Default: publish/"
            echo "  --runtime <rid>       Runtime label used in the default DMG name. Default: osx-arm64"
            echo "  --volume-name <name>  Mounted DMG volume name. Default: Koware"
            exit 0
            ;;
        *) err "Unknown option: $1" ;;
    esac
done

DMG_NAME="Koware-${APP_VERSION}-${RUNTIME}.dmg"
if [ -z "$OUTPUT_PATH" ]; then
    OUTPUT_PATH="$OUTPUT_DIR/$DMG_NAME"
fi

OUTPUT_PARENT="$(dirname "$OUTPUT_PATH")"
DMG_TEMP="$OUTPUT_PARENT/dmg-temp"
TEMP_DMG="$OUTPUT_PARENT/temp-rw.dmg"

# Check if source exists
if [ ! -d "$SOURCE_DIR" ]; then
    err "Source directory not found: $SOURCE_DIR"
fi

info "Creating DMG for Koware v$APP_VERSION"

# Clean up any previous temp files
rm -rf "$DMG_TEMP"
mkdir -p "$DMG_TEMP"
mkdir -p "$OUTPUT_PARENT"

# Copy contents, including dotfiles such as .VolumeIcon.icns.
cp -R "$SOURCE_DIR"/. "$DMG_TEMP/"

# Create a styled DMG
rm -f "$OUTPUT_PATH"

# Create initial writable DMG (larger size for styling)
rm -f "$TEMP_DMG"

info "Creating writable DMG..."
hdiutil create -srcfolder "$DMG_TEMP" -volname "$VOLUME_NAME" -fs HFS+ \
    -fsargs "-c c=64,a=16,e=16" -format UDRW -size 200m "$TEMP_DMG"

# Mount the writable DMG
info "Mounting DMG for styling..."
MOUNT_POINT=$(hdiutil attach -readwrite -noverify -noautoopen "$TEMP_DMG" | \
    awk '/^\/dev\// {
        for (i = 1; i <= NF; i++) {
            if ($i ~ /^\/Volumes\//) {
                mount = $i
                for (j = i + 1; j <= NF; j++) {
                    mount = mount " " $j
                }
                print mount
                exit
            }
        }
    }')

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
hdiutil convert "$TEMP_DMG" -format UDZO -imagekey zlib-level=9 -o "$OUTPUT_PATH"

# Clean up
rm -f "$TEMP_DMG"
rm -rf "$DMG_TEMP"

info "DMG created successfully: $OUTPUT_PATH"
echo "  Size: $(du -h "$OUTPUT_PATH" | cut -f1)"
