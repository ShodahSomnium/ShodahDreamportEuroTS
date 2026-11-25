#!/bin/bash
# usage: build.sh Name.cs [extra world files...]  -> Name.exe
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"
W="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck/Ets"
A="$U/UnityReferenceAssemblies/unity-4.8-api"
SRC="$1"; shift; LOCAL=(); while [ -f "$1" ]; do LOCAL+=("$1"); shift; done
FILES=("$W/EtsFileSystem.cs" "$W/EtsHashFs.cs" "$W/EtsCityHash.cs" "$W/EtsSii.cs" "$W/EtsToken.cs" "$W/EtsModelData.cs" "$W/EtsGDeflate.cs")
for f in "$W"/GDeflate/*.cs; do FILES+=("$f"); done
for f in "$@"; do FILES+=("$W/$f"); done
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -noconfig -nostdlib+ -langversion:9 -nowarn:0168,0219,0414,0649,0169 -unsafe -out:"${SRC%.cs}.exe" \
  -r:"$A/mscorlib.dll" -r:"$A/System.dll" -r:"$A/System.Core.dll" -r:"$A/Facades/netstandard.dll" -r:"$A/Facades/System.Runtime.dll" -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" \
  "$SRC" "${LOCAL[@]}" "${FILES[@]}" 2>&1 | grep -E "error|warning CS8" | head -30
