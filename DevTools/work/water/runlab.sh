#!/bin/bash
# runlab.sh <before|after> <site> [prefix]: renders the water shots for one code copy at one site
W="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/water"
which=$1; site=$2; pre=${3:-$which}
case $site in
 prague) S="p_river1,1560,-22,0,90,8;p_river2,1600,-24,-300,10,6;p_river3,-60,-24,-1150,200,8;p_top,1630,60,0,0,60";;
 dover) S="d_sea1,-174,6,-120,180,6;d_sea2,500,25,-100,200,15;d_seatop,0,80,-300,180,35";;
esac
RL_SHOTS="${RL_SHOTS_OVERRIDE:-$S}" bash "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/renderlab/render.sh" "$W/$which/SomniumEuroTruck" "$W/img/${pre}_${site}" "$site"
echo "exit $?"
