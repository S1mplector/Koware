#!/bin/bash

macos_read_app_version() {
    local repo_root="$1"
    sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$repo_root/Koware.Cli/Koware.Cli.csproj"
}

macos_set_target_rids() {
    local runtime="$1"
    TARGET_RIDS=("$runtime")
    if [[ "$runtime" == "universal" ]]; then
        TARGET_RIDS=("osx-arm64" "osx-x64")
    fi
}

macos_publish_project() {
    local project="$1"
    local configuration="$2"
    local rid="$3"
    local output_dir="$4"
    local self_contained="${5:-true}"
    local executable_name="${6:-}"

    mkdir -p "$output_dir"

    local args=(
        publish "$project"
        -c "$configuration"
        -r "$rid"
        -o "$output_dir"
        --self-contained "$self_contained"
    )

    echo "dotnet ${args[*]}"
    dotnet "${args[@]}"

    if [ -n "$executable_name" ]; then
        chmod +x "$output_dir/$executable_name" 2>/dev/null || true
    fi
}

macos_publish_runtime_bundle() {
    local repo_root="$1"
    local configuration="$2"
    local rid="$3"
    local target_root="$4"
    local self_contained="${5:-true}"
    local cli_dir="$target_root/$rid"
    local reader_dir="$cli_dir/reader"

    mkdir -p "$cli_dir" "$reader_dir"

    macos_publish_project \
        "$repo_root/Koware.Cli/Koware.Cli.csproj" \
        "$configuration" \
        "$rid" \
        "$cli_dir" \
        "$self_contained" \
        "Koware.Cli"

    macos_publish_project \
        "$repo_root/Koware.Reader/Koware.Reader.csproj" \
        "$configuration" \
        "$rid" \
        "$reader_dir" \
        "$self_contained" \
        "Koware.Reader"
}

macos_write_runtime_launcher() {
    local output_path="$1"
    local app_root="$2"
    local root_mode="${3:-absolute}"

    if [ "$root_mode" = "relative_to_script" ]; then
        cat > "$output_path" <<EOF
#!/bin/bash
set -euo pipefail

SCRIPT_DIR="\$(cd "\$(dirname "\${BASH_SOURCE[0]}")" && pwd)"
APP_ROOT="\$(cd "\$SCRIPT_DIR/$app_root" && pwd)"
ARCH="\$(uname -m)"

case "\$ARCH" in
    arm64|aarch64) PRIMARY_RID="osx-arm64"; FALLBACK_RID="osx-x64" ;;
    x86_64) PRIMARY_RID="osx-x64"; FALLBACK_RID="osx-arm64" ;;
    *) PRIMARY_RID=""; FALLBACK_RID="" ;;
esac

for rid in "\$PRIMARY_RID" "\$FALLBACK_RID"; do
    if [ -n "\$rid" ] && [ -x "\$APP_ROOT/\$rid/Koware.Cli" ]; then
        exec "\$APP_ROOT/\$rid/Koware.Cli" "\$@"
    fi
done

echo "No compatible Koware runtime bundle found in \$APP_ROOT." >&2
exit 1
EOF
    else
        cat > "$output_path" <<EOF
#!/bin/bash
set -euo pipefail

APP_ROOT="$app_root"
ARCH="\$(uname -m)"

case "\$ARCH" in
    arm64|aarch64) PRIMARY_RID="osx-arm64"; FALLBACK_RID="osx-x64" ;;
    x86_64) PRIMARY_RID="osx-x64"; FALLBACK_RID="osx-arm64" ;;
    *) PRIMARY_RID=""; FALLBACK_RID="" ;;
esac

for rid in "\$PRIMARY_RID" "\$FALLBACK_RID"; do
    if [ -n "\$rid" ] && [ -x "\$APP_ROOT/\$rid/Koware.Cli" ]; then
        exec "\$APP_ROOT/\$rid/Koware.Cli" "\$@"
    fi
done

echo "No compatible Koware runtime bundle found in \$APP_ROOT." >&2
exit 1
EOF
    fi

    chmod +x "$output_path"
}

macos_create_udzo_dmg() {
    local volume_name="$1"
    local source_dir="$2"
    local dmg_path="$3"

    [ -f "$dmg_path" ] && rm -f "$dmg_path"

    hdiutil create \
        -volname "$volume_name" \
        -srcfolder "$source_dir" \
        -ov \
        -format UDZO \
        "$dmg_path"
}
