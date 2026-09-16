#!/bin/zsh
set -euo pipefail

project_dir=${0:A:h:h}
sdk="$project_dir/../.toolchain/dotnet/dotnet"
output_dir="$project_dir/dist"
staging_dir="$output_dir/staging"
version=$(/usr/bin/sed -n 's/.*"version_number": "\([^"]*\)".*/\1/p' "$project_dir/manifest.json")
package_name="maizz-UnifiedTargetPortal-$version.zip"
game_bepinex="$HOME/Library/Application Support/Steam/steamapps/common/Valheim/BepInEx/core"
bepinex_dir="${BEPINEX_DIR:-$game_bepinex}"

if [[ ! -f "$bepinex_dir/BepInEx.dll" ]]; then
  profile_root="$HOME/Library/Application Support/com.r2modmac/profiles"
  profile_bepinex=$(/usr/bin/find "$profile_root" -path '*/BepInEx/core/BepInEx.dll' -print -quit 2>/dev/null || true)
  if [[ -n "$profile_bepinex" ]]; then
    bepinex_dir="${profile_bepinex:h}"
  fi
fi
if [[ ! -f "$bepinex_dir/BepInEx.dll" ]]; then
  echo "BepInEx build reference not found. Apply a BepInEx profile once, or set BEPINEX_DIR." >&2
  exit 1
fi

"$project_dir/scripts/prepare-publicized-reference.sh"
"$sdk" build "$project_dir/UnifiedTargetPortal.csproj" --configuration Release -p:BepInExDir="$bepinex_dir"

if [[ -e "$output_dir/$package_name" && "${1:-}" != "--force" ]]; then
  echo "Refusing to overwrite existing $package_name. Bump manifest.json, or rerun with --force." >&2
  exit 1
fi

rm -rf "$staging_dir"
mkdir -p "$staging_dir/plugins/UnifiedTargetPortal"
cp "$project_dir/bin/Release/net472/UnifiedTargetPortal.dll" "$staging_dir/plugins/UnifiedTargetPortal/"
cp "$project_dir/manifest.json" "$project_dir/README.md" "$project_dir/CHANGELOG.md" "$project_dir/LICENSE" "$project_dir/icon.png" "$staging_dir/"
rm -f "$output_dir/$package_name"
(cd "$staging_dir" && /usr/bin/zip -qr "$output_dir/$package_name" .)
rm -rf "$staging_dir"
echo "Created $output_dir/$package_name"
