#!/bin/bash
# mk.sh <out.exe> <src.cs...>  compiles a probe against the world's parsers; EXTRA="EtsX.cs EtsY.cs" adds Ets files by name
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"; E="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck/Ets"
OUT=$1; shift
L="$U/UnityReferenceAssemblies/unity-4.8-api"
X=(); for f in $EXTRA; do X+=("$E/$f"); done
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -nologo -noconfig -nostdlib+ -langversion:9 -unsafe -nowarn:CS0168,CS0219,CS0414,CS0649,CS0162 \
 -r:"$L/mscorlib.dll" -r:"$L/System.dll" -r:"$L/System.Core.dll" -r:"$L/Facades/netstandard.dll" -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" \
 -out:"$OUT" "$@" "$E/EtsFileSystem.cs" "$E/EtsHashFs.cs" "$E/EtsCityHash.cs" "$E/EtsSii.cs" "$E/EtsGDeflate.cs" "$E"/GDeflate/*.cs "${X[@]}" 2>&1 | grep -v warning | head -30
