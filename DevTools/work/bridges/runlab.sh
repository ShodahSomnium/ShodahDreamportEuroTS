#!/bin/bash
# runlab.sh: bridges in the road, before/after at the Austrian motorway spot and Dover's 2026-10-05 crossing.
W="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/bridges"; R="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/renderlab/render.sh"
for which in before after; do
  RL_CENTRE="22147,61.3,15305" RL_SHOTS="a1,-37.2,2.4,-12.6,79,2;a2,-19,2.3,-8,79,2;a3,-70,22,-45,62,22" RL_PROBE="-37.2,-12.6,41.4,2.6,1" bash "$R" "$W/$which" "$W/img/${which}_at" austria
  RL_VIEWS=none RL_SHOTS="d1,54,2.5,200,180,0;d2,54,2.5,120,0,0" bash "$R" "$W/$which" "$W/img/${which}_dv" dover
done
echo LAB DONE
