#!/bin/bash
# cc2.sh <out.exe> <src.cs...> : like cc.sh but with mod/EtsMapTerrain.cs + mod/EtsMapSector.cs (road.PreviousLength read)
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"; E="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck/Ets"
L="$U/UnityReferenceAssemblies/unity-4.8-api"; D="$(dirname "$0")"
OUT=$1; shift
X=(); for f in EtsFileSystem EtsHashFs EtsCityHash EtsSii EtsGDeflate EtsMap EtsToken EtsPrefabDescriptor EtsModelData EtsWorker EtsPrefabNav EtsMaterialFile EtsTobj $EXTRA; do X+=("$E/$f.cs"); done
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -nologo -noconfig -nostdlib+ -langversion:9 -unsafe -optimize+ -nowarn:CS0168,CS0219,CS0414,CS0649,CS0162,CS1591 \
 -r:"$L/mscorlib.dll" -r:"$L/System.dll" -r:"$L/System.Core.dll" -r:"$L/Facades/netstandard.dll" -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" \
 -out:"$OUT" "$@" "$D/mod/EtsMapTerrain.cs" "$D/mod/EtsMapSector.cs" "${X[@]}" "$E"/GDeflate/*.cs 2>&1 | grep -v "warning" | head -40
