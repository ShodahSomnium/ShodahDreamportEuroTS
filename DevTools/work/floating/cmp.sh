#!/bin/bash
# cmp.sh <before_ground.txt> <after_ground.txt> : sums the survey's per-point numbers over the points both runs measured.
B="$1"; A="$2"
ext() { # file point -> "floatm2 ring skirtsair skirtroad clr undrawn undrawnroad dead fillm2"
  awk -v P="$2" '
    $1==P && $2=="game" {
      line=$0
      match(line,/fill floating [0-9]+ m2/); f=substr(line,RSTART+14,RLENGTH-17)
      match(line,/round it [0-9]+ m2/); r=substr(line,RSTART+9,RLENGTH-12)
      match(line,/of [0-9]+ m2; skirts/); fa=substr(line,RSTART+3,RLENGTH-13)
      match(line,/in air [0-9]+ segs/); s=substr(line,RSTART+7,RLENGTH-12)
      match(line,/road clearance [0-9]+ segs/); sr=substr(line,RSTART+15,RLENGTH-20)
      match(line,/clearance hits [0-9]+/); c=substr(line,RSTART+15,RLENGTH-15)
      match(line,/, undrawn [0-9]+,/); u=substr(line,RSTART+10,RLENGTH-11)
      match(line,/undrawn over roads [0-9]+/); ur=substr(line,RSTART+19,RLENGTH-19)
      match(line,/dead items [0-9]+/); d=substr(line,RSTART+11,RLENGTH-11)
      print f, r, s, sr, c, u, ur, d, fa; exit }' "$1"; }
pts=$(grep -o "^[a-z]*[0-9]* game" "$A" | awk '{print $1}')
tb=(0 0 0 0 0 0 0 0 0); ta=(0 0 0 0 0 0 0 0 0); n=0
for p in $pts; do
  b=$(ext "$B" $p); a=$(ext "$A" $p); [ -z "$b" ] || [ -z "$a" ] && continue
  n=$((n+1)); read -a bb <<< "$b"; read -a aa <<< "$a"
  for i in 0 1 2 3 4 5 6 7 8; do tb[$i]=$((tb[$i]+${bb[$i]:-0})); ta[$i]=$((ta[$i]+${aa[$i]:-0})); done
  [ -n "$VERBOSE" ] && echo "$p B: $b | A: $a"
done
names=("fill floating m2" "fill above all terrain round it m2" "skirt segments hanging in air" "skirt segments over a road" "clearance cells (anything in 4.75 m over a road)" "undrawn colliders" "undrawn colliders over roads (cells)" "items with colliders and nothing drawn" "fill area m2")
echo "points compared: $n"
for i in 0 1 2 3 4 5 6 7 8; do printf "  %-52s before %10d  after %10d\n" "${names[$i]}" ${tb[$i]} ${ta[$i]}; done
