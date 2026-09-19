#!/usr/bin/env bash
# Builds AudioWatchdog.dll, which clears LCPDFR's stuck audio busy flag.
# See TODO.md item 2. Needs mono-devel for mcs; LCPDFR must already be installed,
# since the reference assemblies ship inside it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Where GTA IV is installed. Pass it as the first argument, or set GAME_ROOT. The default
# walks Steam's own library list, so no drive layout is written down here.
find_game_root() {
    local vdf root path
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$HOME/.steam/debian-installation/steamapps/libraryfolders.vdf" \
               "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [ -f "$vdf" ] || continue
        while IFS= read -r path; do
            for root in "$path/steamapps/common/Grand Theft Auto IV - Original Edition/GTAIV" \
                        "$path/steamapps/common/Grand Theft Auto IV/GTAIV"; do
                [ -d "$root" ] && { echo "$root"; return 0; }
            done
        done < <(sed -n 's/.*"path"[[:space:]]*"\(.*\)".*/\1/p' "$vdf")
    done
    return 1
}

GAME_ROOT="${1:-${GAME_ROOT:-$(find_game_root || true)}}"
[ -n "$GAME_ROOT" ] || { echo "error: GTA IV not found; pass the GTAIV folder as the first argument" >&2; exit 1; }
REFS="$GAME_ROOT/LCPDFR/API Example/References"

for dll in "LCPD First Response.dll" "SlimDX.dll" "ScriptHookDotNet.dll"; do
    if [[ ! -f "$REFS/$dll" ]]; then
        echo "Reference assembly not found: $REFS/$dll" >&2
        exit 1
    fi
done

mcs -target:library -platform:x86 -sdk:4.5 \
    -out:"$HERE/AudioWatchdog.dll" \
    -r:"$REFS/LCPD First Response.dll" -r:"$REFS/SlimDX.dll" -r:"$REFS/ScriptHookDotNet.dll" \
    "$HERE/AudioWatchdog.cs"

echo "Built: $HERE/AudioWatchdog.dll"
echo "Install with: cp '$HERE/AudioWatchdog.dll' '$GAME_ROOT/LCPDFR/plugins/'"
