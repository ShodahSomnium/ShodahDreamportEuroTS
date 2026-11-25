#!/bin/bash
# Server-API guard. The Somnium upload server compiles worlds against an OLDER Fusion than the local one (no
# NetworkRunner.GetMasterClient, 2026-09-23). known_good.txt = every Fusion/SomniumSpace type and member the world
# used in the build that passed the server (2026-09-25). Anything new that this prints must be removed or proven.
# Usage: check.sh <SomniumEuroTruck.dll>
D="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/fusionapi"
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"
DLL="${1:?usage: check.sh <dll>}"
"$U/MonoBleedingEdge/bin/mono.exe" "$D/RefDump.exe" "$DLL" | tr -d '\r' | LC_ALL=C sort -u > "$D/current.txt" || exit 2
LC_ALL=C sort -u "$D/known_good.txt" > "$D/known_good.sorted"
NEW=$(LC_ALL=C comm -13 "$D/known_good.sorted" "$D/current.txt")
if [ -n "$NEW" ]; then echo "UNPROVEN server API (not in any build that passed the server compile):"; echo "$NEW"; exit 1; fi
echo "Server API check: all $(wc -l < "$D/current.txt") Fusion/Somnium members are proven."
