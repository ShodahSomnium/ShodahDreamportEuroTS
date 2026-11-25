#!/bin/bash
# runlab.sh <before|after> <site> : renders the fence shots and writes the fence scan for one code copy at one site
W="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/fences"
which=$1; site=$2
case $site in dover) S=$(cat "$W/shots_dover.txt");; szczecin) S=$(cat "$W/shots_szczecin.txt");; prague) S=$(cat "$W/shots_prague.txt");; esac
export RL_SHOTS="$S" FENCE_SCAN_OUT="$W/img/${which}_${site}_fencescan.txt" FENCE_SCAN_R=400
bash "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/renderlab/render.sh" "$W/lab/$which" "$W/img/${which}_${site}" "$site"
echo "exit $?"
