#!/bin/bash
cd "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim"
CODE="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck"
L="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/daynight/lab"
RL_TRAFFIC="mw_oncoming,-933,34,-302.3,274,8;mw_away,-933,34,-302.3,70,6" bash DevTools/renderlab/render.sh "$CODE" DevTools/work/traffic/img/t2 dover > "$L/t2.out" 2>&1; echo "t2 exit $?"
RL_DAYNIGHT="noon:780:0;night:1380:0;nightrain:1380:1:4;rain:780:1:4" RL_VIEWS=b_road bash DevTools/renderlab/render.sh "$CODE" "$L/r2" dover > "$L/r2.out" 2>&1; echo "r2 exit $?"
RL_LAMPS=1 RL_TIME=1380 bash DevTools/renderlab/render.sh "$CODE" "$L/r3" dover > "$L/r3.out" 2>&1; echo "r3 exit $?"
