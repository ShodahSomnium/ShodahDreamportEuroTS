# How to work on the ETS2 Dreamport (for agents)

- World: Somnium Space world (Unity 6000.3.5f2, URP 17.3), project D:/Dreamport/Euro Truck Simulator/Euro Truck Sim,
  code in Assets/#User/SomniumEuroTruck (asmdef SomniumEuroTruck, namespace SomniumSpace.Worlds.SomniumEuroTruck).
  It reads the player's own ETS2 Demo 1.61 (E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo, HashFS v2
  .scs archives; base.scs holds the whole game incl. /def, /material, /model, /map, /ui, /effect) at runtime and
  rebuilds the game natively. It never ships game assets. README.md in the code folder = design log with measured
  game-format facts (grep it).
- Tools (persistent, here in DevTools/):
  - BUILD: bash DevTools/build/build.sh <your outdir>  -> compiles exactly like Unity's player build (Unity's own rsp,
    C# 9, all .cs under the code folder except Editor/). Must say errors 0. New .cs files are picked up automatically
    but need a .meta next to them (fileFormatVersion: 2 + a fresh random 32-hex guid; grep -r it under Assets to
    prove it unique).
  - SERVER GUARD: bash DevTools/fusionapi/check.sh <outdir>/SomniumEuroTruck.dll -> must say "all ... proven". The
    upload server has an OLDER Fusion; any new Fusion/SomniumSpace member it prints is unproven - avoid it.
  - RENDER LAB: DevTools/renderlab (wait for the file DevTools/renderlab/READY; see its README.md). Renders the world
    to PNG through the real code/shaders. One run at a time (lock). Real-game screenshots: DevTools/refshots/
    (u025 = the real ETS2 in-cab at a yard).
  - PROBES: write small console programs that compile the world's pure parsers (Ets/EtsFileSystem.cs, EtsHashFs,
    EtsCityHash, EtsSii, EtsMap*, EtsModelData, EtsTobj, EtsDds, EtsPixels, EtsMaterialFile, EtsToken, EtsGDeflate/* ...)
    to read the archives: compile with
    "C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data/NetCoreRuntime/dotnet.exe" "C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Data/DotNetSdkRoslyn/csc.dll" -noconfig -nostdlib+ -langversion:9
    -r:<Unity>/Editor/Data/MonoBleedingEdge/lib/mono/4.7.1-api/{mscorlib,System,System.Core}.dll (+ Facades/netstandard.dll,
    + Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll if a parser needs Vector3/Mathf), run with
    <Unity>/Editor/Data/MonoBleedingEdge/bin/mono.exe x.exe. EtsFileSystem.Mount(gameDir, out _) opens the archives.
    In Git Bash export MSYS_NO_PATHCONV=1 before passing archive paths that start with "/".
  - SOUND PROBE: DevTools/work/sound/probe (bash cc.sh <exe> ... like mk.sh; probe.exe scan | list | dump | fev | events |
    sweep | truckset) decodes the game's FMOD banks offline with the world's own EtsFmodBank/EtsFmodStudio/NVorbis
    (README "2026-10-14: the game's real sounds"). Its out/ holds decoded game audio: never copy it into Assets.
    DevTools/work/sound/p2 (bash cc.sh -> p2.exe ev <bank> [regex] | gmap | tl | dec | chk | heavy | msnd <gx> <gz> | tmap
    <truck>) builds against the CURRENT world sound code (scatterers, playlists with silence, trims, map .snd items;
    README "2026-10-14: no more synthesised sounds"); render lab RL_AUDIO runs the ambience in Unity and logs its output.
  - DevTools/orig/code_2026-10-04 = code snapshot before this round (diff against it).
- Client log after a user test: C:/Users/derii/AppData/LocalLow/Somnium Space Ltd/Somnium Space VR/Player.log
  (world lines tagged [SomniumEuroTruck]).
- RULES: C# 9 only. Only whitelisted assemblies (UnityEngine modules incl. XR/Physics/Audio/InputLegacy, UI, TMP,
  URP/Core RP, XRI, SomniumSpace.Bridge, Fusion, SomniumSpace.Network) - NO Unity.InputSystem, Unity.XR.CoreUtils,
  Unity.XR.OpenXR. Never run Unity on the user's project (only the lab copy). Do not edit the scene
  (Assets/Euro Truck Scene.unity) - put needed scene changes in your report; the scene overrides serialized C# defaults.
  A new shader is only in the upload if the scene references it: extend existing shaders instead. Every custom shader
  pass must be single-pass-instanced-stereo correct. VR runs at 90-120 Hz: keep per-frame work small, no per-frame
  allocations. Multiplayer: anything random must be seeded from shared data. Several agents edit the code at the same
  time: only touch the files your brief gives you, re-Read right before every Edit, keep edits in shared files small
  and additive; if the build fails in a file you don't own, wait a minute and retry. Append to README.md only with a
  shell append (cat >> README.md), never an Edit. The user's standing instruction: "check the game files and
  replicate it the same" - take every rule and number from the game's own files (defs, .sii, .mat, /ui layouts,
  locale, the game's shader programs under /effect), never invent; say what evidence each choice rests on. The user
  wants RESULTS: no long review loops; build it, compile, guard, a lab render where visual, report.
- Log one-off lines tagged [SomniumEuroTruck] proving each new feature runs (existing files show the style).
