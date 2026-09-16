#!/bin/zsh
set -euo pipefail

project_dir=${0:A:h:h}
tool_dir="$project_dir/../.toolchain/tools"
dotnet="$project_dir/../.toolchain/dotnet/dotnet"
game_assembly="${VALHEIM_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Valheim}/valheim.app/Contents/Resources/Data/Managed/assembly_valheim.dll"
reference_dir="$project_dir/.buildrefs"
reference="$reference_dir/assembly_valheim.dll"

if [[ ! -f "$game_assembly" ]]; then
  echo "Valheim assembly not found: $game_assembly" >&2
  exit 1
fi

if [[ -f "$reference" && "$reference" -nt "$game_assembly" ]]; then
  exit 0
fi

mkdir -p "$tool_dir" "$reference_dir"
if [[ ! -x "$tool_dir/assembly-publicizer" ]]; then
  "$dotnet" tool install BepInEx.AssemblyPublicizer.Cli --tool-path "$tool_dir"
fi

export DOTNET_ROOT="$project_dir/../.toolchain/dotnet"
export DOTNET_ROLL_FORWARD=Major
"$tool_dir/assembly-publicizer" "$game_assembly" --output "$reference_dir" --strip --overwrite

