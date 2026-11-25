#!/bin/bash
# Compiles the world exactly as Unity's player build does (Unity's own rsp for SomniumEuroTruck, from
# Library/Bee/artifacts/1900b0aP.dag, same defines, references, analyzers, langversion 9), with the CURRENT list of
# .cs files in the world folder (Editor folders excluded). Usage: build.sh <outdir>. Prints errors; exit 1 on errors.
P="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim"
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"
OUT="${1:?usage: build.sh <outdir>}"; mkdir -p "$OUT"
SRC="$P/Library/Bee/artifacts/1900b0aP.dag/SomniumEuroTruck.rsp"
grep -v -e '\.cs"$' -e '^-out:' -e '^-refout:' "$SRC" > "$OUT/world.rsp"
# Fusion 2.1.3 (2026-10-21): this player rsp may predate the migration; its 2.0 defines become the 2.1 set Unity now uses.
sed -i -e '/^-define:FUSION_2_0$/d' -e '/^-define:FUSION_2_0_12$/d' -e '/^-define:FUSION_2_1/d' -e '/^-define:PHOTON_REALTIME_5_OR_NEWER/d' "$OUT/world.rsp"
printf -- '-define:FUSION_2_1\n-define:FUSION_2_1_3\n-define:FUSION_2_1_OR_NEWER\n-define:PHOTON_REALTIME_5_OR_NEWER\n' >> "$OUT/world.rsp"
# ...and its Photon 2.0 client and the two project assemblies built against Fusion 2.0: the editor's fresh 2.1.3 builds instead.
sed -i -e 's|Assets/Photon/PhotonLibs/netstandard2.0/Photon3Unity3D.dll|Assets/Photon/PhotonLibs/netstandard2.0/release/PhotonClient.dll|' -e 's|1900b0aP.dag/Fusion.Unity.ref.dll|1900b0aE.dag/Fusion.Unity.ref.dll|' -e 's|1900b0aP.dag/SomniumSpace.Network.ref.dll|1900b0aE.dag/SomniumSpace.Network.ref.dll|' "$OUT/world.rsp"
echo "-out:\"$OUT/SomniumEuroTruck.dll\"" >> "$OUT/world.rsp"
( cd "$P" && find "Assets/#User/SomniumEuroTruck" -name "*.cs" -not -path "*/Editor/*" | sort | sed 's/.*/"&"/' ) >> "$OUT/world.rsp"
( cd "$P" && "$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -noconfig @"$OUT/world.rsp" ) > "$OUT/world.out" 2>&1
e=$(grep -c 'error CS' "$OUT/world.out"); w=$(grep -c 'warning CS' "$OUT/world.out")
echo "world (player build, $(grep -c '\.cs"$' "$OUT/world.rsp") files): errors $e warnings $w"
grep 'error CS' "$OUT/world.out" | sed 's|.*SomniumEuroTruck/||' | head -25
[ "$e" = "0" ]
