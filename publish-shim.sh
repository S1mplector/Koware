#!/bin/bash
# Publish Koware CLI for the current operating system
# Usage: ./publish-shim.sh [--runtime <rid>] [--output <dir>] [--self-contained]
#
# This script publishes Koware without installing it.
# For installation, use the platform-specific scripts in Scripts/

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$SCRIPT_DIR/publish/shim"
SELF_CONTAINED="false"
RUNTIME=""

# Detect OS and architecture
detect_runtime() {
    local os arch
    
    case "$(uname -s)" in
        Linux*)  os="linux" ;;
        Darwin*) os="osx" ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) echo "Unsupported OS: $(uname -s)"; exit 1 ;;
    esac
    
    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        armv7l) arch="arm" ;;
        *) arch="x64" ;;  # Default to x64
    esac
    
    echo "${os}-${arch}"
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --runtime|-r) RUNTIME="$2"; shift 2 ;;
        --output|-o) OUTPUT_DIR="$2"; shift 2 ;;
        --self-contained|-s) SELF_CONTAINED="true"; shift ;;
        --help|-h)
            echo "Usage: $0 [options]"
            echo ""
            echo "Publish Koware CLI for the current OS (does not install)."
            echo ""
            echo "Options:"
            echo "  -r, --runtime <rid>    Runtime identifier (e.g., linux-x64, osx-arm64, win-x64)"
            echo "                         Default: auto-detected"
            echo "  -o, --output <dir>     Output directory. Default: ./publish/shim"
            echo "  -s, --self-contained   Create self-contained build (no .NET runtime needed)"
            echo "  -h, --help             Show this help"
            echo ""
            echo "Examples:"
            echo "  $0                          # Publish for current OS"
            echo "  $0 --runtime linux-arm64    # Publish for Linux ARM64"
            echo "  $0 --self-contained         # Self-contained build"
            exit 0
            ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# Auto-detect runtime if not specified
if [ -z "$RUNTIME" ]; then
    RUNTIME=$(detect_runtime)
fi

echo "╔════════════════════════════════════════╗"
echo "║       Koware Publish (Shim)            ║"
echo "╚════════════════════════════════════════╝"
echo ""
echo "Runtime:        $RUNTIME"
echo "Output:         $OUTPUT_DIR"
echo "Self-contained: $SELF_CONTAINED"
echo ""

# Clean output directory
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build publish arguments
PUBLISH_ARGS=(
    "publish" "$SCRIPT_DIR/Koware.Cli/Koware.Cli.csproj"
    "-c" "Release"
    "-r" "$RUNTIME"
    "-o" "$OUTPUT_DIR"
)

if [ "$SELF_CONTAINED" = "true" ]; then
    PUBLISH_ARGS+=("--self-contained" "true")
    PUBLISH_ARGS+=("/p:PublishSingleFile=true")
    PUBLISH_ARGS+=("/p:IncludeNativeLibrariesForSelfExtract=true")
else
    PUBLISH_ARGS+=("--self-contained" "false")
fi

echo "Building..."
dotnet "${PUBLISH_ARGS[@]}"

# Rename executable based on platform
if [[ "$RUNTIME" == win-* ]]; then
    # Windows: keep .exe extension
    if [ -f "$OUTPUT_DIR/Koware.Cli.exe" ]; then
        mv "$OUTPUT_DIR/Koware.Cli.exe" "$OUTPUT_DIR/koware.exe"
    fi
else
    # Linux/macOS: remove extension, make executable
    if [ -f "$OUTPUT_DIR/Koware.Cli" ]; then
        mv "$OUTPUT_DIR/Koware.Cli" "$OUTPUT_DIR/koware"
        chmod +x "$OUTPUT_DIR/koware"
    fi
    
    # Create wrapper script for non-self-contained builds
    if [ "$SELF_CONTAINED" = "false" ] && [ -f "$OUTPUT_DIR/Koware.Cli.dll" ]; then
        cat > "$OUTPUT_DIR/koware" << 'EOF'
#!/bin/bash
REAL_PATH="$(readlink -f "${BASH_SOURCE[0]}" 2>/dev/null || realpath "${BASH_SOURCE[0]}")"
SCRIPT_DIR="$(dirname "$REAL_PATH")"
exec dotnet "$SCRIPT_DIR/Koware.Cli.dll" "$@"
EOF
        chmod +x "$OUTPUT_DIR/koware"
    fi
fi

echo ""
echo "════════════════════════════════════════"
echo "[+] Published to: $OUTPUT_DIR"
echo ""
echo "To run:"
if [[ "$RUNTIME" == win-* ]]; then
    echo "  $OUTPUT_DIR/koware.exe --help"
else
    echo "  $OUTPUT_DIR/koware --help"
fi
echo ""
echo "To install, use the platform-specific scripts:"
echo "  Linux:  ./Scripts/publish-linux.sh"
echo "  macOS:  ./Scripts/publish-macos.sh"
echo "  Windows: .\\Scripts\\publish-installer.ps1"
