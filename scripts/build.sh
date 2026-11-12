#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
unity_path="$HOME/.local/share/Steam/steamapps/common/ULTRAKILL"
openremix_path="$script_dir/../../openremix"
deploy=false
plugin_only=false

usage() {
    cat <<EOF
Usage: $(basename -- "$0") [--unity-path PATH] [--openremix-path PATH] [--deploy [--plugin-only]]

Copy game references and build the Unity RTX Remix plugin in Release mode.

  --unity-path PATH      Unity game directory (default: $unity_path)
  --openremix-path PATH  OpenRemix checkout (default: ../openremix beside UnityRTX)
  --deploy              Deploy the plugin and existing OpenRemix Windows binaries
  --plugin-only         With --deploy, update only the plugin
  -h, --help            Show this help

Build OpenRemix first with scripts/linux/build-windows-remix.py in its checkout.
Changed destination files are backed up under BepInEx/UnityRTX-backups/.
EOF
}

fail() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

while (($#)); do
    case "$1" in
        --unity-path)
            [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || fail "--unity-path requires a directory."
            unity_path="$2"
            shift 2
            ;;
        --deploy)
            deploy=true
            shift
            ;;
        --openremix-path)
            [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || fail "--openremix-path requires a directory."
            openremix_path="$2"
            shift 2
            ;;
        --plugin-only)
            plugin_only=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "Unknown argument: $1. Use --help for usage."
            ;;
    esac
done

[[ "$plugin_only" == false || "$deploy" == true ]] || fail "--plugin-only requires --deploy."

command -v dotnet >/dev/null 2>&1 || fail "Install the .NET SDK first (on Arch: sudo pacman -Syu --needed dotnet-sdk netstandard-targeting-pack)."
[[ -n "$(dotnet --list-sdks)" ]] || fail "No .NET SDK found. Install dotnet-sdk; the runtime alone cannot build the plugin."
[[ -d "$unity_path" ]] || fail "Unity game directory not found: $unity_path. Set --unity-path PATH."
unity_path="$(cd -- "$unity_path" && pwd)"

runtime_sources=()
runtime_targets=()
if [[ "$deploy" == true && "$plugin_only" == false ]]; then
    [[ -d "$openremix_path" ]] || fail "OpenRemix checkout not found: $openremix_path. Set --openremix-path PATH, or use --deploy --plugin-only."
    openremix_path="$(cd -- "$openremix_path" && pwd)"
    runtime_sources+=("$openremix_path/_work/build/windows-cross/src/native/remix.dll")
    runtime_targets+=("d3d9.dll")
    for name in SDL3.dll libwinpthread-1.dll libgcc_s_seh-1.dll libstdc++-6.dll; do
        runtime_sources+=("$openremix_path/_work/deps/windows/prefix/bin/$name")
        runtime_targets+=("$name")
    done
    missing=false
    for source in "${runtime_sources[@]}"; do
        if [[ ! -f "$source" ]]; then
            printf 'Missing OpenRemix binary: %s\n' "$source" >&2
            missing=true
        fi
    done
    [[ "$missing" == false ]] || fail "Build OpenRemix with scripts/linux/build-windows-remix.py first. No game files were deployed."
    printf 'Using OpenRemix Windows build: %s\n' "$openremix_path"
fi

managed_dir=""
for candidate in "$unity_path"/*_Data/Managed; do
    [[ -d "$candidate" ]] || continue
    [[ -z "$managed_dir" ]] || fail "Multiple *_Data/Managed directories found in $unity_path. Use a game directory containing only one."
    managed_dir="$candidate"
done
[[ -n "$managed_dir" ]] || fail "No *_Data/Managed directory found in $unity_path. A Unity Mono game is required."

bepinex_core="$unity_path/BepInEx/core"
[[ -d "$bepinex_core" ]] || fail "BepInEx/core not found in $unity_path. Install BepInEx 5.x in the game first."

# Keep these in sync with the references in UltrakillRemix.csproj.
references=(
    "$bepinex_core/BepInEx.dll"
    "$bepinex_core/0Harmony.dll"
    "$managed_dir/UnityEngine.dll"
    "$managed_dir/UnityEngine.CoreModule.dll"
    "$managed_dir/UnityEngine.InputLegacyModule.dll"
    "$managed_dir/UnityEngine.UIModule.dll"
    "$managed_dir/UnityEngine.UI.dll"
)

# Validate everything before replacing any existing development references.
missing=false
for reference in "${references[@]}"; do
    if [[ ! -f "$reference" ]]; then
        printf 'Missing reference: %s\n' "$reference" >&2
        missing=true
    fi
done
[[ "$missing" == false ]] || fail "Required game assemblies are missing. Check the Unity game and BepInEx installation."

printf 'Using Unity game: %s\n' "$unity_path"
mkdir -p -- "$script_dir/lib"
cp -- "${references[@]}" "$script_dir/lib/"

git_hash="$(git -C "$script_dir" rev-parse --short HEAD 2>/dev/null || printf 'unknown')"
cat > "$script_dir/BuildInfo.cs" <<EOF
// Auto-generated file - do not edit manually
namespace UnityRemix
{
    public static class BuildInfo
    {
        public const string GitHash = "$git_hash";
    }
}
EOF

# Resolve project files relative to this script, regardless of the caller's cwd.
cd -- "$script_dir"
dotnet build UltrakillRemix.csproj --configuration Release

dll_source="$script_dir/bin/Release/netstandard2.1/UnityRemix.dll"
printf '\nPlugin built at: %s\n' "$dll_source"

if [[ "$deploy" == true ]]; then
    sources=("$dll_source" "${runtime_sources[@]}")
    targets=("BepInEx/plugins/UnityRemix.dll" "${runtime_targets[@]}")
    staging_dir="$(mktemp -d "$unity_path/.unityrtx-deploy.XXXXXX")"
    trap 'rm -rf -- "$staging_dir"' EXIT
    backup_dir=""
    changed_targets=()

    # Stage the complete update and preserve existing files before replacing any.
    for i in "${!sources[@]}"; do
        relative="${targets[$i]}"
        destination="$unity_path/$relative"
        if cmp -s -- "${sources[$i]}" "$destination"; then
            printf 'Already up to date: %s\n' "$destination"
            continue
        fi
        [[ ! -d "$destination" ]] || fail "Deployment destination is a directory: $destination"
        mkdir -p -- "$(dirname -- "$staging_dir/$relative")" "$(dirname -- "$destination")"
        cp -p -- "${sources[$i]}" "$staging_dir/$relative"
        if [[ -e "$destination" ]]; then
            if [[ -z "$backup_dir" ]]; then
                mkdir -p -- "$unity_path/BepInEx/UnityRTX-backups"
                backup_dir="$(mktemp -d "$unity_path/BepInEx/UnityRTX-backups/$(date -u +%Y%m%d-%H%M%S).XXXXXX")"
                printf 'Previous files backed up to: %s\n' "$backup_dir"
            fi
            mkdir -p -- "$(dirname -- "$backup_dir/$relative")"
            cp -p -- "$destination" "$backup_dir/$relative"
        fi
        changed_targets+=("$relative")
    done

    # Rename staged DLLs instead of truncating files that a running game may use.
    for relative in "${changed_targets[@]}"; do
        mv -fT -- "$staging_dir/$relative" "$unity_path/$relative"
        printf 'Deployed: %s\n' "$unity_path/$relative"
    done
    if ((${#changed_targets[@]})); then
        printf 'Restart the game to load the deployed binaries.\n'
    fi
fi
