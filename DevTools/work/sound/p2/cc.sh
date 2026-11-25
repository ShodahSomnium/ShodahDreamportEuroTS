#!/bin/bash
# cc.sh: builds p2.exe (tmap | ev | cat | ls) against the world's sound + archive code
U="C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data"; C="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck"; E="$C/Ets"
L="$U/UnityReferenceAssemblies/unity-4.8-api"; D=$(dirname "$0")
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" -nologo -noconfig -nostdlib+ -langversion:9 -optimize+ -unsafe -nowarn:CS0168,CS0219,CS0414,CS0649,CS0162,CS1591 \
 -r:"$L/mscorlib.dll" -r:"$L/System.dll" -r:"$L/System.Core.dll" -r:"$L/Facades/netstandard.dll" -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" -r:"$U/Managed/UnityEngine/UnityEngine.AudioModule.dll" \
 -out:"$D/p2.exe" "$D"/*.cs "$E/EtsFileSystem.cs" "$E/EtsHashFs.cs" "$E/EtsCityHash.cs" "$E/EtsSii.cs" "$E/EtsGDeflate.cs" "$E"/GDeflate/*.cs \
 "$E/EtsFmodBank.cs" "$E/EtsFmodStudio.cs" "$E/EtsVorbisSetups.cs" "$E"/Vorbis/*.cs "$E"/Vorbis/*/*.cs "$C/EtsGameSound.cs" "$E/EtsVehicleSpec.cs" "$E/EtsWorker.cs" "$E/EtsMapSounds.cs" "$E/EtsTrafficCatalog.cs" "$E/EtsModelData.cs" "$E/EtsMapSector.cs" "$E/EtsToken.cs" "$E/EtsMapTerrain.cs" 2>&1 | grep -v warning | head -30
