# Render lab

Renders the ETS2 Dreamport world to PNG through its REAL code, shaders and render settings (URP pipeline asset,
post-processing, the scene's Global Volume and sun, EtsPostFx, fog, EtsSky) at chosen game locations. It does this
without the Somnium client and without touching the user's project.

How it works: `render.sh` copies the code folder you give it into the lab's own Unity project (`proj/`, a throwaway
copy of the user's project). It then runs Unity 6000.3.5f2 in `-batchmode` on the GPU (D3D11, NOT `-nographics`),
once per site. `RenderLabEditor.Go` opens the scene snapshot and switches off every root except "Ets Map Viewer",
"Global Volume" and the sun. It points the map viewer at the site's centre and enters play mode.
`RenderLabRunner` then waits for the map stream to settle round each view and renders it to PNG, with a .txt next to
each PNG that records the camera, fog, sun, ambient, sky, climate and post-volume values.

Never run Unity on the user's real project. The lab only ever opens `DevTools/renderlab/proj`.

## Use

    bash "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/renderlab/render.sh" <code folder> <output prefix> [sites]

- `<code folder>`: a SomniumEuroTruck code folder with its .meta files. Use the live
  `D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/Assets/#User/SomniumEuroTruck` (only read), the untouched
  snapshot `D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/orig/code_2026-10-04`, or your own copy with a
  change in it. To compare before and after, render the snapshot and your copy with two prefixes.
- `<output prefix>`: for example `DevTools/renderlab/out/myfix_after`. Gives `myfix_after_a_yard.png` (+ `.txt`),
  `myfix_after_<site>_unity.log` (Unity's log; grep `[RenderLab]` and `[SomniumEuroTruck]`), and
  `myfix_after_<site>_runlog.txt`. Old images with the same prefix are deleted first.
- `[sites]`: optional, default `"dover szczecin"`. Pass only the site you need: each site costs one Unity start plus
  the map load (about 1-2 minutes).
- Exit code: 0 ok; 2 bad arguments; 4 = the code under test does not compile (the `error CS` lines are printed); 5 = the
  lab project is missing (see "Rebuilding the lab project" below).
- Example (the proof run, 2026-10-04: 121 s, out/check_a_yard.png and out/check_b_road.png):

      RL_VIEWS=a_yard,b_road bash DevTools/renderlab/render.sh \
        "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/orig/code_2026-10-04" \
        "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/renderlab/out/check" dover

  (The same command on the live code folder stopped with exit 4. Another agent's half-finished fence edit was in
  EtsMapBuilder.cs at the time, so `TwinPart` and `RailingPieceDrawn` did not exist yet.)

Other agents edit the live code at the same time. If the lab's compile fails in a file you don't own, wait and retry,
or render a copy of the snapshot that has your change in it.

## Sites and views

| site | centre (game) | views (RL_VIEWS names) |
|---|---|---|
| `dover` | user's 2026-09-24 HQ, (-33391.4, 24.0, -7420.0) | `a_spawn` (Unity 0,0,0 in the HQ garage, yaw 90), `a_yard` (the yard, -16,40), `a_trees` (same spot, yaw toward the most trees), `b_road` (a divided country road 345 m out, left lane, 2.52 m eye), `c_sky` (same, pitch -20, sky), `d_tree` (tree close-up near the road, 1.7 m eye) |
| `szczecin` | the game's own start yard, (14994.0, 46.8, -15235.2) | `e_yard` (yaw toward the busiest direction), `e_trees`, `e_tree` (tree + big_bush close-up) |
| `doverh` | as dover | hole survey of the user's VR drive points `d1`..`d4` |
| `prague` | client's Prague HQ, (14501.1, 73.1, 3624.6) | hole survey, points `p1`..`p9` (`p5` = where the user fell through the world) |

Views are 1920x1080 with a 60 degree vertical FOV (cab eye 2.52 m unless noted). To add a site or a view, edit
`ViewsFor` / `CentreFor` / `SurveyFor` in `labsrc/RenderLabRunner.cs` and the `case "$SITE"` list in `render.sh`.

Hole survey (`doverh`, `prague`): at each point the lab does a 2 m ground census 60 m round the point. It renders 8
compass views at cab height (2.6 m, pitch 2) and 4 views from 30 m up (pitch 25), at 1280x720. Each view is rendered
normally and again with post-processing off and the sky cleared to pure magenta. Every magenta pixel below the horizon
is a hole. Holes are ray-cast and binned in 6 m cells, with what stands round each cell, what should draw over it, and
every collider along the ray. Output: `<prefix>_<point>_<cab|up>_y<yaw>.png`, plus `_void.png` when a view has at
least 30 hole pixels, plus `<prefix>_<site>_holes.txt`.

## Environment variables

- `RL_VIEWS=a_yard,b_road`: only these views (on survey sites, these points: `RL_VIEWS=p5,p9`).
- `RL_FOV=60`: vertical FOV in degrees.
- `RL_NOPOST=1`: also writes `<view>_nopost.png`, with the camera's post-processing off.
- `RL_TIMEOUT=1500`: seconds the in-editor run may take per site. The script kills Unity after RL_TIMEOUT + 900 s.
- `RL_SHOTS="name,x,y,z,yaw,pitch;..."`: renders fixed poses (Unity coordinates; pitch 90 = straight down), each
  normally plus a void render with hole analysis. Gives `<prefix>_shot_<name>.png` / `_void.png` and
  `<prefix>_<site>_shots.txt`. Works on any site.
- `RL_PROBE="x,z,r,step;x0,z0,x1,z1,step;..."`: lists every collider under a grid (4 fields) or a line (5 fields), on
  all layers and back faces too, and every renderer over the middle. Gives `<prefix>_<site>_probe.txt`.
- `RL_PAIRS_AB=1` (survey sites): repeats each view with the blended terrain pairs swapped for their flat stand-ins.

Unity coordinates are relative to the site centre: game x = centre x + unity x, game z = centre z - unity z.

## Lock

One lab run at a time. `render.sh` takes `DevTools/renderlab/LOCK` (`mkdir`). `LOCK/owner` says who holds it and since
when. A second caller waits, polling every 15 s. The lock is removed on exit, and a lock older than 3 hours counts as
stale. Never delete someone else's lock while their Unity is running. If you need the lab project for anything else
(for example rebuilding it), take the same lock first.

## Image tools (Windows PowerShell; pass Windows-style or forward-slash paths)

- `powershell -NoProfile -ExecutionPolicy Bypass -File imgstats.ps1 <png> "sky:0,0,1920,300" "road:700,700,1200,1080"`:
  gives, for each region, the mean sRGB, mean luma (Rec.709 on sRGB, 0-255), luma standard deviation, mean HSV
  saturation, and the linear-light mean.
- `powershell -NoProfile -ExecutionPolicy Bypass -File imgdiff.ps1 <a.png> <b.png>`: gives the mean absolute
  difference per channel and the share of pixels that differ by more than 8. Two runs of the same code differ slightly
  (wind, birds), so compare against that noise floor.
- `powershell -NoProfile -ExecutionPolicy Bypass -File imgcrop.ps1 <in> <out.png> x y w h [scale]`: crops a region,
  with an optional nearest-neighbour zoom, for a close look with Read.
- Real-game reference screenshots to compare against are in `DevTools/refshots/` (u025 = the real ETS2 in-cab view
  at a yard).

## Files

- `render.sh`: the driver (lock, code copy, scene snapshot, Unity run, watchdog, summary).
- `labsrc/RenderLabRunner.cs`, `labsrc/Editor/RenderLabEditor.cs`: the in-Unity part. They are copied into
  `proj/Assets/RenderLab/` on every run, so edit them here, not in proj.
- `proj/`: the lab's Unity project (about 3 GB with its Library). Its scene is always reset to
  `DevTools/orig/scene_2026-10-04.unity`.
- `rebuild_proj.sh`: rebuilds `proj/` from the user's project (Assets, Packages, ProjectSettings; code and scene from
  DevTools/orig) and imports it once in batchmode (5-10 minutes).
- `probe/ProgramRL1.cs`: an archive probe (compile it as described in DevTools/AGENTS.md "PROBES"). It picks
  country-road spots with vegetation close to both sides of the road. This is how `b_road` was chosen.
- `out/`: the default place for renders.
- `_recover/replay.pl`: how these files were restored on 2026-10-04. It replays every Write, Edit and file-editing Bash
  call from the 2026-09-25 agents' transcripts, in time order. Bash calls are replayed the way the Bash tool ran them:
  the tool turns every `\\` into `\` before bash sees it, so the replay does the same. Every replayed grep output
  matched the original line for line.

## Rebuilding the lab project

On 2026-10-04 the old lab project in the session scratchpad (Temp) had been gutted by Windows temp cleanup: 600 of
its ~33000 files were left and nothing compiled. It was rebuilt here, outside Temp. If `render.sh` exits 5, or every
compile fails with missing Fusion / TMPro / UnityEngine.UI / SomniumSpace.Bridge, run `bash rebuild_proj.sh`.
If the user's project gains new packages or assets the world needs, run it too.

## AI traffic views (2026-10-14)

`RL_TRAFFIC="name,x,y,z,yaw,pitch;..." [RL_TRAFFIC_WAIT=150] render.sh <code> <prefix> <site>`: the camera stands at the
first pose (Unity coordinates of the site), an EtsTraffic runs (role Local, PerFocus 40, a 40 ms build budget), the lab
waits RL_TRAFFIC_WAIT seconds, then renders each pose to `<prefix>_traffic_<name>.png` with a `.txt` listing the vehicles
and trailers within 300 m. Busy roads used: Dover A20 motorway lane at Unity (-964.8, 25.1, -293.2) heading 81 degrees,
Prague D0 motorway lane at (-368.2, -1.9, 509.8) heading -5 (DevTools/work/traffic/probe/lanes.cs lists such lanes).

## Time of day and weather (2026-10-14)

The world's day runs now (EtsDayNight). Every lab run holds it at 10:00 in clear weather (RenderLabRunner sets
`EtsDayNight.LabMinutes = 600`, `LabWeather = 0`) so renders stay comparable. To render other times:

    RL_DAYNIGHT="noon:780:0;dusk:1290:0;night:1380:0;rain:780:1:4" RL_VIEWS=b_road,c_sky \
      bash DevTools/renderlab/render.sh <code folder> <prefix> dover

Each spec is `name:game minutes:weather[:bad slot]` (minutes since Monday 00:00, 780 = 13:00; weather 0 nice .. 1 bad;
bad slot 0-9 of bad.sii, default the hash's). Each view is placed once and rendered per spec after the sky's panoramas
are read: `<prefix>_<view>_<name>.png` and a `.txt` with the day line (profiles, exposure, rain, wetness, lamps).

## RL_AUDIO (2026-10-14, sound)

`RL_AUDIO="name,x,y,z;..."` (Unity coordinates, any site; `RL_AUDIO_WAIT` seconds per pose, default 60): puts an
AudioListener on the lab camera and runs an EtsAmbience (the game's map sound items, default beds, rain) at each pose,
plays the truck/trailer/crash/UI one-shots once, and logs every 5 s `[RenderLab] audio <pose> t+N s: listener rms ...,
game clips ..., streams ..., sources playing ...` (Unity's audio does run in the batchmode lab: 48 kHz stereo). No images.
Example: `RL_AUDIO="hq,0,2,0;forest,-456,3,-151" RL_AUDIO_WAIT=45 bash render.sh <code> out/sound dover`.

## RL_FINDMAT (2026-10-27, water)

`RL_FINDMAT=water RL_SHOTS="..." render.sh <code> <prefix> <site>`: after the shots, every loaded renderer whose material name
contains the text is written to `<prefix>_<site>_find.txt` (renderer, material, shader, Unity bounds centre/size, drawn or not),
so views can be aimed at it. Note: a lab copy of the live code needs DevTools/work/floating/before_code/SceneNetworking.cs and no
Editor/EtsFusionRebake.cs (the lab project has the older Fusion).

## Ground survey: floating ground, tunnels, invisible colliders (2026-10-27)

Site `austria`: centre `RL_CENTRE="gx,gy,gz"` (game), default the A9/S6 junction Knoten St. Michael (16900, 40, 18300).

`RL_GROUND="name,gx,gz,r;..."` (GAME coordinates, any site; `RL_GROUND_GRID` m, default 4): at each point, after the stream
and the ground fill settle, measures a square of half-width r: (A) ground-fill triangles with more than 3 m of air under
them or more than 5 m over every terrain top 25 m round them; (B) terrain skirts whose foot (0.35 m outside, 3 points) has
nothing within 2 m under it, or that hang over a road / junction surface; (C) terrain, fill, skirts and undrawn colliders in
the 4.75 m clearance over every road / junction surface ("covered" = something drawn 4.75-80 m over it: tunnel, mountain,
bridge; "poking" = under 0.6 m; "backed" = a drawn surface within 0.6 m); (D) every collider with nothing drawn on it by kind
and name, and items whose colliders stand with nothing drawn at all. Writes `<prefix>_<site>_ground.txt` (a line per point,
totals, worst spots and examples in game coordinates). It switches the world's static batching off for the run
(EtsMapViewer.StaticBatching, by reflection) so the skirt meshes can be read. A run of 53 points round Austria took ~45 min
and crashed the editor at the 46th (Switzerland): compare points, not only totals.

Lab copies of the CURRENT code need the pre-Fusion-2.1 `SceneNetworking.cs` (from DevTools/orig/code_2026-10-04) and no
`Editor/EtsFusionRebake.cs`: the lab project still has the old Fusion (compile errors otherwise).

## 2026-10-31 additions

- `RL_PICK="shot@px,py;..."` (pixels of the 1280 x 720 shot, y down): renderers along that pixel's ray, nearest first; for a
  readable mesh the hit triangle ("TRI d front|BACK [corners]") and the renderer's layer, shader, queue, forceRenderingOff,
  isVisible, vertex count and collider; then every collider along the same ray (all layers, back faces). Turns static
  batching off.
- `RL_SKIRTDEBUG="x0,z0,x1,z1"` (Unity x/z): EtsTerrainBuilder logs every skirt decision for edges whose middle lies in that
  box ("Skirt debug: edge ... : no wall / nothing within SkirtDepth under the middle (retry) / WALL to y (late)").
- `RL_LATECLAMP=1`: every terrain-item side skips its build-time clamp and takes the late path (DeferClamp / PumpClamps),
  as when the road footprints lag the stream in the client.
- The output prefix must be an absolute path (a relative one is resolved inside the lab project: "Could not find a part of
  the path ...renderlab/proj/DevTools/...").
