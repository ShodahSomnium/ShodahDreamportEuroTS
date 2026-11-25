#!/bin/bash
# Rebuilds the render lab's Unity project (DevTools/renderlab/proj) from scratch: a copy of the user's project's
# Assets, Packages and ProjectSettings, with the world code and the scene replaced by the untouched snapshots
# (DevTools/orig), then one Unity batchmode import (-quit) to build its Library (about 5-10 minutes).
# Only ever opens the COPY. Takes the lab lock, so no render runs meanwhile.
#
#   bash rebuild_proj.sh
set -u

main() {
    local P="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim"
    local T="$P/DevTools"
    local L="$T/renderlab"
    local PROJ="$L/proj"
    local UNITY="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Unity.exe"

    until mkdir "$L/LOCK" 2>/dev/null; do
        echo "rebuild_proj.sh: waiting for the lab lock ($(cat "$L/LOCK/owner" 2>/dev/null))"; sleep 30
    done
    echo "rebuild_proj.sh pid $$ since $(date '+%H:%M:%S')" > "$L/LOCK/owner"
    trap 'rm -rf "'"$L"'/LOCK"' EXIT

    rm -rf "$PROJ"
    mkdir -p "$PROJ" "$L/logs"
    cp -r "$P/Packages" "$P/ProjectSettings" "$P/Assets" "$PROJ/" || { echo "copy failed"; return 3; }
    rm -rf "$PROJ/Assets/#User/SomniumEuroTruck"
    cp -r "$T/orig/code_2026-10-04" "$PROJ/Assets/#User/SomniumEuroTruck"
    cp "$T/orig/scene_2026-10-04.unity" "$PROJ/Assets/Euro Truck Scene.unity"

    "$UNITY" -batchmode -projectPath "$PROJ" -quit -logFile "$L/logs/import.log"
    local code=$?
    echo "rebuild_proj.sh: import exit $code (log $L/logs/import.log)"
    grep "error CS" "$L/logs/import.log" | sort -u | head -20
    return $code
}

main "$@"
exit $?
