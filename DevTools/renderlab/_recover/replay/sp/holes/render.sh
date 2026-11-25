#!/bin/bash
# Render lab (hole-survey copy, N/holes/render.sh, 2026-09-25): identical to N/renderlab/render.sh plus the sites
# "doverh" and "prague" (the hole survey of RenderLabRunner.SurveyFor; RL_SHOTS / RL_PROBE work on any site).
# Render lab: renders the Euro Truck Simulator world with the world's real code, shaders and render settings.
#
#   bash render.sh <code folder> <output prefix> [sites]
#
#   <code folder>    a copy of Assets/#User/SomniumEuroTruck (with its .meta files), e.g. N/orig/code or the live
#                    "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck" (only read)
#   <output prefix>  e.g. N/renderlab/after  ->  after_a_yard.png, after_b_road.png, ... (+ a .txt per view with the
#                    camera, fog, sun, ambient, sky, climate and post-volume values it was rendered with, and
#                    <prefix>_<site>_unity.log / <prefix>_<site>_runlog.txt)
#   [sites]          optional, default "dover szczecin"
#   views            dover: a_spawn a_yard a_trees b_road c_sky d_tree      szczecin: e_yard e_trees e_tree
#   environment      RL_VIEWS=a_yard,b_road  only these views
#                    RL_FOV=60               vertical field of view in degrees (a desktop Somnium camera is ~60)
#                    RL_NOPOST=1             also <view>_nopost.png: the camera's post-processing off (the lighting and
#                                            the sky before the tone curve, grading and bloom)
#                    RL_TIMEOUT=1500         seconds the in-editor run may take per site
#
# What it does: copies the code folder over the lab project's Assets/#User/SomniumEuroTruck (N/renderlab/proj, a
# throwaway copy of the user's project; the user's project is never touched), then runs Unity 6000.3.5f2 in batchmode on
# the GPU (D3D11) once per site: RenderLabEditor.Go opens "Assets/Euro Truck Scene.unity", keeps only the map viewer,
# the scene's sun and its Global Volume, loads the map round the site's centre with the world's own classes and renders
# each view through URP (the project's pipeline asset, post-processing on, the EtsPostFx volume, fog, EtsSky) to PNG.
# A lock (N/renderlab/LOCK) makes a second caller wait until the first is done. Everything is inside main(), so the
# whole script is read before it runs (editing it while a run is going cannot break that run).
set -u

main() {
    local N="C:/Users/derii/AppData/Local/Temp/claude/D--Dreamport-Euro-Truck-Simulator-Euro-Truck-Sim/b6bed9e4-1d99-4fd7-84f3-35278f538c47/scratchpad"
    local L="$N/renderlab"
    local UNITY="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Unity.exe"
    local PROJ="$L/proj"

    if [ $# -lt 2 ]; then
        echo "usage: bash render.sh <code folder> <output prefix> [sites]"; return 2
    fi

    local CODE="${1%/}" OUT="$2" SITES="${3:-dover szczecin}"

    if [ ! -f "$CODE/SomniumEuroTruck.asmdef" ]; then
        echo "render.sh: $CODE is not a SomniumEuroTruck code folder (no SomniumEuroTruck.asmdef)"; return 2
    fi

    case "$(cd "$CODE" 2>/dev/null && pwd -W 2>/dev/null)" in
        *renderlab/proj/*) echo "render.sh: the code folder must not be inside the lab project"; return 2 ;;
    esac

    mkdir -p "$(dirname "$OUT")"

    # ---- lock: one lab run at a time (a lock older than 3 hours is taken as left behind by a killed run)
    local waited=0
    until mkdir "$L/LOCK" 2>/dev/null; do
        if [ -n "$(find "$L/LOCK" -maxdepth 0 -mmin +180 2>/dev/null)" ]; then
            echo "render.sh: removing a stale lock (older than 3 h)"; rm -rf "$L/LOCK"; continue
        fi
        if [ $waited -eq 0 ]; then echo "render.sh: another lab run holds $L/LOCK ($(cat "$L/LOCK/owner" 2>/dev/null)); waiting..."; fi
        waited=$((waited + 1)); sleep 15
    done
    echo "pid $$ out $OUT since $(date '+%H:%M:%S')" > "$L/LOCK/owner"
    trap 'rm -rf "'"$L"'/LOCK"' EXIT

    # ---- the code under test, and the lab's own scripts
    rm -rf "$PROJ/Assets/#User/SomniumEuroTruck"
    cp -r "$CODE" "$PROJ/Assets/#User/SomniumEuroTruck" || { echo "render.sh: copying the code failed"; return 3; }
    mkdir -p "$PROJ/Assets/RenderLab/Editor"
    cp "$L/labsrc/RenderLabRunner.cs" "$PROJ/Assets/RenderLab/RenderLabRunner.cs"
    cp "$L/labsrc/Editor/RenderLabEditor.cs" "$PROJ/Assets/RenderLab/Editor/RenderLabEditor.cs"
    # The lab's scene is the untouched snapshot (N/orig/scene.unity): nobody edits the real one, and it must not drift here.
    cmp -s "$N/orig/scene.unity" "$PROJ/Assets/Euro Truck Scene.unity" || cp "$N/orig/scene.unity" "$PROJ/Assets/Euro Truck Scene.unity"
    # A lock file left by a killed editor would stop the next one opening the project (we hold the lab lock, so none runs).
    rm -f "$PROJ/Temp/UnityLockfile"

    local status=0 SITE LOG start code v views
    for SITE in $SITES; do
        LOG="${OUT}_${SITE}_unity.log"
        case "$SITE" in
            dover) views="a_spawn a_yard a_trees b_road c_sky d_tree" ;;
            szczecin) views="e_yard e_trees e_tree" ;;
            doverh|prague) views="" ;;  # hole survey (N/holes): images named <prefix>_<point>_<cab|up>_y<yaw>.png
            *) echo "render.sh: unknown site $SITE"; status=2; continue ;;
        esac
        # No stale image from an earlier run with the same prefix may pass for this one.
        for v in $views; do
            if [ -z "${RL_VIEWS:-}" ] || [[ ",${RL_VIEWS}," == *",$v,"* ]]; then
                rm -f "${OUT}_${v}.png" "${OUT}_${v}.txt" "${OUT}_${v}_nopost.png"
            fi
        done
        rm -f "$LOG" "${OUT}_${SITE}_runlog.txt"

        echo "render.sh: site $SITE -> ${OUT}_*.png (log $LOG)"
        start=$(date +%s)
        RL_SITE="$SITE" RL_OUT="$OUT" RL_VIEWS="${RL_VIEWS:-}" RL_FOV="${RL_FOV:-60}" RL_TIMEOUT="${RL_TIMEOUT:-1500}" RL_NOPOST="${RL_NOPOST:-0}" \
            "$UNITY" -batchmode -projectPath "$PROJ" -executeMethod RenderLabEditor.Go -logFile "$LOG" &
        local pid=$! winpid
        winpid=$(cat "/proc/$pid/winpid" 2>/dev/null)
        # Watchdog: the in-editor run gives up by itself after RL_TIMEOUT + 300 s; this catches a hang before that
        # (an import or compile that never ends).
        local limit=$(( ${RL_TIMEOUT:-1500} + 900 ))
        while kill -0 $pid 2>/dev/null; do
            if [ $(( $(date +%s) - start )) -gt $limit ]; then
                echo "render.sh: Unity still running after $limit s - killing it"
                [ -n "$winpid" ] && taskkill //F //PID "$winpid" >/dev/null 2>&1
                kill $pid 2>/dev/null
                break
            fi
            sleep 5
        done
        wait $pid
        code=$?
        echo "render.sh: site $SITE Unity exit $code after $(( $(date +%s) - start )) s"
        if grep -q "error CS" "$LOG" 2>/dev/null; then
            echo "render.sh: COMPILE ERRORS in the code under test:"; grep "error CS" "$LOG" | sort -u | head -20; status=4
        fi
        grep "\[RenderLab\] \(wrote\|NOT settled\|TIMEOUT\|view .* failed\|setup failed\)" "$LOG" 2>/dev/null | sed 's/^.*\[RenderLab\]/  /' | sort -u
        [ $code -ne 0 ] && [ $status -eq 0 ] && status=$code
    done

    echo "render.sh: images:"
    ls -1 "${OUT}"_*.png 2>/dev/null || echo "  none"
    return $status
}

main "$@"
exit $?
