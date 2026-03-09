#!/bin/bash
# Author: Ilgaz Mehmetoglu
# Summary: Creates a macOS .pkg installer for Koware using a folder-based runtime bundle.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# Configuration
RUNTIME="${RUNTIME:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"
APP_NAME="Koware"
APP_VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/Koware.Cli/Koware.Cli.csproj")
PKG_IDENTIFIER="com.koware.cli"
INSTALL_LOCATION="/usr/local"
COPY_TO_DESKTOP="${COPY_TO_DESKTOP:-false}"

# Output paths
BUILD_DIR="$REPO_ROOT/publish/macos-pkg"
PKG_ROOT="$BUILD_DIR/root"
PKG_SCRIPTS="$BUILD_DIR/scripts"
PKG_RESOURCES="$BUILD_DIR/resources"
OUTPUT_DIR="$REPO_ROOT/publish"

info() { echo -e "\033[36m[INFO]\033[0m $1"; }
warn() { echo -e "\033[33m[WARN]\033[0m $1"; }
err()  { echo -e "\033[31m[ERR ]\033[0m $1"; exit 1; }

publish_bundle() {
    local rid="$1"
    local app_dir="$PKG_ROOT/lib/koware/$rid"
    local reader_dir="$app_dir/reader"
    local cli_proj="$REPO_ROOT/Koware.Cli/Koware.Cli.csproj"
    local reader_proj="$REPO_ROOT/Koware.Reader/Koware.Reader.csproj"

    mkdir -p "$app_dir" "$reader_dir"

    info "Publishing CLI bundle for $rid"
    dotnet publish "$cli_proj" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        -o "$app_dir" \
        --self-contained true

    info "Publishing reader bundle for $rid"
    dotnet publish "$reader_proj" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        -o "$reader_dir" \
        --self-contained true
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --runtime) RUNTIME="$2"; shift 2 ;;
        --copy-to-desktop) COPY_TO_DESKTOP="true"; shift ;;
        --help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  --runtime <rid>       Runtime identifier (osx-arm64, osx-x64, universal). Default: osx-arm64"
            echo "  --copy-to-desktop     Copy the PKG to ~/Desktop after build"
            exit 0
            ;;
        *) err "Unknown option: $1" ;;
    esac
done

info "Building Koware PKG Installer v$APP_VERSION ($RUNTIME)"

PKG_NAME="Koware-${APP_VERSION}-${RUNTIME}.pkg"

rm -rf "$BUILD_DIR"
mkdir -p "$PKG_ROOT/bin"
mkdir -p "$PKG_ROOT/lib"
mkdir -p "$PKG_SCRIPTS"
mkdir -p "$PKG_RESOURCES"
mkdir -p "$OUTPUT_DIR"

TARGET_RIDS=("$RUNTIME")
if [[ "$RUNTIME" == "universal" ]]; then
    TARGET_RIDS=("osx-arm64" "osx-x64")
fi

for rid in "${TARGET_RIDS[@]}"; do
    publish_bundle "$rid"
done

cat > "$PKG_ROOT/bin/koware" << 'EOF'
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
EOF
chmod +x "$PKG_ROOT/bin/koware"

cat > "$PKG_SCRIPTS/postinstall" << 'EOF'
#!/bin/bash
# Post-installation script for Koware.
# Config directory will be created by koware on first run with correct user permissions.
exit 0
EOF
chmod +x "$PKG_SCRIPTS/postinstall"

cat > "$PKG_RESOURCES/welcome.txt" << EOF
Welcome to Koware Installer

Koware is a console-first anime and manga tool for macOS.

This installer will install:
• koware launcher to /usr/local/bin
• bundled CLI runtime to /usr/local/lib/koware
• bundled cross-platform reader used by 'koware read'

Configuration will be created at ~/.config/koware/ on first run.

After installation, open Terminal and run:
  koware --help

Click Continue to proceed with the installation.
EOF

if [ -f "$REPO_ROOT/LICENSE.md" ]; then
    cp "$REPO_ROOT/LICENSE.md" "$PKG_RESOURCES/license.txt"
else
    cat > "$PKG_RESOURCES/license.txt" << 'EOF'
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
EOF
fi

cat > "$PKG_RESOURCES/conclusion.txt" << EOF
Koware has been installed successfully.

To get started, open Terminal and run:

    koware --help

The CLI runtime and bundled reader are installed at:
    /usr/local/lib/koware/

Your configuration is stored at:
    ~/.config/koware/

Thank you for installing Koware.
EOF

info "Building component package..."
COMPONENT_PKG="$BUILD_DIR/koware-component.pkg"

pkgbuild \
    --root "$PKG_ROOT" \
    --identifier "$PKG_IDENTIFIER" \
    --version "$APP_VERSION" \
    --install-location "$INSTALL_LOCATION" \
    --scripts "$PKG_SCRIPTS" \
    "$COMPONENT_PKG"

cat > "$BUILD_DIR/distribution.xml" << EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="1">
    <title>Koware</title>
    <organization>com.koware</organization>
    <domains enable_localSystem="true"/>
    <options customize="never" require-scripts="true" rootVolumeOnly="true"/>

    <welcome file="welcome.txt"/>
    <license file="license.txt"/>
    <conclusion file="conclusion.txt"/>

    <choices-outline>
        <line choice="default">
            <line choice="koware"/>
        </line>
    </choices-outline>

    <choice id="default"/>
    <choice id="koware" visible="false">
        <pkg-ref id="$PKG_IDENTIFIER"/>
    </choice>

    <pkg-ref id="$PKG_IDENTIFIER" version="$APP_VERSION" onConclusion="none">koware-component.pkg</pkg-ref>
</installer-gui-script>
EOF

info "Building product installer..."
PKG_PATH="$OUTPUT_DIR/$PKG_NAME"

productbuild \
    --distribution "$BUILD_DIR/distribution.xml" \
    --resources "$PKG_RESOURCES" \
    --package-path "$BUILD_DIR" \
    "$PKG_PATH"

info "PKG installer created: $PKG_PATH"

if [ "$COPY_TO_DESKTOP" = "true" ]; then
    DESKTOP="$HOME/Desktop"
    if [ -d "$DESKTOP" ]; then
        cp "$PKG_PATH" "$DESKTOP/"
        info "Copied PKG to Desktop: $DESKTOP/$PKG_NAME"
    fi
fi

rm -rf "$BUILD_DIR"

echo ""
info "Build complete!"
echo "  PKG: $PKG_PATH"
echo "  Size: $(du -h "$PKG_PATH" | cut -f1)"
echo ""
info "Double-click the .pkg file to install Koware."
