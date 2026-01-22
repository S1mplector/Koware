#!/bin/bash
# bump-version.sh - Bump Koware version across all projects
# Usage: ./Scripts/bump-version.sh [version]
# If no version provided, prompts for input

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# All csproj files that contain version
CSPROJ_FILES=(
    "Koware.Application/Koware.Application.csproj"
    "Koware.Browser/Koware.Browser.csproj"
    "Koware.Cli/Koware.Cli.csproj"
    "Koware.Domain/Koware.Domain.csproj"
    "Koware.Infrastructure/Koware.Infrastructure.csproj"
    "Koware.Installer.Win/Koware.Installer.Win.csproj"
    "Koware.Player/Koware.Player.csproj"
    "Koware.Player.Win/Koware.Player.Win.csproj"
    "Koware.Reader/Koware.Reader.csproj"
    "Koware.Reader.Win/Koware.Reader.Win.csproj"
    "Koware.Tests/Koware.Tests.csproj"
    "Koware.Tutorial/Koware.Tutorial.csproj"
)

# Get current version from main CLI project
get_current_version() {
    grep -oP '(?<=<Version>)[^<]+' "$ROOT_DIR/Koware.Cli/Koware.Cli.csproj" 2>/dev/null || \
    grep -o '<Version>[^<]*</Version>' "$ROOT_DIR/Koware.Cli/Koware.Cli.csproj" | sed 's/<[^>]*>//g'
}

# Validate version format (x.x.x or vx.x.x)
validate_version() {
    local version="$1"
    # Remove leading 'v' if present
    version="${version#v}"
    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        return 1
    fi
    echo "$version"
}

# Update version in a single file
update_file() {
    local file="$1"
    local new_version="$2"
    local full_path="$ROOT_DIR/$file"
    
    if [[ ! -f "$full_path" ]]; then
        echo -e "  ${YELLOW}⚠ Skipped${NC} (not found): $file"
        return
    fi
    
    # Use sed to replace version (compatible with both macOS and Linux)
    if [[ "$OSTYPE" == "darwin"* ]]; then
        # macOS
        sed -i '' "s|<Version>[^<]*</Version>|<Version>$new_version</Version>|g" "$full_path"
    else
        # Linux
        sed -i "s|<Version>[^<]*</Version>|<Version>$new_version</Version>|g" "$full_path"
    fi
    
    echo -e "  ${GREEN}✓${NC} Updated: $file"
}

# Main
echo -e "${CYAN}╔══════════════════════════════════════╗${NC}"
echo -e "${CYAN}║     Koware Version Bump Utility      ║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════╝${NC}"
echo

CURRENT_VERSION=$(get_current_version)
echo -e "Current version: ${YELLOW}$CURRENT_VERSION${NC}"
echo

# Get new version from argument or prompt
if [[ -n "$1" ]]; then
    NEW_VERSION="$1"
else
    read -p "Enter new version (vx.x.x or x.x.x): " NEW_VERSION
fi

# Validate and normalize version
NORMALIZED_VERSION=$(validate_version "$NEW_VERSION")
if [[ -z "$NORMALIZED_VERSION" ]]; then
    echo -e "${RED}✗ Invalid version format.${NC} Expected: vx.x.x or x.x.x (e.g., v1.2.3 or 1.2.3)"
    exit 1
fi

echo -e "New version: ${GREEN}$NORMALIZED_VERSION${NC}"
echo

# Confirm
read -p "Proceed with version bump? [y/N] " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo -e "${YELLOW}Cancelled.${NC}"
    exit 0
fi

echo
echo -e "${CYAN}Updating project files...${NC}"

# Update all csproj files
UPDATED=0
for file in "${CSPROJ_FILES[@]}"; do
    update_file "$file" "$NORMALIZED_VERSION"
    ((UPDATED++))
done

echo
echo -e "${GREEN}✓ Version bumped to $NORMALIZED_VERSION in $UPDATED files.${NC}"
echo
echo -e "Next steps:"
echo -e "  1. Review changes: ${CYAN}git diff${NC}"
echo -e "  2. Commit: ${CYAN}git commit -am \"Bump version to $NORMALIZED_VERSION\"${NC}"
echo -e "  3. Tag: ${CYAN}git tag v$NORMALIZED_VERSION${NC}"
