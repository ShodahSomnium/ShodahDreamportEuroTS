#!/bin/bash
cd "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim"
CODE="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck"
L="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/daynight/lab"
RL_TRAFFIC="mw_oncoming,-933,34,-302.3,274,8;mw_away,-933,34,-302.3,70,6" bash DevTools/renderlab/render.sh "$CODE" "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/traffic/img/t2" dover > "$L/t2.out" 2>&1; echo "t2 exit $?"
