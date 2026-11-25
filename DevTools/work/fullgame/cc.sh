#!/bin/bash
# cc.sh <out.exe> <src.cs...> : compile a probe with the world's archive readers (+ EXTRA files) like DevTools/work/fines/mk.sh
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"; E="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck/Ets"
L="$U/UnityReferenceAssemblies/unity-4.8-api"
OUT=$1; shift
X=(); if [ -n "$WITHFS" ]; then X=("$E/EtsFileSystem.cs" "$E/EtsHashFs.cs" "$E/EtsCityHash.cs" "$E/EtsSii.cs" "$E/EtsGDeflate.cs" "$E"/GDeflate/*.cs); fi
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -nologo -noconfig -nostdlib+ -langversion:9 -optimize+ -nowarn:CS0168,CS0219,CS0414,CS0649,CS0162,CS1591 \
 -r:"$L/mscorlib.dll" -r:"$L/System.dll" -r:"$L/System.Core.dll" -r:"$L/Facades/netstandard.dll" -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" \
 -r:"$L/System.Drawing.dll" -out:"$OUT" "$@" "${X[@]}" 2>&1 | grep -v "warning" | head -40
