#!/bin/bash
# cc.sh <out.exe> <src.cs...> : compiles a trees probe with the world's map readers (+ EXTRA world files under Ets/)
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"; E="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck/Ets"
L="$U/UnityReferenceAssemblies/unity-4.8-api"
OUT=$1; shift
X=(); for f in EtsFileSystem EtsHashFs EtsCityHash EtsSii EtsGDeflate EtsMap EtsMapSector EtsMapTerrain EtsToken EtsPrefabDescriptor EtsModelData EtsWorker $EXTRA; do X+=("$E/$f.cs"); done
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -nologo -noconfig -nostdlib+ -langversion:9 -unsafe -optimize+ -nowarn:CS0168,CS0219,CS0414,CS0649,CS0162,CS1591 \
 -r:"$L/mscorlib.dll" -r:"$L/System.dll" -r:"$L/System.Core.dll" -r:"$L/Facades/netstandard.dll" -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" \
 -out:"$OUT" "$@" "${X[@]}" "$E"/GDeflate/*.cs 2>&1 | grep -v "warning" | head -40
