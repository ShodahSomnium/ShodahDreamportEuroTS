#!/bin/bash
# sign texts lab: after + before at Dover and Prague (RL_SHOTS poses from DevTools/work/signs/shots.exe)
R="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/renderlab/render.sh"
LIVE="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck"
BEFORE="C:/Users/derii/AppData/Local/Temp/claude/D--Dreamport-Euro-Truck-Simulator-Euro-Truck-Sim/b6bed9e4-1d99-4fd7-84f3-35278f538c47/scratchpad/signs_before/SomniumEuroTruck"
O="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/signs/lab"
DOVER="hq98,41.09,1.61,-83.63,75.0,-2.5;hq142,82.46,1.61,126.74,165.0,-2.5;tall648,624.51,15.18,-44.02,67.6,-1.6;big922,-839.03,28.46,-312.17,-90.5,-1.2;gantry1149,-1061.41,34.98,-331.31,-96.0,-0.9;calais,3145.89,-10.96,-2096.14,-67.0,-3.4;calais2,3277.13,-8.64,-2148.50,-93.0,-2.5;calaisgantry,3481.40,2.03,-2198.98,-84.2,-1.0"
PRAGUE="phw79,56.05,1.23,37.76,90.0,-2.5;pstr119,119.64,1.24,-10.86,0.1,-2.1;pcity283,272.13,1.23,-94.96,-136.0,-2.9;psr302,302.91,1.23,-79.34,-56.0,-2.9;pdist601,-358.30,1.31,468.62,1.7,-2.5;pdist701,454.19,2.48,515.85,47.6,-2.5;pgas967,599.41,3.25,749.49,30.0,-4.3"
RL_VIEWS=none RL_SHOTS="$DOVER" bash "$R" "$LIVE" "$O/after" dover
RL_VIEWS=none RL_SHOTS="$PRAGUE" bash "$R" "$LIVE" "$O/after" prague
RL_VIEWS=none RL_SHOTS="$DOVER" bash "$R" "$BEFORE" "$O/before" dover
RL_VIEWS=none RL_SHOTS="$PRAGUE" bash "$R" "$BEFORE" "$O/before" prague
echo ALLDONE
