#!/bin/bash
cd "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim"
CODE="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck"
L="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/daynight/lab"
RL_LAMPS=1 RL_TIME=1380 bash DevTools/renderlab/render.sh "$CODE" "$L/r4" dover > "$L/r4.out" 2>&1; echo "r4 exit $?"
RL_DAYNIGHT="night:1380:0;dusk:1290:0;rain:780:1:4;noon:780:0" RL_VIEWS=b_road,a_yard bash DevTools/renderlab/render.sh "$CODE" "$L/r5" dover > "$L/r5.out" 2>&1; echo "r5 exit $?"
