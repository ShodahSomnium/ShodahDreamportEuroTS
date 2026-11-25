# Somnium Euro Truck

Euro Truck Simulator 2 inside a Somnium Space world, the way Somnium Quake and Somnium Mythology
do it: the game's own data files are read from the player's installation at runtime and rebuilt in
Unity. Nothing from the game is shipped with the world. Non-XR first; VR is the next phase.

## Where it stands

| Layer | State |
| --- | --- |
| Archives (`.scs`, HashFS v1, CityHash64) | Done. All 8 demo archives mount in ~0.3 s; every one of 37,562 listed paths in def.scs resolves. |
| Definitions (`.sii` / `.sui`, includes) | Done. 377 road looks, 1,839 prefabs, 5,756 world models, 125 cities parse. |
| Textures (`.tobj` -> `.dds`) | Done. DXT1/DXT5/BC4/BC5/BC7 uploaded as-is; DXT3 and uncompressed converted. |
| Materials (`.mat`, both spellings) | Done. Effect names mapped onto URP Lit: cutout, glass, emissive, invisible. |
| Models (`.pmd` v4 + `.pmg` 0x15) | Done. Looks, variants, parts, locators; truck/wheels/interior/bridge parse with 0 bad indices. |
| **Model viewer** in the scene | Built. Press Play: the DAF XF Euro 6 (chassis 4x2 + cabin C) with wheels and interior. |
| Map sectors (`.base/.aux`, `.ppd`) | Done for roads, prefabs, models, buildings, cut planes; other types stepped over. All 271 demo sectors parse in 8.5 s: 97.3% of declared items, 1.11 M nodes, 50,302 roads, 14,318 prefabs (every definition and descriptor resolves), 192,317 models. |
| **Map viewer** in the scene | Runs. Roads bent along their splines from the game's template road models with their shoulders (ribbons for the older looks), prefabs aligned on their nodes, models placed, terrain strips; fly camera in the editor. First run: 47 roads, 2 prefabs, 186 models in 0.9 s, everything in place. After the first build it streams: every half second it builds whatever lies within 1 km of the truck and unloads what is more than 1.6 km away, reading new sectors on demand (untested in Play as of 2026-09-09 02:30). |
| **Terrain** (`.data` files, terrain items) | Built. The game has no heightmap: ground is a strip beside every road side and terrain item, shaped by a profile and painted quad by quad. Decoded from the sector `.data` files (61,175 records across the demo, 97 % of roads, all 56,812 terrain items, every brush material resolving). Prefab corner terrain is read but not yet built. |
| **Vegetation** | Built, untested in Play. Each road side and terrain item carries up to three vegetation bands (set, density, from, to); the set (`veg.<token>`) lists `vegetmodel.*` units whose medium-poly models are scattered with the item's seed and stood on the terrain by ray cast. In the centre sector 91 of 105 roads have bands and every set and model resolves. Density is thinned to 8 % and capped at 25 plants per band, since the game's unit is undocumented (`EtsVegetationBuilder.Thinning`). |
| **Prefab terrain and profile blending** | Built, untested in Play. Ground round junctions grows from the prefab descriptor's border points using the connected road's terrain on the matching side (fallback: the prefab's own corner data); each road side blends from the previous road's profile over its transition length (16, 8, 4 or 32 quads by flag). Since Phase 1 (2026-09-22) the prefab's model, its terrain and its spawn points all stand on map node 0's full rotation, pitch and roll included, not its yaw alone (`EtsMapBuilder.TryPrefabFrame`; see "Phase 1" at the end). |
| **Phase 1: tablet, per-player trucks, saved careers** | Built behind `EtsFeatures.NewFlow` (off), offline-tested only (2026-09-22). With the flag off the world plays as before, apart from the list at the start of "Phase 1" at the end. |
| **Sound** | Synthesised. The game's sounds are FMOD banks of Vorbis packets without setup headers (codec 15 in `daf_eu6.bank`), undecodable without FMOD's tables, so the engine note is built from the engine's own figures (six cylinders, rpm, load), plus turbo, tyre and wind noise and a two-tone horn (H). Attenuated inside the cab. |
| **Full truck** | Built. The showroom save (`/def/initial_save/preview/daf_xf_euro6.sui`) lists every accessory; `EtsTruckAssembly` reads it and hangs 20 add-ons (mirrors, door steps, handles, sun shield, side skirts, intakes, inlays, fender, badges, rear bumper, mud flaps, cables) on the locators named by their slots, the rear tyres and rims on the rear axle, the interior model with its add-ons (mirrors, sun shield, the steering wheel, which turns with the front wheels) and the dashboard instrument model. |
| **Trailer** | Built. The company curtainsider (two axles, `ch_2_lod0`) from its chassis and wheel definitions, a rigid body with raycast wheels, coupled at its kingpin to the truck's fifth wheel by a joint with the pitch and roll play from `physics.sii`. Laden with the cargo's mass. |
| **First level: quick jobs** | Built. On start the whole map is read (10 s spread over frames) and its road network indexed (75,645 nodes: roads both ways, prefab nodes fully connected). Offers come from the nearest depot's shipped cargo (`/def/company/<x>/out`) to depots in other cities that take it (`/in`): short, middling and long. Taking one stands the truck at the bay with the laden trailer, draws the route (Dijkstra, 7 ms for 5.8 km) as a green ribbon along the roads and pays the cargo's per-unit-per-kilometre rate on stopping in the destination bay. Home city Nürnberg, the nearest city to the map centre with six depots. |
| **Menu and sound** | Built. Escape opens the runtime-built menu with the job market, camera, sound, respawn and controls. Sounds are synthesised (engine, turbo, tyres, wind, horn, reversing alarm). |
| Buildings, signs | Not built. |
| **AI traffic** | Built, networked, offline-tested only (2026-09-22). Cars from the game's own AI models on lanes built from the map, run by one client and drawn by the rest. See "Traffic" at the end. |
| **Truck driving** | Built, untested in Play as of 2026-09-09 02:00. A rigid body with one raycast wheel per wheel locator, set up from the game's chassis (kerb weight per axle, powered axle, residual travel), engine (torque, rev limit), gearbox (ratios, final drive), tyres (wet grip, rolling resistance) and physics.sii (steering lock and its fall-off with speed, air resistance, brake factor). Automatic gearbox, chase and interior cameras (interior field of view, head offset and look limits from the game's camera definition), the Somnium player seated at the driver locator. Spawns on the road nearest the centre once the map is built. |

The earlier window-capture component (`EtsEmbeddedGame`) still exists as a side tool, exactly as the
Quake and AoM projects keep theirs, but it is not the product: it launches the real game as a
separate process, which is not "inside Somnium".

## Files

| File | Role |
| --- | --- |
| `Ets/EtsCityHash.cs` | CityHash64 as the archives use it: 1.0 short branches, 1.1 long loop. Established by test against the archive. |
| `Ets/EtsHashFs.cs` | One `.scs` archive: table, lookup by path hash, zlib entries, folder listings. |
| `Ets/EtsFileSystem.cs` | All archives as one tree in the engine's mount order; SII cache; definition families. |
| `Ets/EtsToken.cs` | Base-38 packed identifiers (looks, variants, parts, locators). |
| `Ets/EtsSii.cs` | Definition file parser with `@include` at file and unit level. |
| `Ets/EtsTobj.cs`, `Ets/EtsDds.cs` | Texture object and DDS readers. |
| `Ets/EtsMaterialFile.cs` | Material file parser (old `material :` and generated `effect :` forms). |
| `Ets/EtsModelData.cs` | pmd + pmg parser into a Unity-free model description. |
| `Ets/EtsEngine.cs` | Mounts once per session; owns the caches. |
| `Ets/EtsTextureCache.cs`, `Ets/EtsMaterialCache.cs` | Game textures and materials as Unity objects, once each. |
| `Ets/EtsModelBuilder.cs` | Model description to GameObjects; the game-to-Unity frame conversion lives here. |
| `Ets/EtsMapSector.cs` | One sector's `.base` + `.aux`: node tables, item headers, road/prefab/model/building bodies. |
| `Ets/EtsPrefabDescriptor.cs` | `.ppd` reader: the prefab's node positions and directions. |
| `Ets/EtsMap.cs` | Sectors on demand, node lookup across sectors, road-look / prefab / model / scheme definitions by token. |
| `Ets/EtsMapTerrain.cs` | Terrain quad grids, terrain sides, terrain items (type 1), and the sector `.data` reader. |
| `Ets/EtsTerrainBuilder.cs` | Ground meshes: profile-shaped strips beside road sides and terrain items, one mesh per brush material. |
| `Ets/EtsVegetationBuilder.cs` | Trees and bushes from the vegetation bands, seeded per item, stood on the terrain by ray cast. |
| `Ets/EtsClimate.cs` | The game's nice-weather fog applied to Unity's render settings. |
| `Shaders/EtsDecal.shader` | Ground decals with vertex-colour fading, as the game's `decal.over` effect. |
| `Ets/EtsMapBuilder.cs` | Roads bent along splines (template slices or ribbons) with shoulders and terrain, prefab alignment, model placement. |
| `Ets/EtsVehicleSpec.cs` | The truck's definitions: chassis, engine, gearbox, tyres, physics.sii, interior camera. |
| `EtsTruckController.cs` | Rigid body with raycast wheels, tyre model, automatic gearbox, brakes, steering; keyboard input. |
| `EtsTruckCamera.cs` | Chase and interior cameras placed in LateUpdate on whichever camera renders (editor or Somnium). |
| `EtsTruckSeat.cs` | Seats the Somnium local player at the driver locator through the Bridge, as the Quake world does. |
| `EtsTruckAudio.cs` | Synthesised engine, turbo, tyre, wind and horn sounds driven by the controller's rpm, load and speed. |
| `EtsMenu.cs` | Runtime-built Unity UI menu: drive, jobs, camera, sound, respawn, controls. Escape toggles. |
| `EtsJobs.cs` | Quick jobs: map indexing, offers from shipped and accepted cargo, trailer, route, delivery and pay. |
| `EtsTrailer.cs` | The curtainsider: model, wheels, rigid body, kingpin joint to the fifth wheel. |
| `EtsRouteLine.cs` | The route ribbon along the roads and the distance left along it. |
| `Ets/EtsRoadGraph.cs` | Road network over map nodes with Dijkstra and nearest-node search. |
| `Ets/EtsTruckAssembly.cs` | A saved vehicle's accessory list turned into models on locators, outside and inside; the steering wheel on the dashboard skeleton's posed bone. |
| `Ets/EtsAnimation.cs` | A `.pma` bone animation (version 5): frame times, bone indices, keyframes, sampling. |
| `Ets/EtsSkeletonPose.cs` | A skeleton's pose from bind locals plus applied animations; world transform of any bone. |
| `Ets/EtsPrefabTerrainBuilder.cs` | Ground round prefabs from the descriptor's border points and the connected roads' terrain. |
| `EtsModelViewer.cs` | Scene component: loads the truck, hangs wheels on locators, mounts the interior, sets up driving. |
| `EtsMapViewer.cs` | Scene component: builds the map within a radius of a game position; editor fly camera. |
| `EtsEmbeddedGame.cs`, `EtsLaunchSpike.cs`, `Ets/EtsProcess.cs`, `Ets/EtsWindowCapture.cs`, `Ets/EtsCaptureThread.cs`, `Ets/EtsStartupOptions.cs`, `Ets/EtsInstall.cs` | The window-capture side tool and the install finder it shares with the engine. |
| `Editor/EtsSceneSetup.cs` | `Somnium Euro Truck` menu: model viewer, capability probe, embedded game. |
| `../../Euro Truck Scene.unity` | The world: Uploader (scripting on), light, platform, model viewer with status label. |

## Format facts, all measured on the demo (1.48.1.6)

> **The demo updated itself to 1.61.1.0 and several of these no longer hold.** The archives are HashFS
> v2, the map is version 907, every texture is a packed image, and the spawn point record is 36 bytes
> read w first. Every change and what it was measured against is in "Game 1.61" at the end. The 1.48
> facts below are kept because the code still reads 1.48 by them, branching on the version.

- **HashFS v1**: `SCS#`, u16 version 1, u16 salt 0, `CITY`, u32 entries, u32 table offset 0x1000.
  Entry 32 bytes: u64 hash, u64 offset, u32 flags (1 dir, 2 zlib, 4 verify), u32 crc, u32 size,
  u32 compressed size. Folder entries are text: one name per line, `*` prefix for subfolders.
- **CityHash64**: paths up to 64 bytes hash with the CityHash 1.0 branches (k3 constant,
  RotateByAtLeast1); longer paths with the 1.1 loop. Root folder is the hash of "".
- **Frame**: right-handed, Y up, truck front toward -Z, +X on the driver's right. Front faces are
  wound clockwise (Direct3D convention; 0 of 200 sampled DAF triangles agree with a right-hand
  cross product). Unity conversion: negate Z, keep the index order (the mirror alone makes them
  Unity's clockwise-front; reversing them renders everything inside out), quaternion (x,y,z,w) ->
  (-x,-y,z,w), tangent w negated, flip v. Quaternions are stored w first in pmg locators.
- **pmg 0x15 piece** (100 bytes): edges, verts, uv mask, uv channels, material, bb centre,
  diagonal, aabb, then at +60 stride and at +64.. absolute offsets of position, normal, uv, colour,
  unused, tangent, unused, unused, index. Extra uv channels follow at +8. Indices u16.
- **UV sets per effect**: `eut2.truckpaint.*` pieces carry three uv sets and read the base
  (ambient-occlusion mask) texture through set 2 (measured: mean brightness 0.7 and every
  coordinate inside 0..1 through set 2, versus tiling off the edge through set 0). `tsnmapuv`
  reads its base through set 0 and its normal map through set 1. Everything else seen uses set 0.
- **pmd 4**: 16 u32 header; look and variant names as tokens; per-part attribute range; attribute
  table (token, type, offset); values per variant; per-look material path offsets.
- **tobj**: path length at 40, path at 48, address modes at bytes 30/31 (0 repeat, 1 clamp,
  2 clamp-to-edge), type at 24 (2 = 2D, 5 = cube).
- **ppd** version 23: counts at 4..44 (nodes, nav curves, signs, semaphores, spawn points, terrain
  points, terrain variants, map points, trigger points, intersections), offsets at 48..92, node =
  104 bytes (terrain range, variant range, pos, dir, 8 input lanes, 8 output lanes).
- **Map sector** file: u32 version 898, u64 game id (token `euro2`), u32 3, u32 item count, items
  from byte 20; somewhere after them u32 node count and 56-byte node records (u64 uid, 3 x i32
  position / 256, quaternion w x y z, u64 backward item, u64 forward item, u32 flags), then a
  4-byte trailer. The table is not always last, so it is found by scanning for a count followed by
  that many node-like records. Node forward = rotation applied to (0, 0, -1), measured on 109 roads.
- **Map item head** (57 bytes): u32 type, u64 uid, 5 kdop minimums + 5 maximums as floats, u32
  flags, u8 view distance. Kdop axes are x, y, z, (x+z)/2, (x-z)/2 - the relation that identifies a
  real header. Items may reference nodes in the other file of the sector or in a neighbour.
- **Road** (type 3, body 212): +0 u32 flags, +4 look token (unit `road.<token>`), +28 variant token,
  +44/+52 left/right edge tokens (`road_edge.<token>`), +76 left terrain profile token + f32
  coefficient at +84, +88/+96 right profile + coefficient, +100 material token, +192 start node,
  +200 end node, +208 f32 length. Curve = cubic Hermite from node positions and forwards scaled by
  length. Template looks (`template_right` = a slice model in `/road_template/`) are bent along the
  curve; procedural looks use `road_size_left/right`.
- **Road templates** (`/road_template/ger/ger_road_1.pmd`): 15 m along z, 10.5 m wide. The road's
  material token names the template *look* (`ger_1`..`ger_5` = asphalt materials) and its variant
  token the centre-line variant (`single_de`, `broken_de`, `warn_de`, ...). Parts: `vis` (asphalt),
  `coll`, per-variant line decals (`eut2.dif.spec.decal.over`), and `vis_tran`, the road-to-terrain
  transition (ground-dirt decals plus a `weight.weight` two-texture blend). `vis_tran` and the blend
  pieces are skipped: drawn without terrain they cover the road with beige patches. Road edges
  (`road_edge.<token>`: `model`, `variant_5`, `variant_15` by slab slice length) are 1 m shoulder
  models built for the right side (x 0..1) and mirrored for the left; their visible part is a decal.
- **Prefab models** (`/prefab2/...tmpl.pmd`) follow the same pattern: looks `ger_1..ger_5`, one
  variant (`clean`), part `vis` (asphalt plus `weight.weight` ground pieces), part
  `decal` (ground dirt, `decal.over`), `coll_base` (`eut2.none`), and marking parts (`_giveroad`,
  `_midlines`, `_parking`, `_stopline`, ...) that no variant shows. The map item's "additional
  parts" are these part names, switched on by name. The `weight.weight` pieces are NOT skirts to be
  left out - in a company prefab they are the whole yard (see "The holes were the yard" below); they
  are drawn with the two-layer shader in mode 4.
- **Decals** (`eut2.dif.spec.decal.over`): texture alpha times *vertex* alpha. Measured on the
  junction: the dirt decal's vertex alpha runs 0..141 (mean 31), the lane lines' 255. Drawn with
  URP Lit the dirt was a hard beige streak (cutout) or a grey sheet (blend). `Shaders/EtsDecal.shader`
  (`SomniumEuroTruck/Decal`, referenced by the map viewer so the bundle ships it) multiplies colour
  and alpha by the vertex colour with the game's 128-means-one convention (the dirt wash carries
  vertex red about 10, so it darkens the asphalt rather than lightening it; lines carry 128 and
  alpha 255), blends in the transparent queue with a depth offset, and takes main-light shadows and
  fog. Texture alpha of `decal_ground_01`: 59 % near zero, 16 % opaque, mean 0.26.
- **Interior camera**: the game has no camera locator. The interior model's origin is the driver's
  eye; it hangs on the cabin's `interior2` locator (DAF: x -0.77, 2.81 m up, 1.94 m ahead of the
  chassis origin) and the camera unit's `head_offset` (0, -0.08, 0.08: down and back) is measured
  from it.
- **Steering wheel**: the wheel model lies flat in its own frame (rim at y -0.02, buttons and
  badge above the hub, back cover down to y -0.12, hand slots `slot_0..3` at 10, 2, 4 and 8
  o'clock), face +y toward the driver, top toward -z; it turns about its local y, two and a half
  turns lock to lock. The interior model's `steering_w` locator is NOT where it hangs: that
  locator is the column's base and its rotation is stale (on the DAF it would point the face at
  the floor; across the fifteen demo trucks it points down, up or forward at random). The game
  hangs the wheel on the animated dashboard model's bone named `steering_w`, whose +y is the
  wheel's axis, after posing the column with the interior definition's `wheel_updown_anim` /
  `wheel_frontback_anim` at `wheel_updown_anim_default` / `wheel_frontback_anim_default` (0 to 1
  of the animation; DAF 0.3 and 0.45). Checked on all fifteen trucks: composing each bone's
  local transform up the parent chain reproduces the file's bind matrices exactly, and every posed
  axis comes out up and back, 37 to 65 degrees from vertical (DAF XF Euro 6: 46 degrees, centre
  0.44 m below and 0.39 m ahead of the eye). The wheel's top is the highest point of the rim.
- **Skeleton and animation**: pmg 0x15 bones (count at +12, table at the offset at +72, 200
  bytes each): name token, bind matrix bone-to-model (4x4 row-major, translation in the last
  column), its inverse, stretch quaternion, rotation quaternion, translation, scale, sign, u32
  parent; quaternions w first. pma version 5: u32 version, frames, bones, u64 skeleton hash (the
  pmg carries the same at +24), float3 movement centre, radius, float3 first and last translation,
  length in seconds, then offsets to the frame lengths (one float per frame), the bone indices (one
  byte per animated bone) and the keyframes (56 bytes: stretch quaternion, rotation quaternion,
  translation, scale; frame-major). `EtsAnimation`, `EtsSkeletonPose`.
- **Materials**: `shininess` is a Blinn-Phong exponent (terrain 12-15, kerbs 8, decals 1,
  plastics 18, chrome 80, glass 95, paint 250); `specular` its colour (terrain 0.1-0.2, cloth 0,
  chrome and paint 1); `env_factor` the environment reflection strength of the `add.env`,
  `lamp.add.env`, `truckpaint` and `glass` effects, with `fresnel { bias, scale }` (bias = the
  reflectivity facing the viewer, 0.2 on paint and lamps, 0.1 on aluminium, 0 on wood); `nofresnel`
  marks true metals (chrome 1.0, steel 0.79); `indenv` masks the reflection by the texture's alpha;
  `reflection` on road and terrain materials is the wet-weather reflectivity (dry here, ignored).
  `EtsMaterialCache.SurfaceOf` turns these into URP metallic and smoothness: roughness
  (2 / (shininess + 2))^0.25 damped by the specular strength, metallic = env_factor for nofresnel
  metals and twice the facing reflectivity above 0.04 for the rest, reflective surfaces smoothed
  toward 0.95, glass always 0.9 and never metallic. A realtime reflection probe over the cab
  (128 px, one face per frame, refreshed every second) stands in for the game's environment map.
- **Mirrors**: each mirror glass piece uses effect `eut2.dif.lum` with a placeholder texture named
  for its mirror: `/material/environment/{close,close_s,far,far_s,side,front}_mirror_reflection.tobj`.
  `game_data.sii` `mirror_fov[]` gives their fields of view in that order: 55, 90, 55, 90, 60, 100.
  `EtsMirrors` puts a camera at each glass looking where the driver's gaze reflects, renders to a
  texture the glass shows flipped, two cameras per frame, only while the driver's camera is in
  the seat. Four seconds in it logs each picture's average colour and camera direction, so a
  black glass can be told from a wrong view.
- **Performance**: nodes are indexed map-wide (`EtsMap.FindNode` was scanning every loaded sector,
  a 30 ms hitch twice a second once the job system had read all 271); model meshes are built once
  per piece and shared; items are static-batched without their plants; roads cast no shadows;
  plants cast none and are thinned to 6 %. Streaming while driving builds only a few items a frame
  (`_streamItemsPerFrame`, default 6) and holds what it has built out to 2.6 km (`_unloadRadius`),
  so a new chunk arriving never stutters the drive and the road behind is not torn up. The mirrors
  render only in the interior view, at 256/192 px out to 180 m; the cab's reflection probe is a
  64 px cube out to 220 m, refreshed every two seconds, culling the truck out of its own reflection.
- **Physics layers**: the Somnium client's layer matrix decides what collides. Everything on the
  truck used to sit on Ignore Raycast, which collides with nothing solid, so the truck drove through
  houses and the trailer through everything. Now the truck and trailer bodies are on **Vehicle** and
  the map's buildings and props on **Environment**, which the matrix pairs; the drive surface
  (roads, junctions, terrain) stays on **Default**, which the Vehicle body passes through so the
  suspension, not the body box, meets the ground. The wheel, spawn and scatter rays use a ground
  mask of Default + Terrain (`EtsLayers.GroundMask`), so a wheel never catches on its own chassis or
  on a building. `Ets/EtsLayers.cs` holds the indices and the matrix reasoning.
- **Climate** (`/def/climate/default/nice.sii`, one entry per time-of-day slot, slot 3 midday):
  `fog_offset` 56 m, `fog_density` 0.0032 per metre beyond the offset, colours as HDR radiance
  (hex floats, `&3b536cb8` form). `EtsClimate` maps this to Unity linear fog from the offset to the
  90 % distance (about 775 m) and applies it at runtime, replacing the scene template's exponential
  fog (density 0.006, which whitens everything past 100 m).
- **Prefab** (type 4, body 46 + 8·parts + 20·nodes): prefab token (`prefab.<token>` gives
  `model_desc` and `prefab_desc`), variant token, u32 parts + tokens, u32 nodes + node uids, u32 0,
  u64 0, u16 origin, per node (u64 terrain profile, f32 coefficient), u64 0. Placement: descriptor
  node `origin` on map node 0, yaw from descriptor direction to map forward; map node i is
  descriptor node (origin + i) mod n. Verified to the centimetre on a T-junction.
- **Model** (type 5, body 64 + 8·parts): model token (`model.<token>` gives `model_desc`), look
  token, variant token, u32 parts + tokens, node uid, f32 x3 scale, u64 terrain material, u32
  terrain colour, u32.
- **Building** (type 2, body 48 + 4·n): scheme token (`bld_scheme.<token>`), u64 look, start node,
  end node, f32 length, u32 seed, f32 stretch, u32 n + n floats. **Cut plane** (type 8): u32 count +
  node uids. **Sign** (type 36): sign token, node uid, then variable data (stepped over).
- **Prefab descriptor** (`.ppd` v23): counts at 4..44, offsets at 48..92, node = 104 bytes
  (terrain range, variant range, float3 position at +16, float3 direction at +28, 8 + 8 lanes).
- **Road body, exact** (matches the TruckLib library byte for byte): +0 four flag bytes, +4 look,
  +12/+20 right/left traffic rule, +28/+36 right/left variant, +44 right side's right edge, +52
  right side's left edge, +60/+68 left side's edges, +76 right terrain profile + f32 coefficient,
  +88 left profile + coefficient, +100/+108 right/left look (the asphalt material), +116 material,
  +124 three railings per side (token + i16 offset), +184/+188 height offsets, +192/+200 nodes,
  +208 length. Header flag byte 1: left noise bits 0-1, right noise 2-3, left transition 4-5, right
  transition 6-7; byte 4 bit 0 high-poly. Body flag byte 1 bit 1 superfine.
- **Sector `.data` file**: 16-byte header (u32 898, u64 game id, u32 3), then u64 uid + payload in
  ascending uid order. Road payload (898): overlay token; per side (right, left): 2 models (token,
  i16, u16), u16 terrain size / 10, 3 vegetation records of 16 bytes, sidewalk material token,
  quad data; centre material token, colour, u16 rotation, u32 seed, centre vegetation (token, u16,
  u8, u8), 4 x u16, vegetation spheres (u32 count, 20 bytes each), then u32-counted lists of
  additional parts (8) x2, edge overrides (20) x2, variant overrides (12) x2. Newer versions
  append four edge-look tokens; 898 does not. Prefab payload: look token, six corner blocks of 38
  bytes (u16 size / 10, u16 from, u16 to, two vegetation records), vegetation parts (u32 count,
  32 bytes each), u32 seed, spheres, six times model/variant/look tokens, six quad grids. Six
  corners whatever the node count.
- **Terrain quad data**: u16 brush material count + (token, u16 rotation) each; u16 colour count +
  rgba each; u16 rows (across, one per profile step inside the size); u16 cols (along: one per
  15 m on a normal road, three on high-poly, one per 16 m on a terrain item); u32 quad count and 4
  bytes per quad row-major (byte 0 main material low nibble, blend material high nibble; byte 1
  opacity and a corner colour; bytes 2-3 colours and vegetation); u32 offset count + (u16 col, u16
  row, float3) each; u32 normal count + same. Brush tokens name `terrain_mat.<token>` units (910,
  each a `.mat` path and a colour); `inv` is invisible.
- **Terrain profiles** (`terrain.<token>`, 179 units): `step[16]` widths (usually 1 1 1 2 6 6 10 10
  10 20 40 50 50 50 100 100, total 457 m) and `height[16]`; the coefficient scales heights. Sizes
  in the map are partial sums of the steps (67, 107, 157, 207, 257, 357 m).
- **Terrain item** (type 1) body: start node, end node, two float3 offsets, f32 length, u32 seed, 3
  railings, per side (78 bytes: u16 size / 10, profile, f32 coefficient, a second token + float, 3
  vegetation records, u16 from/to), spheres, right then left quad data, four edge tokens. Flag byte
  1 bits 4-5 are the column step (1 = 16 m on every item measured).
- **3nK** obfuscation is used only by `base.cfg`; every definition file is plain text.

## Workflow

1. Open `Euro Truck Scene.unity` and press Play. The truck appears on the platform; in the editor a
   camera is created (right-drag orbits, wheel zooms). In Somnium the player's own rig is used.
2. `Somnium Euro Truck > Create model viewer` adds a viewer to any scene; set `Model Path`,
   `Variants` and `Look` in the inspector to look at anything in the archives.
3. Upload through the Uploader with scripting enabled and `SomniumEuroTruck` as the assembly.

## Next

1. Play the first job: Escape opens the job market once the map is read; take an offer, the
   truck is stood at the bay with the trailer, follow the green ribbon and stop in the far bay.
   Watch the coupling (trailer swing, pitch on crests), the add-on placement (slot names matched
   to locators by prefix), the steering wheel's tilt and turn direction, the chrome and paint
   under the reflection probe, the mirror log line, and the route ribbon's height.
2. Tune the physics from what the first drive shows: the named constants at the top of
   `EtsTruckController` and `EtsTrailer` (spring sag, damping, cornering stiffness, shift points).
3. Dashboard needles, stalks and wipers: the `.pma` animations and the skeleton are now read
   (`EtsAnimation`, `EtsSkeletonPose`), so the instrument bones (`speed`, `rpm`, `fuel`,
   `temperature`, `wiper*`) can be driven from the truck's state by skinning the dashboard mesh
   or moving the pieces each bone owns. Then traffic, buildings (`bld_scheme`), signs.
4. Test in Somnium: the seat (`EtsTruckSeat`) and camera override follow the Quake world's
   Bridge pattern but have only been compiled, not run in the client.

## Terrain coverage and the pale distance (2026-09-09)

Measured on the demo, against a screenshot of hard-edged ground with pale blue beyond it:

- **Fog was hiding the world.** `fog_color` is stored in a tiny high-dynamic-range unit (midday
  7.3e-6, 1.51e-5, 2.57e-5), so only the channel ratio carries meaning: a deep sky blue, roughly
  0.28 / 0.59 / 1.00. It was being mixed 60 % toward white, which turned every distance into one
  pale sheet. It is now kept at the game's blue.
- **Visibility was halved.** The game fades as `exp(-density x distance)`, 90 % gone at
  `2.3 / density` and only finished near `4.6 / density`. Unity's linear ramp was ending at the
  90 % point, so everything past 775 m was solid fog. It now ends at 1480 m, where the game's fog
  is genuinely complete.
- **The camera clipped before the fog did.** Unity's default far plane is 1000 m, so ground was
  cut away while still visible, and the bare sky behind it read as a hole in the terrain. Cameras
  now draw to `EtsClimate.Visibility`, the fog end plus 200 m.
- **A parser experiment that failed, recorded so it is not retried.** Item uids mostly ascend
  through a sector file and mostly share their top bits. Enforcing either as a hard rule in the
  header test looked attractive, because the walk otherwise invents ghost items whose node
  references point nowhere (1542 of 1657 terrain items around the centre). But it made things far
  worse: the map fell from 97.3 % of declared items found to 29 %, and one depot's surroundings
  from 416 real items to 5, because the walk then refused genuine headers. The kdop test remains
  the only gate, and the ghosts are harmless since they are dropped for having no node.
- **The map does not describe ground everywhere.** Rasterising every road side and terrain item from the
  data, the ground the files describe covers 71.8 % of a 1200 m square at the map centre but only
  28.5 % round the Tree-ET depot. The rest is genuinely absent. The game gets away with it because
  you only see the world from a cab about 2.5 m up, where the 100 to 350 m of terrain beside your
  own road hides everything past it and the fog closes in; from any height the gaps are open sky.
  `EtsGroundFill` fills them: a 40 m grid is ray-cast down onto the ground that really was built, so
  vertices over real terrain take its exact height and meet it seamlessly, vertices over a gap take a
  height smoothed from their neighbours, and only quads touching a gap are drawn. The map is
  `/map/europe`, 271 sectors, and it is the only one the demo ships - checked.
- **Solved: the streamer was keeping a 500 m bubble.** The run built 114 items where 417 to 1319 lay
  within 1200 m. The count itself was right all along: the scene had been saved with a stream radius
  of 500 m against a build radius of 1200 m, so the moment the truck moved it kept only what lay
  within 500 m and let the rest go. The new log line said it outright: "114 built of 70 within
  500 m". The scene is now 1200 m with a 2600 m unload ring, and `StreamRoutine` refuses a stream
  radius smaller than the build radius, since that unloads the ground from under the truck.

## Where to start on the demo map

The demo ships the whole base map: `/map/europe`, 271 sectors, from Calais in the west to Serbia in
the east. It is the only map in the archives. How much ground the files describe varies enormously
by place, and that decides how solid the world looks, so the world starts in a city rather than in
open country. Measured coverage of a 1200 m square:

| City | Centre, game coordinates | Ground described |
| --- | --- | --- |
| Stuttgart | -4637, 78, 10039 | 88 % |
| **Frankfurt am Main** (default) | **-5584, 65, 2497** | **84 %** |
| Kiel | 1137, 9, -21609 | 82 % |
| Linz | 13636, 66, 13905 | 79 % |
| Calais | -30340, 42, -5006 | 77 % |
| Erfurt | 2824, 45, -2033 | 74 % |
| Hannover | -2004, 9, -9085 | 71 % |
| Hamburg | -1690, 19, -16356 | 69 % |
| A rural depot (Tree-ET) | 1082, 5196 | 28 % |

Set `Center Game` on the map viewer to any of these to play there; the truck spawns on the road
nearest it and the job market picks the depots around it. `EtsGroundFill` covers whatever the files
leave out, so even the sparse places are solid underfoot, but a city still looks far better.

## The demo's one delivery, and its own menu

Checked against the installation at `E:\SteamLibrary\steamapps\common\Euro Truck Simulator 2 Demo`
(8 archives, `base.scs` 13.5 GB, all readable).

- **The one-delivery limit is not in the data.** 38,809 definition files under `/def` were walked and
  none uses the `available_in_demo` flag; `/def/demo_data.sii` and `/def/demo_truck.sii` are named by
  the executable but are not shipped. The archives carry the complete game: every city, every truck,
  the whole map. The limit lives in the executable, which is genuinely a demo build - its own build
  path is `final_build_ets2_demo_windows_bin_steam_x64`, and it carries `-demo`,
  `demo_gameplay_mode`, `screen.demo`, `demo_menu_hdl`, `demo_end_hdl` and `/ui/demo_mode_hud_*.sii`.
- **The screen it ends on is shipped**, and is now rebuilt from its own layout:
  `/ui/desktop_standalone_demo.sii`, eight units in the 1440 x 900 frame `/def/ui.sii` sets - a dark
  panel from 465 to 975 across and 138 to 893 up, a centred title, the game's logo from
  `/material/ui/logo/ets2_logo.mat`, a big centred line, the trial background
  `/material/ui/window/bcg_trial.mat`, and one button at 560 to 880. `EtsGameUi` reads any of these
  screens and places every element where the file puts it; `EtsDemoEndScreen` shows this one.
- **The run is one delivery.** `EtsJobs.DemoRun` (on by default) stops the job market after the first
  load is delivered and raises the end screen. Its button carries on driving rather than opening a
  shop, and switches `DemoRun` off so the market returns.
- **The wording is the one thing that could not be read.** Locale files are 3nK-obfuscated
  (`/locale/en_gb/local.sii` begins `3nK`) and the scrambling is not a byte cipher - additive keys,
  rolling and multiplicative keys, repeating keys and raw deflate were all tried and none recovers
  it. The keys are in the layout (`@@demo_ended@@`, `@@standalone_demo_complete@@`,
  `@@go_to_store_page@@`), so the English is written against the key names in `EtsGameUi.Words` and
  marked there as mine. Everything else on the screen is the game's own.
- **The menu** now uses the game's frame and its real logo rather than a layout of my own.

## The game's own front end

Every screen in the game is a layout under `/ui`, and they are all readable, so they are rebuilt
rather than redesigned. `EtsGameUi` places any of them: each element carries a rectangle in the
1440 x 900 frame `/def/ui.sii` sets, a look template naming its style, and either a locale key or
markup naming an image from the archives.

| Screen | File | Built |
| --- | --- | --- |
| Launchpad | `/ui/launchpad.sii`, 61 elements | Yes, with the game's logo |
| Loading screen | `/ui/loading.sii`, 16 elements | Yes, with the game's photographs |
| Demo ended | `/ui/desktop_standalone_demo.sii`, 8 elements | Yes |
| Headquarters | `/ui/desktop.sii` + a garage scene | No |
| Truck configuration | `/ui/truck_configuration.sii`, 54 elements | No |
| Job market | `/ui/quick_job.sii`, 80 elements | Data yes, screen no |
| World map | `/ui/world_map.sii`, 32 elements | No |
| Driving HUD and sat nav | `/ui/hud_*.sii`, `/ui/gps.sii` | No |

- **The loading photographs are the game's own.** `/material/ui/loading/bar_pics` holds 75 of them,
  all DXT1 1920 x 680 and all resolving, each beside a `.sii` naming the place and the DLC it
  belongs to. The caption is recovered from the location key, since the locale is obfuscated but the
  keys read plainly: `ls_d941_le_puy_notre_dame_france`. The progress bar is the one the layout
  places at 464 to 976 across, filled from `EtsMapViewer.LoadProgress`.
- **Audio remains blocked.** `/sound` is entirely FMOD `.bank` files, including `/sound/music`. Those
  are FSB5 codec 15, Vorbis without setup headers, which cannot be decoded without FMOD's own
  tables. The engine, tyres, wind, horn and reversing alarm stay synthesised in `EtsTruckAudio`.

## Surfaces: what the files actually say (2026-09-14)

Three things about textures were being guessed. All three are written down in the game's own files.

- **A terrain material's `aux[0]` is `{ u_size, v_size, rotation }`** — metres per texture repeat, and
  a rotation in degrees. 579 of the 583 terrain materials carry it, from 1 m (kerb detail) to 120 m
  (backdrop hills); asphalt is 8, most fields 10 to 13, and the rotations are 0, 45, 90 and 135
  (`barley_field_horiz` is simply `barley_field` at 90). `EtsTerrainBuilder` used to draw every
  surface at a flat 16 m, which is why fields looked coarse and roads looked stretched. Each brush
  material now gets its own UVs at its own size, rotated as asked.

- **`truckpaint` reads its atlas through uv set 0, not set 2.** The test that settles which of a
  model's uv sets is the real unwrap is *area*: an unwrap lays each triangle down exactly once, so
  the area its triangles cover in uv space comes out near 1, while a tiling set covers the square
  thousands of times. Summed over the DAF's 58 m² of painted panels, set 0 measures 1.08, set 1
  0.38, set 2 0.73 — and decisively, every painted piece's set 2 runs from u 0.01 to about u 0.9 and
  stops at v 0.59, all of them stacked on top of one another. That is the layout of the paint *job*
  mask, the thing a livery is painted into. Set 0 gives each piece its own rectangle of the atlas.
  The cab — the biggest surface on the truck — had been sampling its ambient-occlusion atlas through
  the mask's coordinates. The same measurement shows tsnmapuv's 63 m² of plastic at 3665 on set 0
  (the small tiling texture) and 0.92 on set 1 (a unique detail map), so only that effect has to go
  without its normal map; painted bodywork keeps its.

- **On `eut2.dif.spec.add.env.*` the base texture's alpha is a reflection mask, not transparency.**
  Dump the alpha plane and it is a different picture from the colour. `chrome_c` is one grey atlas of
  grilles, steps, mud flaps and light housings; its alpha is about 0.28 over the plastic and near 1.0
  over the grilles, the steps and the polished strips. `metalic2`'s alpha is black across the whole
  chassis with two bright patches on the aluminium tank. Both materials are opaque, so none of that
  is see-through — it is how much of the surroundings each texel reflects. One metallic and one
  smoothness for a whole atlas therefore had to be wrong nearly everywhere: either the plastic came
  out as dark mirror or the chrome came out as grey paint. `EtsTextureCache.LoadReflectionMask`
  unpacks that alpha (DXT5 alpha is two endpoints and sixteen three-bit indices per block; raw
  BGRA/RGBA is read directly) from the first mip 256 px or smaller, and hands back an RGBA32 map with
  red = alpha × metallic and alpha = the mask — which is exactly what URP's Lit reads from
  `_MetallicGlossMap`. It is skipped where the format has no alpha, where the mask is flat, where
  alpha genuinely is transparency (glass, cutout, ground decals) and for `lamp`, whose alpha is the
  lit part of a lens.

## The menus are files too

`look_template` is not decoration — it names an entry in `/ui/template/text.sii`, `button.sii` or
`frame.sii`, and those entries are markup that says exactly how the game draws the thing.

- The launchpad's background is `txt.window.dyn_bcg.nl`: fifteen `<img>` tags cutting
  `/material/ui/window/bcg2.mat` up by pixel (`left=p54 top=p78 right=p70`, `right=p256`) and
  stretching the pieces into a frame. The file is a 256×256 BGRA image holding one rounded
  **top-left corner** — a hairline light border round a near-black panel (mean colour 18,18,18) that
  is only about half opaque, with 45% of it fully clear — which the game mirrors into the other
  three. `EtsGameUi.WindowPlate` does the same: a sliced sprite (rect x 54..256, y 0..178 in Unity's
  bottom-up space; border left 16, top 22) drawn four times with mirrored scale.
- `btn.normal` plates every button with `/material/ui/button/btn_dark.mat`, an 8×32 dark vertical
  gradient, and lays `btn_normal_focus` or `btn_normal_press` over it when the button is pointed at
  or pressed, with the caption in `/font/big.font`. `MakeButton` now uses that strip.
- All of the UI chrome is uncompressed BGRA, so harness `Program100` is `Program86` plus a raw
  RGBA/BGRA path — `Program86` alone reports "format not decoded here" on these.

Still missing on the launchpad, and each is its own job: the six button icons, the blurred photograph
behind the window, the profile dropdown, and the game's font (`/font/*.font` is a bitmap font, which
TextMeshPro cannot take as-is).

## The launchpad is gone, and why

Rebuilding the game's front-end screens from `/ui/*.sii` works for the loading screen and does not
work for anything else. The layouts give positions and look-template names; the screens you actually
remember are drawn over a bitmap font (`/font/*.font`, which TextMeshPro cannot take), icon atlases
and a photographic background, none of which come out of the layout. And the launchpad's buttons open
screens this world does not have, so the best possible result was a dead menu standing between the
player and the truck. Three attempts, three times "this looks nothing like the original".

Age of Mythology hit the same wall and the answer there was `AomUiPanel`: stop reproducing the
original's chrome and put a plain Unity Canvas up, with a GraphicRaycaster and a camera assigned, so
the Somnium client's own pointer drives it. `EtsMenu` was already exactly that, and it already held
the whole job loop.

So the front end is now: the game's loading screen (its own layout over its own photograph) and then
`EtsMenu`, open, with the job board on it. `EtsGameUi.WindowPlate` draws a flat plate in the panel
colour measured off `bcg2` rather than a mirrored nine-slice — the nine-slice put grey seams down the
middle of every panel and showed the corner texture's transparent-but-white margin as a white box
behind the logo. Buttons keep the real `btn_dark` strip, which was the part that did look right.

Two things were also stopping a job being finished rather than started:

- `EtsJobs.MakeOffers` ran **once**, at the end of the map index, and returned silently with nothing
  if the truck was not on the ground yet — leaving a permanently empty board. `EtsMenu` now asks
  again every two seconds while the board is open and empty.
- `EtsJobs.StartJob` gave the depot's yard 15 seconds to stream in and made a single attempt to set
  the truck down. A miss dropped the job and left the player idle. It now waits 40 seconds and tries
  twelve times, and says what it is waiting for.

## Only the first texture was being drawn (2026-09-14)

The world looked wrong — a white city, featureless grey buildings, pale patches over the fields — and
it was not the textures failing to load. Over the nine sectors around Frankfurt: 7496 models, 1788
distinct materials, and **1784 of those 1788 resolve all the way to a DDS with nothing broken**. The
four that do not are `shadowonly`, two `none`, and `water`.

The fault was that the game's materials carry **several** textures and only the first was read.

| slot | materials | |
|---|---|---|
| `texture_base` | 1784 | the only one being drawn |
| `texture_nmap` | 542 | normal map |
| `texture_mult` | 537 | **a second colour layer, multiplied on** |
| `texture_reflection` | 514 | environment map |
| `texture_iamod` | 218 | third layer, inverse-alpha blended |
| `texture_over` | 143 | the `amod` chain's second layer |
| `texture_mask` | 113 | |

**537 materials — 30% of everything in view — have a second colour layer, and all 537 of those
overlay textures resolve.** For a building, `texture_base` is a small tiled concrete or brick and
*all* of its character — the weathering, the streaking, the window bands — is in the layer that was
being thrown away. That is why the city was white blocks.

Which UV set each layer uses was measured, not guessed. On
`/model2/building/administration/admin_13.pmd`, whose walls are
`eut2.dif.spec.mult.dif.spec.iamod.dif.spec.tsnmapuv`, the three UV sets cover their textures 4.63,
0.99 and 0.68 times: set 0 tiles the base, set 1 is a true unwrap for the layer above it. The effect
name is a chain read left to right — `dif.spec`, then `mult.dif.spec`, then `iamod.dif.spec` — so
layer *i* uses UV set *i*. And `texture_mult` here is `concrete_over_01`, a 512×4096 atlas of grime
and streak bands, which is exactly what an unwrap into a strip wants. `EtsModelBuilder` had always
been writing UV set 1 into `mesh.uv2`, so nothing in the mesh path needed changing.

One naming trap: the `amod` chain stores its layer in **`texture_over`**, not `texture_amod`. All 90
`eut2.dif.spec.amod.*` materials near Frankfurt use that slot; the only other users are the `weight`
effects (terrain blending, driven by vertex weights rather than a second UV) and `baked` (pre-lit),
both of which are left alone. Note also that `"iamod"` contains `"amod"`, so it must be tested first.

`Shaders/EtsMulti.shader` ("SomniumEuroTruck/Multi") is the result: a URP shader with forward,
shadow-caster and depth passes, sampling base through uv0 and the second layer through uv1, joined by
`_LayerMode` — 0 multiplies, 1 lays it over by its own alpha, 2 by the inverse. At `_MultStrength` 0
the second layer is white and the result is identical to the old single-texture path, so a material
without one is unaffected. It is referenced from the scene (`EtsMapViewer._multiShader`) so the world
bundle ships it, with a `Shader.Find` fallback, exactly as the decal shader is.

### Water was the same mistake

`eut2.water` keeps its two wave textures in `texture_layer0` and `texture_layer1`, so `BaseTexture`
came back null and the surface fell back to the material's `diffuse`, which is `{1,1,1}`. Every lake,
river and flooded pit was drawn as a flat **white** sheet — the pale blue-grey patches scattered over
the fields. The colour is written down right beside it: `water_near_color` and `water_horizon_color`,
both `{0.030, 0.074, 0.133}`, a deep blue. Water now uses `texture_layer0` as its base map, that
colour as its tint, smoothness 0.9 and no metal.

## The spawn, and terrain being two brushes deep (2026-09-14)

**The grey yard was not a bug.** The truck was spawning at "the nearest road node to the map centre,
plus 20 m", which at Frankfurt is a service road in a paved industrial estate. Checked against the
files, the terrain brushes at that exact spot are `ibe_39` → `sidewalk_03_ibe`, `blke_8` →
`asphalt_parklot_var_1`, `share_29` → `asphalt_02`, `151` → `asphalt_tiles_eu_01` and `dlc_fr_71` →
`soil_gravel_fr`. Sidewalk, car park and gravel — grey is what they are.

Two things were ruled out with evidence and should not be chased again:

- Terrain brushes are **not** failing to resolve. All 11,803 brush slots in the sectors round
  Frankfurt point at a real `.mat` — 0% missing — so none of the grey is the debug fallback colour.
- A `dlc_` prefix does **not** mean missing. `dlc_balt_54` resolves to
  `/material/terrain/balt/grass_sparse_balt.mat`, which the demo ships. Only DLC *model* descriptors
  resolve to `/`.

So the spawn now prefers the nearest **company spawn point** within 4 km of the centre — the same
depots the job board offers loads from — and falls back to the old road search only if there is none.
It is deterministic, and the truck starts where a job starts.

### Terrain quads carry two brushes, and a tint

The same shape of mistake as the multi-texture materials, and almost exactly the same 30%.

Every terrain quad stores a **main brush, a blend brush and a blend opacity** — all three already
parsed into `EtsTerrainQuadData`, and the builder used only the first. Measured over the sectors
round Frankfurt: **385,392 quads, of which 117,821 (30.6%) carry a second brush**, with the opacity
spread right across the range rather than clustered at the ends:

```
opacity 0..15:  267567  7920  6354  7527  7795  34253  269  3770
                  6638   247 24689  3275  7869  3686 3533    0
```

That is why the ground read as hard-edged slabs of a single material. A faithful splat needs a shader
with the mask in it; short of that, a quad more than half covered by its blend brush is now drawn as
that brush, which puts the right material down far more often than always taking the main one.

Beside every brush the sector also stores a **colour**, and **28.6% of them are not white**: warm
cream `255,242,219`, greys from `216` down to `164`, pale green `215,227,212`, a yellow
`255,250,148`. That is how one grass or asphalt texture becomes a dozen different-looking fields, and
it was being dropped. It now multiplies into `_BaseColor`, cached per brush and colour quantised to
32 steps a channel.

Still outstanding: a real two-brush splat shader, and the per-quad `MaterialRotations`.

## How the game stands a truck somewhere (2026-09-14)

I got this wrong twice by assuming, so it is written down.

Euro Truck Simulator does **not** put a vehicle on a map node. `/def/initial_save/normal/game.sii`
holds money, time and progress but no position at all, and the preview profile's `player.sii` is a
truck configuration — colour, chassis, cabin, engine, accessories — with no placement either. The
position comes from the map, and specifically from a **prefab spawn point**.

Every `.ppd` carries a table of them. The record is **32 bytes** — `float3 position`,
`float4 rotation`, `u32 type` — laid out from the offset at **byte 64** of the descriptor. That was
verified rather than assumed: across the 23 prefab descriptors around Frankfurt the stride comes out
at exactly 32 every time, and the types that come back are the ones the game's own editor names.
*(Superseded for 1.61: the record is 36 bytes from descriptor version 24, and the rotation was always
stored w first - read x y z w, most points came out turned 180 degrees. See "Game 1.61".)*

| type | meaning | count near Frankfurt |
|---|---|---|
| 1 | trailer position | 37 |
| 3 | gas pump | 23 |
| 19 / 20 | unload medium / hard | 15 / 15 |
| 2 | unload easy | 13 |
| **13** | **company — where a truck parks at a depot** | 12 |
| **10** | **parking bay** | 11 |
| **14** | **garage** | 2 |

Crucially their **y is 0.0** in the prefab's own frame: they sit *on* the prefab's paved surface. Put
through the prefab's own transform — the same one `BuildPrefab` uses, rotate by the yaw that turns
the anchor node's direction onto the map node's, then offset so the anchor lands on the map node —
they land on ground that exists. `EtsMapBuilder.TryPrefabSpawn` does exactly that.
*(Superseded in Phase 1, 2026-09-22: the transform now also takes map node 0's pitch and roll
(`EtsMapBuilder.TryPrefabFrame`), so on a sloping yard the points sit on the tilted surface. See "Phase 1".)*

The previous attempt used `company.SpawnPoints[0]`, which is a map **node** uid, and assumed a truck
could stand on it. It could not, and the truck fell out of the world.

Measured near the Frankfurt start: 690 prefabs, 31 with spawn points, **21 usable truck spawns**, the
nearest a company bay 400 m from the origin — inside the 1200 m initial build radius.

**And nothing is placed until there is ground under it.** The world streams in around the truck, so
a chosen yard may not be built yet. The spawn now raycasts down at the spot and only sets the truck
there once something solid answers; if the ground never arrives it leaves the truck where it already
stood rather than dropping it through the map.

## The front end, from the game's own files (2026-09-15, overnight build)

Everything on the menu side is now drawn from the player's installation rather than designed here.

**Fonts.** `/font/normal.font`, `big.font`, `title.font` and `small.font` are distance-field fonts —
the same technology TextMeshPro renders with — described by a text file over a set of atlases.
Every number on a glyph line is in the pixels of its atlas and the atlas's `scale` turns them into
reference pixels; that was verified rather than assumed (A's bottom lands at exactly the header's
"max pixels above the line"). The atlas is a multi-channel field (`ui.font.msdf`), so the median of
the three channels is taken per pixel and uploaded as an alpha texture; glyph rectangles are grown
by the field's padding, which was checked to overlap nothing. `EtsFontFile` parses, `EtsFont` builds
the `TMP_FontAsset` at runtime, kerning pairs included. Three of the four faces share one atlas.

**Chrome.** A window in the game is a recipe in `/ui/template/text.sii` that cuts pieces out of a
small atlas: for `txt.window.dyn_bcg.nl`, across each band, the corner column of `bcg2` (pixels 54
to 70), a column at 70 stretched, the body 70 to 256, the body mirrored, the column again, the corner
mirrored. For the launchpad's 824-pixel window that stretch comes out at exactly the 210 the layout
passes in. `EtsUiPainter` composites these on the CPU — windows, button strips, icon halves, texture
regions — and its output was written to PNG and looked at before Unity drew it. `EtsPixels` is the
shared DXT/BGRA decoder both the painter and the font loader use.

**The garage.** `/ui/desktop_background.sii` puts a `ui_truck_scene_base` behind the menus whose
config, `/def/desktop/config.sii`, names `/model/ui/garage/desktop_garage.pmd`, the truck at the
origin, a camera at (0,3,0) aimed at (0,3,1) at 45°, the lights, and a cinematic path in
`/def/camera/ui/desktop_garage_cam.sii` (five cameras with positions a quarter-second apart).
`EtsGarageScene` builds it 2.5 km below the map with a stripped copy of the built truck in it.

**The flow**, in the game's order (`EtsFrontEnd`): the launchpad over the garage (`/ui/launchpad.sii`:
window, title, the Continue Game plate `btn.launcher.continue.game` draws with the logo from
`/material/ui/launcher`, the profile row, six `btn.screen_link` icons from `/material/ui/link`,
two news boxes) → the loading screen → the desktop (garage, top strip, the button bar
`/ui/desktop_button_bar.sii` lays across the foot) → the job market (`/ui/quick_job.sii`: the list of
offers with company logos from `/material/ui/company/small`, the details panel, Take job) → the
loading screen with the job line → the drive → the delivery summary (`/ui/delivery.sii` with the
run's distance, time, fuel and pay) → the demo's end screen.

**The route advisor** (`EtsRouteAdvisor`) is the sat nav: `/ui/adviser_map.sii` puts the panel at
1069..1419 × 83..288, the map in 1069..1419 × 83..266, the title top-left, the F2 zoom key top-right,
and the route notes across the middle; `/ui/adviser_job.sii` is the job page (F1). The map is the
world from a camera 400 m above the truck, turned to its heading, drawn into a texture — roads,
yards and buildings read as a map at that scale and the route line appears on it for free. The next
turn is found by walking the route polyline for a bend over 35°.

**Bays.** A job now collects from and delivers to the prefab spawn points the game marks: the truck
parks on a company point (type 13) and the trailer is dropped on an unloading point (2, 19 or 20),
both at height zero on the prefab's own yard and put through the prefab's transform. The unloading
bay is marked with a translucent slab and a beam; delivery triggers when the trailer comes to rest
in it.

**Terrain.** Quads are grouped by the *pair* of brushes on them. Pairs with a second brush are drawn
with the two-layer shader's fourth mode — the blend's texture on the second uv set, faded by the
per-quad cover averaged onto the shared vertices into the vertex alpha — so one brush now fades
into the next as painted instead of stepping. Quads with no blend group by their main brush as
before, tinted by the brush colour the sector stores.

## The holes were the yard (2026-09-15)

The pale cyan polygons at the DomDepo bay were not terrain at all. Listing everything within 220 m
of the bay from the files (harness Program120-124) showed all 24 terrain brushes resolving to
dark or grey textures and every prefab and model material carrying a real base texture. What was
missing was in the model builder: since the first junction was built, every prefab piece whose
effect contains `weight.weight` had been treated as a "terrain-blend skirt" and kept only as a
collider. In `/prefab2/building_mat_store/building_mat_store_balt.pmd` those pieces are the whole
yard - 141 x 116 m of `grass_city1_pref_balt` over `soil_gravel_fr` and `asphalt_parklot_base`
over its `var_1` and `var_3`. A collider with nothing drawn on it is a hole, and through it shows
the camera background, which is the scene's fog colour (0.843, 0.945, 0.957) - that cyan exactly.

The effect `eut2.dif.spec.weight.weight.dif.spec.weight` (also with `.tg0.tg1`) is two textures,
`texture_base` and `texture_over`, mixed by the vertex colour's alpha, alpha 1 = over. That is
SCS's own definition: their public Blender tools carry a shader per effect, and this one wires
"Vertex Color Alpha" into a MIX node's factor with base as colour 1 and over as colour 2, and
deletes the opacity node - the surface is opaque. (A first reading from the yard's rim alphas
had it the other way round; the tool is the authority.) The single `dif.spec.weight` effect
uses the vertex colour only to weight specular - no transparency. These materials are the
two-layer shader's mode 4: the same mix as the terrain pairs, drawn one queue step after plain
geometry and pulled one depth unit toward the camera (`_DepthBias`), because a prefab's pieces
meet edge to edge in one plane and their differing vertices round to different depths along the
seams. Measured on the DomDepo yard and the tram crossing: the "shared" cells between pieces are
half-metre boundary strips, not stacked areas, and the crossing's `weight.weight` piece is the
other half of the junction rather than a copy of its slab. The vertex colour's rgb is a grey
0..128 with 128 meaning x1 - a baked darkening the game multiplies in on every `dif.spec`
material - and is not yet applied to models.

The same listing found the trees. Of the 7,496 "models" the map places in the nine sectors round
Frankfurt, 1,413 have no `model_desc`: their unit reads `vegetation_model = vegetmodel.<name>`,
and the vegetmodel unit names `hi_poly_model`, `med_poly_model` and `sprite_model`. They had all
failed as "descriptor not found: /" - 181 of them near the bay - and the `dlc_`/`rus_`/`blke_`
prefixes on their names had nothing to do with missing content. `EtsMapBuilder.BuildModel` now
resolves them through `EtsVegetationBuilder.Model`.

**F10** logs what is under the mouse (`EtsProbe.UnderCursor`): the object's path, whether it has a
renderer at all, its material, shader, bound textures with formats, colour, smoothness, metallic,
layer mode, queue and keywords, and the mesh's counts - or "nothing with a collider" when the
cursor is on sky. Point it at anything that looks wrong before theorising.

## Divided roads, the cab's mask, and the doubled mirrors (2026-09-15, afternoon)

The strip of sky beside the truck at the bay was a **half-built road**. Road look `ger34`
("ger r2 tram") names `template_right` and `template_left` — the same one-sided slab,
`/road_template/ger/ger_r2_tram.pmd`, modelled on x 0..9.5 — with `road_offset` 3.25: the game
stands the right copy 3.25 m right of the centre line and the left copy 3.25 m left of it,
mirrored. Both were being built at zero, unmirrored, so the left carriageway did not exist, and
the map paints the terrain beside such roads invisible (48-61 of 72 quads on this one) because
the road is meant to cover it. `BuildRoad` now offsets the right template, mirrors the left one
at minus the offset (using the right template again when a look has an offset but names no
left one), hangs the edges at offset plus the slab's width, and lays a **median** between the
carriageways in the road's centre brush: the sector body carries one terrain-material token per
road at offset +116 (`terrain_mat.0`, dirt, here), read into `EtsMapRoad.CenterMaterial`. The
`vis_tran` and edge `tran_*` pieces stay skipped: their base texture is `ter_trans_placeholder`,
which the game replaces at runtime with whatever terrain lies beside the road.

The pale oval over the instruments was interior `model.pmd` piece 3, effect `eut2.mlaaweight` —
an empty material with no texture, the game's anti-aliasing weight mask over the cluster —
built as a white plate. It is now skipped like `none` and `shadowonly`. The cluster's real glass
(piece 26) is `eut2.glass` at opacity 0. The same listing shows the cab **has** display pieces:
piece 19 (`dashboard.tobj`, over the cluster) and piece 18 (`gps.tobj`), both `eut2.dif.lum`,
with the interior definition naming `dashboard_path /ui/dashboard/daf_xf_euro6.sii` and
`gps_path /ui/gps.sii` — an in-dash sat nav can be drawn into piece 18 later.

The doubled mirrors: an accessory with both `exterior_model` and `interior_model` is shown one
way or the other by the game — the interior model in the cab view, the exterior one outside —
never both. `EtsTruckAssembly` now remembers what it hung (`ExteriorRoots`, `InteriorRoots`) and
`ShowFromInside` swaps the pairs; the viewer applies it whenever `EtsTruckCamera.View` changes.

The mirror pictures were upside down for a reason already written down elsewhere in this file:
the model builder turns every v over because the game's DDS textures are stored top row first,
and a Unity render texture is not, so on the glass mesh the camera's picture arrived inverted.
`EtsMirrors` now flips the texture on both axes (mirror image, and the v back again).

## The left carriageway, and the prefab terrain read wrongly (2026-09-15, evening)

Two more from the files. The lane text "BUS" read "SUB" on the left carriageway of the tram
road: a mirror image. The game does not mirror the left template, it turns it round — that
carriageway carries its traffic the other way and its markings face that way. `BentSlice` now
gives the left copy a half turn (x and z reversed within the slice, normals with them, winding
kept) instead of a mirror, for slabs and shoulders alike.

The prefab terrain had been built from a misread descriptor. TruckLib.Models (the community's
reference decoder) reads the `.ppd` terrain points as one array of positions followed by one
array of normals, with a variant table of (start, length) pairs after them and, per node, the
run of points plus that node's range in the variant table - one border per model variant. The
parser here had taken six floats per point, interleaving positions and normals, so every border
was scrambled: strips grew from nonsense, overlapping the road terrain (the flicker) and leaving
gaps (holes). The map's six "corner" terrains belong to the descriptor's nodes in order, and the
map stores exactly points - 1 columns for each. Checked after the fix on 141 prefabs: 236 of 358
node terrains match the map's columns exactly, the rest have coarser map grids and are sampled
proportionally. `EtsPrefabTerrainBuilder` now grows each node's terrain from that node's border
with the corner's own size, profile and brushes, tiled at the brush's size; the earlier scheme
of borrowing the connected road's terrain is gone.

Still to apply from the same source: the terrain quad's other nibbles are colour indices per
**quad corner** into the brush colour palette (not one colour per brush), which means the ground
tint should be a vertex colour.

## The road body and its payload, by the reference decoder (2026-09-15, night)

TruckLib's `RoadSerializer` settles the road item field by field, and two of my readings were
wrong. The body carries **four** shoulder tokens - the right carriageway's right and left edge
(+44, +52) and the left carriageway's right and left edge (+60, +68). A single-carriageway road
is all "right carriageway" (every one near the bay leaves the left-carriageway pair empty), so
its left shoulder is the right carriageway's left edge, as the builder already had it; a divided
road's inner edges face the median, and on the tram road both are `ger_track_sh` - the tram
lines run in the middle of the avenue, one on each inner edge. `BuildRoad` now hangs them at
plus and minus the road offset and takes the outer left shoulder from the left carriageway's
own left edge. The token at +116 is the *legacy* material of pre-template roads (`0` on every
template road here), not the centre brush: the centre material, its colour and uv rotation sit
in the `.data` payload straight after the left side's quad grid. Read from there, the tram
road's median is `asphalt_tiles_eu_01` (it was drawn as dirt from the wrong slot).

Skipped from now on: every `*.night` effect. `eut2.lightmap.night` is the pool a street lamp
throws on the ground and walls (664 pieces within 1.2 km of the bay, `decal_light_01`) and the
`building.*.night` effects are lit windows; the game adds them after dark.

Ruled out, with data, as the cause of the large yellow sheets in the free-camera screenshot:
`ter_trans_placeholder` (it decodes to a dark green, 60,89,30), every terrain brush near the
census centre (all dark or grey), the brush colour palettes (all pale) and the night pools (dark).
The census now names the surfaces that cover the most ground - material, shader, base and over
textures, colour, layer mode, emission - with a sample position each, and a sample position for
every co-planar pair, so the next log names the yellow rather than the next guess.

**F11 / auto census.** `EtsProbe.GroundCensus` ray-casts a 2 m grid 120 m round the truck
through everything solid and logs how many cells have no ground at all (holes, with the first
sixteen world positions) and every pair of surfaces whose top hits lie within 3 cm of each other
(the ones that flicker), by object and material name with a count. It runs by itself 75 s into
a session and on F11, so a log always carries one; read that block before touching the ground
code again.

## The vertex records were positions, not nudges (2026-09-16)

The flashing ground and the holes had one cause, and it was not the tangents, the sides, the
widths or the census misnaming things: a tangent audit of all 212 roads and terrain items within
600 m of the bay found every item at its start node's forward and its end node's backward link,
no Hermite overrunning its chord by more than a metre, and every alternative sign convention far
worse. What an offline replica of `BuildSide` did reproduce - by name, rank and position - was the
runtime census: the top pair at world (822,-39), the pairs at the bay, the empty band at (745,62).
The terrain quad data's per-vertex records (`u16 column, u16 row, float3`), which the parser
called "hand-moved vertices" and `BuildSide` ADDED to the grid vertex, are the vertex's POSITION
relative to the item's backward (start) node in the game's frame - TruckLib, `TerrainQuadData`:
"Offsets of terrain vertices. Positions are relative to the position of the backward node."
Measured on 1755 records within 400 m of the bay: read as deltas the median vertex moved 58 m
(max 286 m; 2771 of 2844 records moved a vertex more than 5 m), strips folded over themselves
(1203 same-item overlaps) and over their neighbours; read as positions the median move is 2.4 m,
co-planar cells at the bay fall from 1276 to 69, same-item overlaps to 14, and the hole cluster
beside the rail is covered by the rail's hand-shaped 4 m left strip (1109 of 1264 cells). The
records on one side grow linearly with the column index, a position pattern, not nudges.
`BuildSide` now sets `vertex = origin + ToUnity(record)` with `origin` the start node's world
position (for terrain items the bare node, not node + offset; every offset measured is zero),
and applies records by index whatever grid was built, since a position is right by index.

**Prefab corners carry them too**, on 25 of 27 corner sides near the bay, relative to the
prefab's OWN map node: the first in its node list, whichever descriptor node the origin index
names (TruckLib, `Prefab.Nodes`: "The origin node will always be the first entry in this list";
`Origin` says which ppd node that is). Corner data stays indexed by descriptor node k (the stored
grids match only that pairing). Tested against every node of each prefab and the prefab-local
frame: the first map node fits at a median 2.5 m on the 49 corners whose default grid has the
stored shape; every other node lands 10-60 m off. Two harness runs said otherwise first, both
because the harness itself paired the nodes wrongly - the rule was settled only once TruckLib's
prefab node semantics were read.

**And the stored column count is the mapper's.** The same descriptor corner is stored with 2
columns in one placement and 4 in another at much the same size; a 14-point arc with 4; a
6-point straight run with 1; a 4-point border with 5. No rule on the border (duplicate drop at
0.05-3 m, collinearity) reproduces it. The game spaces the stored count evenly along the border:
resampled so, the records on the 43 disagreeing corners fit at a median 3.3 m (raw border 6.0 m),
the same as the agreeing ones, and records beyond the built grid halve. `EtsPrefabTerrainBuilder`
now re-cuts the border into `quads.Cols` equal columns when they differ, then applies the records.

**Census timing.** The auto census now fires 10 s after the first build completes (not 75 s
into the session, which short runs never reached) and again once the truck is 150 m clear.

Harness: Program140 (tangents), 144-146 (offline census replica, delta vs position), 147-149
(prefab records, frames, column counts). Left open: a constant 2.7 m outward residual on one
tram road's shaped row (a mapper's row drag or a shoulder width - decompose per side), the
`Normals` record list (TruckLib: "what is this??", zero rows here), records whose row is beyond
the stored grid (stale from a larger size; dropped).

## The textures were upside down because v was turned over (2026-09-16, afternoon)

The user was right that "some things" hung upside down: every poster, sign and dial. The model
builder flipped v on every uv set "because the game counts rows from the top", and so did the
road slices. That double-counted. A DDS does store its top row first, but Direct3D puts that
first row at v = 0 and Unity's raw upload (`LoadRawTextureData`) puts it at v = 0 as well, so
the game's own v reads the same row in both; only a picture drawn on a plain screen quad, whose
v = 0 is its bottom, needs turning - which the UI does, and still does. Measured rather than
argued: over the 212 models within 500 m of the bay, 1380 upright pieces with a v range, v shrinks
with height on 593 and grows on 165; on the advert boards (`/model2/props/street/advert/*`,
textures with writing, decoded with the first row at the top and read upright) the correlation
of height with v is -1.0 - v = 0 at the top of the board. `EtsModelBuilder.ToUv` and the road
slice uvs no longer flip. Textures on tiling walls and ground never showed it, which is why it
went unnoticed until the posters did.

The mirrors told the same story from the other side: their glass is mapped with v = 1 at the
top (the game's mirror pictures are stored bottom row first, unlike its DDS files), which is a
Unity render texture's own order, so with the flip gone the picture stands up by itself and
`EtsMirrors` now flips the render texture left to right only. Harness Program150 (the vote),
Program151 (per texture, and the PNGs).

**Census.** The first auto census after the vertex-record fix, at the bay: 3355 co-planar cells,
but every pair in the top fourteen is now the game's own decal lying on the game's own ground
(`decal_ground_01` over prefab pieces and terrain, drawn by the decal shader with `ZWrite Off`
and `Offset -1,-1`, so it does not fight) or a collision mesh under its visual mesh (nothing
drawn). No road terrain against road terrain is left. Holes 885 of 14400 cells (6.1 %); the
first-sixteen list was useless (all on the window's west edge), so the census now groups holes
into connected patches and prints the ten largest with centre, extent, whether they touch the
window's edge and the ground beside them.

## The blue sheet under the parked cars (2026-09-16, evening)

With the strips fixed and the plates readable, a pale blue rectangle stayed under a row of parked
cars. Traced through the files: those cars are `cars_parked_5x1` groups at world (500-570, 49,
-270..-320) - a lot 65 m above the bay - on a terrain item's 86 m right strip painted with brushes
151 (asphalt_tiles_eu_01) and dlc_balt_20; the offline replica with the vertex records applied
covers every sample point under them at y 49.0, so the ground is there. The group's placed
variant is `no_lines`, and the pmd's variant table hides its `vis_lines` decal quad in it; its
`coll` box is `eut2.none.rfx`, which the cache skips, and neither of its textures (lines_eu_01,
decal_ground_01) has a blue texel in it. What does turn blue is a smooth dielectric at a grazing
angle: a `dif.spec` yard piece with shininess 65, specular 1 and `reflection : 0` gets smoothness
0.58 from `SurfaceOf`, and URP's Lit then reflects the skybox off it, where the game's own shader
puts only a Blinn-Phong highlight of the sun. Environment reflection is now switched off
(`_EnvironmentReflections` 0, `_ENVIRONMENTREFLECTIONS_OFF`) on every material whose effect has
no environment term (add.env, glass, truckpaint, lamp, mirror, chrome); the highlight keeps its
smoothness. The earlier clamp for "weight" ground stays.

The census now also reports, per patch, the cells where a collider exists but nothing opaque is
drawn within 3 m under the top hit (a collision box, a transparent decal over nothing - the sky
shows through), names the shine of any surface with smoothness or metallic above 0.3, and marks
transparent queues, so the next log settles which of the two the blue was. Harness Program152-156
(the prop's materials, texture decodes with alpha, brushes near the bay, material dumps).

## The profile's last step repeats, bezier patches, and what the near view is missing (2026-09-16, night)

**Holes.** An offline hole finder (harness Program161, written by a probe that then died on the
usage limit) rasterised every ground source round the bay - roads and terrain items with the
vertex records applied, prefab corner terrains, prefab and model ground pieces - and reproduced
the runtime census: 784 collider-free cells (the census's 885), the ground fill covering 3 of them
(it fills only 25 m quads with no ground at any corner, so it never reaches a gap beside real
terrain). Its three largest patches - 231 cells (46 x 52 m) at (380,-222), 133 at (326,-224),
331 at (218,-289) - each sat beside a terrain item on `terrain.profile113`, whose step array is
the single value `[5]`. A 40 m side on it is stored with an 8-row grid whose records lie 5, 10,
15 ... 36 m from the line; a 35 m side has 7 rows, 45 m has 9, `profile121` `[4]` at 12 m has 3:
**when the steps run out before the size, the last step repeats.** Both builders stopped at the
end of the array and built such a side one step wide. Seven sides within 500 m of the bay were
under-built that way (Program164). Fixed in `BuildSide` and the prefab corner builder; heights
repeat their last value.

**Bezier patches** (item type 39, 502 in the nine sectors round the bay) are now parsed and
built: sixteen control points relative to the item's node, a u16 x u16 tesselation, the node, a
seed, three vegetation entries, vegetation spheres and an inline quad grid in the road-side
format (TruckLib BezierPatchSerializer). The surface is the bicubic Bezier through the control
points sampled on the quad grid (rows along the x index, columns along the z index), painted by
the shared `EmitQuads`, which was split out of `BuildSide` for the purpose. Only two of the 502
lie within 400 m of the bay, both 300-400 m backdrops, so they are not the bay's holes.

**LODs and the "background meshes".** No far model is drawn near the player: the parser skips
FarModel items (43) altogether, and the only three in the nine sectors are 690 m, 3 km and 5 km
from the bay. Of the 150 model items within 300 m, none is low-detail by vertices per footprint,
but the parked-car groups the map places are `*_far_hookup` stand-ins - 52 vertices for five cars
- whose detail the game adds at runtime through HOOKUPS: 461 Hookup items in the nine sectors
(313 `traffic.parked.car.common`, 71 `traffic.parked`, 57 parked trucks; layout u64 length,
string, u64 node), which draw a `traffic_vehicle` from `/def/vehicle/traffic_storage_*.sii` by
tag query (`allowed_vehicle[]: "car common"`), plus 134 hookup locators on 30 nearby models
(gate bars, lamps, fill lights). The world builds none of them, nor the 89 Building items within
300 m (walls, netting, power lines: `building_scheme` -> `building_model` with `model_desc`,
`width`, `railing_model`, repeated along the line between two nodes with `stretch` and per-segment
height offsets - the parser already reads them), nor Curve items (3647 in the nine sectors:
`curve_model` with `model_desc`, `variation`, `start_part`/`end_part`, `fixed_step`; body: node,
forward node, two locator uids, length, subcurve mask and subcurves), nor Compound items (609:
u64 node, u32 count, count x (i32 type + full child item), u32 nodes, 56-byte nodes). That is the
missing near-field detail; the next package.

**Census** now also reports cells with a collider but nothing opaque drawn under them, the shine
of any surface above smoothness or metallic 0.3, and transparent queues.

## The depot at the bay was 13 m out of place (2026-09-18)

A prefab's body carries, after its node list, a list of SLAVE ITEMS - the service items a company,
garage or fuel station owns (TruckLib `PrefabSerializer`: `pf.SlaveItems = ReadItemRefList(r)`
between the node list and the ferry link). This reader assumed that list was always empty and took
the origin twelve bytes past the node list, so any prefab that owns service items had its origin
read out of the middle of a uid: the DomDepo beside the truck's bay came out with origin 232 and
the Scandinavian garage with 22710, and every terrain profile and coefficient after it was shifted
eight bytes per slave (one corner decoded a coefficient of -1.4e15). The origin selects which
descriptor node the prefab's first map node stands on, so a wrong origin places and turns the whole
prefab wrongly.

Measured over the nine sectors round the bay: 25 of 690 prefabs carry slave items; 24 of them moved
once the origin read correctly, by a median of 45.7 m (p90 77 m, max 114 m), and 16 turned - half
of those by 90 or 180 degrees. The list is exactly what you would expect it to be: fuel stations,
garages, the quarry, the chemical plant, the cement works, the airport cargo depot, the Iveco
dealer, the hotel, the recruitment agency - and `dlc_balt_237`, the building-material depot 9 m
from the truck's bay, which was 13.5 m out. After the fix, all 690 prefabs have an in-range origin,
every one's map node count matches its descriptor's, no coefficient is absurd, and all 1924 terrain
profile tokens resolve to real units. `EtsMapPrefab` now keeps the slave uids; they are the fuel
pumps and weigh stations to build later.

**Colliders that were never cooked.** Unity logs "cleaning the mesh failed" whenever PhysX rejects
a mesh, and then the piece gets NO collider: visible ground with nothing to stand on, which the
census counts as a hole and the truck falls through. The cause is ordinary map data - the mapper's
vertex records put neighbouring grid vertices on the same spot to taper a strip to a point, which
makes zero-area triangles. Both `EtsTerrainBuilder.Piece` and the model builder now cook from a
cleaned copy (degenerate and non-finite triangles dropped, the drawn mesh untouched), and the
build report says how many meshes needed it.

**Census correction.** Blended ground is a pair - a blended mesh with no collider plus a
single-brush stand-in that carries the collider with its renderer switched off - so the census's
"is anything drawn here" test called about 500 cells of good terrain undrawn. It now follows the
pair.

**The ground fill's rim.** The fill draws only 25 m quads with no ground at any corner, so every
gap kept an unfilled border a whole cell wide - which, beside the yards, was most of what was
still missing. Quads with some corners on ground are now re-sampled on a grid of their own (6 x 6,
so 4.2 m) and their sub-quads filled by the same rule, with heights interpolated from the coarse
corners so the two meshes meet without a crack. Only mixed quads are sampled, at most 900 a pass.

**Where the holes stand.** Rasterising every ground source offline against the corrected parsers
(harness Program161): the census square round the bay goes from 887 collider-free cells to 765,
and the 235-cell patch at world (289,-79) disappears - that was the depot's own yard, which the
origin bug had moved out from under it. Of the 765 that remain, 431 sit under quads the map paints
with the invisible brush, which is the map saying something else covers that ground: the compound,
building and curve items this world does not build yet.

## The header gate was hiding a thousand items, and the white on the tyres (2026-09-18, evening)

**Items the walk threw away.** The sector walk checks a candidate header's five-axis bounding
volume for plausibility, and its last clause demanded that axis 3 - the (x+z)/2 diagonal - carry
at least a third of the box. An item that runs north-west to south-east keeps x + z nearly
constant, so axis 3 is thin and axis 4 carries the whole span: such headers were refused, the walk
lost the thread and skipped forward. Eleven refused headers were caught sitting exactly at the end
of a correctly parsed body, with perfectly sane boxes - a prefab 249 m across whose axis 3 spans
63 m and axis 4 241 m, roads at 15 m of 69 and 142 of 480. The gate now asks the LARGER of the two
diagonals against the larger box diagonal, and the nine sectors round the bay go from 32,721 to
33,079 items found of 33,953 declared, with roads, terrain items, prefabs, buildings and bezier
patches all up. Fifteen prefab data records that used to end on an item the walk never registered
now match.

**White on the tyres.** `/vehicle/truck/upgrade/wheel/tire/front_1.pmd` carries three materials:
the tyre itself, a `shadowonly` piece the cache already skipped, and a third with the effect
`eut2.fakeshadow.rfx` and no texture of any kind - the blob of contact shadow the game multiplies
under the wheel. Drawn as an ordinary surface it took the fallback colour and came out white. The
cache now skips `fakeshadow` with the other helpers.

**The ground fill's sheets.** Points over a gap used to start at the average height of the whole
2.2 km square, and in a large gap the relaxation never pulled the middle back, so the fill appeared
as huge flat sheets cutting through hillsides. Every gap point is now seeded from the height of the
NEAREST real ground, found by walking outward from all known points at once, and the passes only
smooth the joins. A quad whose corners differ by more than 20 m is left undrawn - that is a wall,
not ground.

**The speedometer needs skinning.** The dashboard's own model names its needles - bones `speed`,
`rpm`, `temperature`, `fuel`, `l_blinker`, `r_blinker` - and the truck's interior definition gives
the ranges: `speedometer_anim` over 0 to 160, `tachometer_anim` 0 to 2500, `water_temp_anim` 30 to
110, each with its own .pma animation beside the model. Nothing drives them because the model
reader does not yet read per-vertex bone indices and weights, so the dashboard is built as static
geometry at its bind pose. That is the next piece of work: read the skin data, build the cluster
with its bones, and sample those animations from the truck's speed, revs and fuel.

## The five things that were making the ground look wrong (2026-09-18, late)

Five probes read the demo's own sector files against TruckLib and against the project's parser,
and between them accounted for 650 of the 785 collider-free cells in the 120 m square round the
bay. None of them was a wrong side, a bad profile or a missing material. Every one is ground the
files hold and this world was throwing away.

**1. The brush grid is stored column-major.** A terrain strip's quads are four bytes each, and
the index is `col * Rows + row`, not `row * Cols + col` - TruckLib puts it as "indexing begins in
the bottom left corner in forward direction", meaning the file takes one step ALONG the strip and
then lists every quad across it. Read the other way every brush, blend and invisible patch lands
on the wrong quad wherever the grid is not square. Measured over the 7,318 non-square grids near
the bay, 621,854 quads and 1,110,936 neighbouring pairs: two adjacent quads share a brush 86.8 %
of the time read column-major, 73.9 % read row-major, and 64.8 % under a random shuffle of the
same quads - the old reading was barely better than chance. Per side, column-major is the more
coherent on 4,522 against 192. This is why surfaces striped across roads and verges.

**2. "inv" is not a hole, it is invisible ground.** The brush resolves to
`/material/terrain/invis.mat`, whose entire body is `material : "eut2.none" { substance : "road" }`
- the same effect SCS puts on a model's `coll` part. A substance is a physics property, grip and
tyre noise, and would mean nothing on a surface with no collision. Both terrain builders dropped
those quads outright, which is 368 of the 785 bare cells, most of them under dirt decals the map
paints over invisible ground deliberately. They are now built with a collider and no renderer.

**3. A negative scale is a mirrored model, not a broken body.** The item reader refused any model
whose scale had a component `<= 0.0001`, which is how the map writes a mirrored copy. Fifty
headers in the nine sectors were refused and every one had a negative component; 42 were genuine
top-level models with their node in the sector table. One of them,
`/model2/building/shop/shop_big_03_balt.pmd` at scale (-1, 1, 1), is a 32.5 x 49.8 m shop whose
footprint was the single largest hole in the ground - 230 cells. Only the magnitude is checked now.

**4. Compounds carry their own node table.** A compound (type 40) is a bag of items the mapper
grouped together, and its children stand on nodes listed INSIDE the compound, which no sector node
table ever contains: of 2,750 non-zero child node references, 2,750 resolve in the compound's own
list and none in any sector's. Because the type had no decoder the walk re-synced on the first
child and read the children as top-level items - so they were parsed all along and then dropped at
the node lookup. The compound case now decodes the children in place and reads the node block
after them, which is the step that makes them buildable. 489 compounds, 1,852 children, and 1,848
of those children now resolve a node.

**5. Curve, Sign and Hookup had no decoder at all**, so their bodies were measured by scanning for
the next header - a guess that desynchronises the walk. Version 898 does not match TruckLib's
current layouts for two of the three:

- **Curve (44)** is a FLAT single subcurve of `112 + 4n` bytes with no use-mask and no list. The
  mask TruckLib expects reads zero on all 3,647 curves; this reading ends exactly on the next
  header on 3,699 of 3,750 and every token decodes to a name that exists in `curve_model.sii`.
- **Sign (36)** is `41 + 24 * boards` untemplated - the template's length is a u64, which is what
  made the size come out four bytes short when read as a u32 - and a templated sign carries ONE
  override array, not the two TruckLib reads.
- **Hookup (47)** is a Pascal string with a u64 length and no terminator, then the node uid. All
  461 decode to a clean pool name and a node that resolves; 312 of them are parked cars.

Together: bodies that had to be guessed at rather than decoded fell from **9,829 to 533**.

**Buildings and curves are now built.** Both are one model repeated nose to tail along the Hermite
between two nodes - crash barriers, fences, kerbs, walls, hedges, powerlines, the painted stripes
down a car park, 9,499 and 3,762 of them in the nine sectors and not one drawn before. The element
is the pmd part named `center` (or the definition's `width`), never the whole model's z extent,
because start, centre and end parts sit side by side in the same file. Count is
`length / (step * stretch)` rounded - to nearest for a building, up for a curve, each measured
against the map's own height-offset arrays, which store one entry per copy - and the spacing is
the length divided by that whole count, so the copies abut exactly and each is stretched to fit.

## The stray camera (2026-09-18, after the runs landed)

Adding buildings and curves took the first build past thirty seconds, and that made a latent bug
visible: the view stopped following the truck. The truck was being followed the whole time - it was
the wrong camera on screen.

`EtsMapViewer` offers to make a fly camera in the editor when the scene has none, and it asked the
question as `Camera.main == null`. Two things make that the wrong question. The truck's camera is
never tagged MainCamera, and - the one that actually bit - `EtsFrontEnd` PARKS the world camera
while the garage and the launchpad are up, setting `enabled = false` on it, and `Camera.main`
ignores a disabled camera. So for that whole stretch the scene looked empty of cameras. Once the map
grew big enough that loading finished AFTER the launchpad appeared, the check fired and dropped a
second camera into the scene at (0, 40, -60). The front end then re-enabled the real one, leaving two
cameras drawing to the screen; the stray was created later at the same depth, so it drew last.

The test is now "is there any camera at all that draws to the screen", counting disabled ones and
ignoring the mirrors' six render-texture cameras.

## The hole narrower than the fill's own cell (2026-09-18, later)

The census taken 500 m from the bay found a class of hole the census at the bay never showed: a
16 x 128 m strip of sky beside road blke31, a railway. The map is not missing it. Six terrain items
cover the point and every one reads right side size 0, left side 17 to 110 m, so the ground either
side of the strip is described and built; what was missing was the strip between them.

The ground fill was passing it over by rule. Its coarse grid is 25 m, and it re-samples finely only
a quad with SOME corners on real ground and some not - a mixed quad, which is the rim of a gap. A
quad with all four corners on ground was skipped outright, and a 16 m strip fits inside a 25 m cell
with all four of its corners landing on the terrain either side. Nothing ever looked between them.

Each full quad now gets ONE ray at its centre, and only a quad that ray falls through is re-sampled
on the fine grid. That is about 7,700 extra rays over the 2.2 km square against the 33,000 the fine
pass already spends, and the border-quad cap went from 900 to 2,400 to leave room for what it finds.

## The runs were laid wrong, and the validator that caught it (2026-09-18, night)

Four probes went back over the buildings and curves. The first thing they found was that the
measurement I had used to justify the copy count does not hold, and the second was three defects in
the placement that are far larger than the count ever was.

**The count is not established, and the earlier figure was noise.** The rule was checked against the
stored height-offset array on the assumption that it holds one entry per copy. It does not: items
identical in model, variation, length to two decimals, stretch and fixed step store DIFFERENT array
lengths on 19 of 51 curve groups and 3 of 15 building groups. No step explains any large group, and
the ceiling under any candidate step is 52 % for buildings and 33 % for curves - so "49 % for
rounding to nearest against 44 % for rounding up" was fitting noise. The rules stay as a reasonable
default and the file now says so.

**A validator that does hold.** The item header's five-axis bounding volume is the generated
geometry plus a uniform 0.125 m margin - the residual sits at exactly -0.125 / +0.125 over 1,542
curves. Under it: a 20.2 m concrete wall predicts y 37.15..45.15 from its base0 part where the file
stores 37.02..45.27, and a 26 m pebblestone barrier predicts 64.967..66.701 against 64.973..66.703.

**1. The element is not modelled about z = 0.** A pmd lays its start, centre and end parts side by
side down one long strip, so the repeated part can sit anywhere: fence_metal_conc_white_01_fr_crv's
"center1" is a 2 m panel modelled at z -54.2..-52.2, and mud_decal_blke's "center1" at -61.5..-50.9.
Only 2 % of the curve elements near the bay have their leading edge at z = 0 and 85 % sit somewhere
else. The game discards that offset and lays the part ON the path - over the 461 curves whose
element is more than 10 m from its own origin the stored box grows past the two nodes by a median of
0.85 m, not by the offset. Leaving it in threw 78 % of curve copies and 70 % of building copies more
than a metre off: median 1.99 m, p90 26 m, max 232 m.

**2. Every copy was facing backwards.** A run is modelled along the GAME's +z - "start" sits at a
lower z than its "end" on 2,614 of the 2,908 paired parts across the curve models - and ToUnity
negates z, so in Unity the run's forward is the copy's local MINUS z. Looking along the path turned
every element through 180 degrees. Invisible on a symmetric fence panel, wrong on a kerb ramp, an
arrow, a stair tread, and on both end caps. The two fixes HAD to land together: on its own the
rotation is worse than leaving both, because today the errors partly cancel for the 13 % of parts
modelled at z in [-L, 0].

**3. Copies were spaced by the Hermite parameter, not by distance.** Equal steps of t are not equal
steps of arc length; measured over the curves near the bay the two differ by a median of 0.10 m and
up to 3.85 m on a 321 m tree wall. The path is now sampled 64 times and walked by distance.

**Two skip classes closed.** A wall, bank, river or weed line has no part called "center" - its
segments are base0, base1, ... - which was dropping 117 items in the nine sectors, so BasePart picks
one by the item's seed. And power_wire_straight_crv's "center" is four vertices all at z = 0, a
cross SECTION rather than a segment, with fixed_step 50 in its definition; every one of the 26 items
near the bay is under 23 m, so the count is one span, which is what a wire is.

**"inv" settled, and it was the sidewalk.** Of 910 terrain_mat units with a path, exactly one
resolves to an eut2.none material, and the inv band beside a template city road is the SIDEWALK's
footprint - road ger34's right side is inv on rows 0-3, which is the first 4 m out from the slab,
and the map lays two 3 m sidewalk curves there. A cross section 17 m along reads slab 3.3..12.8,
sidewalk 12.8..14.9, sidewalk 14.9..17.4, terrain 17.4..22.4: no gap. Of 104 inv quads round the bay
none are covered by nothing. The census patches were the displacement bug above tearing 8 m holes
where two sidewalk runs meet on a shared node - predicted 7.9 m from the part offsets, measured 7.8
and 8.0. Drawing inv with a neighbour's brush was tested and REFUTED: it would add 1,765 m2 of
co-planar duplicate ground in one square and put grass under 174 m of city pavement.

## The anchor had the wrong end of the part (2026-09-20)

The run-placement fix shipped in the previous pass made the placement WORSE, and only measuring it
showed that. Replaying both rules offline against the validator - the item's stored five-axis box is
the generated geometry plus a uniform 0.125 m margin - over the 1,516 curves near the bay:

    no anchor at all, old rotation          median 4.79 m   within 0.5 m on  2 %
    anchor = i*spacing - loZ*along, old rot median 1.99 m   within 0.5 m on 22 %
    anchor = i*spacing - loZ*along, NEW rot median 6.26 m   within 0.5 m on  9 %   <- what shipped
    anchor = i*spacing + hiZ*along, NEW rot median 1.74 m   within 0.5 m on 27 %   <- correct

The reason is that the two corrections were inconsistent with each other. The rotation flip puts the
model's +z BACKWARDS along the path, so a model point at local z lands at path offset
`anchor - z * along`, and the part spans `[anchor - hiZ*along, anchor - loZ*along]`. To make that
start at `i * spacing` the anchor has to cancel the HIGH end: `anchor = i*spacing + hiZ*along`. The
`- loZ` form is the right one for the OLD rotation, which is what the probe derived it against; put
together with the flip, the two errors added instead of cancelling.

Worth keeping in mind: the residual is 1.74 m, not 0.125 m, so the geometry is right on 27 % of runs
and merely closer on the rest. What is still wrong there is the ELEMENT CHOICE - which part of the
variation gets repeated, and how many times - and the count remains unestablished.

## The tutorial start, the GPS lane, the mirror paint and the grey boxes (2026-09-20)

Four probes, and three of them found that the world was doing something the files plainly contradict.

**The truck was being stood on the company's MAP MARKER.** A prefab descriptor's spawn points carry
a type, and type 13 is the point the game stands the Company ITEM on - its marker. Measured: for all
33 companies round the start the company item's own node sits on its type-13 point to 0.00 m. The
game starts a job on a type-1 point, the trailer's loading place, and sets the tractor BACK along
that point's own axis so it can couple. The demo's own log settles the distance: it records
`[actor] Initialized at [20511.1;45.9;11846.5]`, the only company within 200 m is euroacres in
Vienna, and resolving that position in the frame of its nearest type-1 point gives **-18.34 m along
the axis, -0.08 m across it** (which is the rounding of the printed heading) and the same height to
the centimetre. The world now spawns on type 1 with that setback. *(Superseded: that sign was an
artefact of reading the rotation x y z w. Measured again on 1.61 with the rotation read w first, the
game stands the tractor 13.68 m AHEAD of the point. See "Game 1.61".)*

The rest of the real first drive, from the files, for when the tutorial proper is built: the player
picks an HQ city (`/ui/desc/profile.sui` -> `/ui/hq_selection.sii`, stored as `hq_city`), a looped
cinematic plays over it (`/def/special_event_data.sii` -> `cutscene.game_intro` ->
`/def/camera/city_start/`), nearly every desktop screen is locked
(`/def/initial_save/normal/locked_screens.sui` is a LOCK list), and the first job cannot be
cancelled (`@@no_cancel_first_job@@`, with `first_job_preferred_radius: 0`). The job lends a company
truck and a loaded company trailer from `/def/vehicle/truck_company/`.

**The route line sits on the median.** A prefab's .ppd holds one 132-byte navigation curve per lane,
named `nav_<lane>_in_<node>`, and across the 25 sectors round Frankfurt 5,313 of 5,366 joints put
those curve ends at exactly `road_offset + 2.25 + 4.5k + lane_offsets_right[k].x` metres right of
the road's direction. On ger34 - the road in the screenshot - that is tram tracks at +/-1.75 and
driving lanes at +5.50 and +10.00, carriageway centre +7.75. Drawing the line on the spline puts it
7.75 m to the left of the lane, in the middle of the tram tracks, which is exactly the screenshot.
NOT YET APPLIED: patches are written and compiled in scratchpad/v432.

**The paint was a mirror because of a missing default.** The cab is
/automat/ed/ed82dfb511c1299a.mat, `eut2.truckpaint.tsnmap16.rfx`, and it states NO env_factor - as
do 249 of the 306 truckpaint materials on the demo's vehicles. The cache read "missing" as 1.0, full
strength, where the 57 that do state one say 0.073239 in 50 cases (the linearisation of an authored
0.3). Worse, the game's truckpaint environment is not the world at all: texture_reflection is six
references to two flat 256x256 images, and the game's own compiled shader for that effect tops the
whole term out near 0.019 of white at a grazing angle. URP was returning 0.428 of a realtime probe
of the sky at 81 degrees. Environment reflection is now off for truckpaint and the missing
env_factor defaults to the game's own 0.073239.

**The grey boxes are the backdrop, and the fog was the wrong shape.** Not a wrong LOD - the world
already picks the highest detail - and not a far model, which decode exactly now and whose nearest
geometry is 16 km away. They are the demo's own backdrop families (/model/village_far,
/model2/panorama, /model2/mid_city_buildings, *_mid, *_veryfar) standing 351 to 600 m off and
correctly placed to a median of 0.127 m. Two things made them read as boxes on the pavement: the fog
was a LINEAR ramp where the game's is exponential - at 355 m the game is 61.9 % hazed and this world
was 21.0 %, at 500 m 76.1 % against 31.1 % - and the world ignored the header's view-distance byte,
which is METRES OVER TEN, drawing 1,262 model items at the bay where the game draws 242. Both fixed.
A fourth defect fell out: the map item's additional parts were parsed and never passed to the model
builder, so factory_hall_04_it was drawn as 594 of its 6,204 vertices.

## Walking, VR and uploading (2026-09-21)

**The avatar stands on everything this world builds, measured.** The avatar's CharacterController
is on layer 2 (Ignore Raycast) in both `Player.NonXR.Body.prefab` and `Player.XR.Body.prefab`, not
on Player (24). The client's `DynamicsManager.asset` row for layer 2 collides with 0 Default,
14 Terrain, 16, 23 Environment and 28 Vehicle, and the VR ground check (`_groundLayers` on
`Player.XR.Features.prefab`) is 0, 2, 7, 14, 16, 23, 28. The roads, terrain, prefab ground and
fill are on Default with MeshColliders, and buildings are on Environment. Quake's walked map is
built under a Default-layer loader (`MAP LOADER`, m_Layer 0). The spawn platform (a 30 m plane with
a collider) holds the player until the map streams in.

**Correction (same evening):** this section first said the client's collision matrix differs from
the SDK project's. It does not: the two `m_LayerCollisionMatrix` strings are byte-identical. What
the editor lacks is an avatar. The body boxes carry `excludeLayers` for the drive surface so they
pass through the road the wheels ride on.

**Getting out.** The Escape menu has "Get out and walk" (left Y in VR). Out: the seat hands the
player back to the client (locomotion, gravity, collision). The player is then teleported through
`DoTeleportToPoint` to the ground 1.6 m outside the driver's door, facing the truck. The truck
parks and EtsTruckCamera gives the view back. Getting in is clicking the truck: see the next
section.

**VR (EtsVr, EtsVrPanel), following AoM and Quake.**
- Every screen made through `EtsGameUi.CreateCanvas`, plus the Escape menu, becomes a
  world-space panel. It goes on the UI layer (5) with a `TrackedDeviceGraphicRaycaster`, the
  scaler is off, and `worldCamera` is re-read twice a second. It hangs upright 0.8 m in front of
  the eyes, 0.95 m across the 1440-unit frame, and comes round again if the player moves 2.5 m
  away. Full-screen sheets are cut to the frame.
- The route advisor stands on the dashboard right of the wheel. `EtsVrDash` puts speed, gear and
  job on a plate above the wheel.
- EtsTruckCamera never writes the camera in a headset. EtsTruckSeat moves the rig instead
  (Quake's PlaceHead): the eyes are put at `DriverEyeLocal`, keeping the head's own movement since
  sitting down (clamped at 0.45 m, re-taken past 0.7 m).
- The front end never switches the camera off in a headset. The garage stays hidden, because it
  is 2.5 km down with no floor.
- Controls, picked around the client's bindings (menu buttons = its menu, right B = its
  InspectorMode): right trigger accelerate, left trigger brake/reverse, left stick steer, right A
  parking brake, right stick click horn, left X menu, left Y get out/in.

**Uploading.** The server compiles the asmdef against a fixed list. AoM's
`Scripting/logs.txt` shows it resolving XRI, UnityEngine.UI, TMP, URP, the Bridge and the
UnityEngine modules (XR, VR, Physics...), and skipping CommunityModules and the SDK's own
assemblies. Unity.InputSystem is not on it, so nothing here uses it. The asmdef now references
TMP, UnityEngine.UI and Unity.XR.Interaction.Toolkit. The scene's Uploader has scripting on,
assembly SomniumEuroTruck, and **DoReplaceExistingWorld = 1 with world 12510 "Bigger transit"
selected**. Check that is the slot meant to be overwritten before uploading.

**Not verified in a headset.** Nothing in this section has run in the client yet. What a first VR
run's log should show:
- "VR seat: the camera rides on the body" (or "is outside the body"), naming the rig moved;
- panels pressable by the controller ray;
- the eye landing in the cab rather than above the roof.

The dashboard offsets (advisor at eye + (0.42, -0.34, 0.62), dash at eye + (0, -0.27, 0.58)) are
placements, not measurements.

## The truck fell through the world in the first client run, and getting in by clicking (2026-09-21, night)

**What the log said** (the client's `Player.log`, a VR run on world 12510): "Player seated in the
truck", then "VR seat: the camera rides on the body; moving rig 'XR.Body'". During the launchpad,
with no input and nobody driving, the heartbeat read: truck at (1, 0, -2) 6 km/h, then
(0, -5, -7) 11 km/h, then (-32, -517, -23) 297 km/h, then (-54, -1664, -32) 434 km/h.

**Why, from the client source.**
- The Bridge's `SetCollisionDisableState(true)` does one thing: `MotionFeature` ORs layer 2 into
  the avatar CharacterController's `excludeLayers`. The client's own comment says this stops
  players bumping into each other. The capsule stays solid against everything else, including
  the truck (layer 28 hits 2).
- `XR.Body` and `NonXR.Body` each carry a kinematic Rigidbody and a CharacterController. The seat
  wrote that body into the cab every frame, inside the truck's dynamic body box. PhysX pushed the
  truck out at up to the 10 m/s depenetration speed, and the next frame put the body back.
- `PlaceOnGround` had released the parking brake and left the gearbox in neutral, so the push
  became rolling.
- The wheel rays started at the hubs. Once a hub was under the road, the ray started below the
  surface and found nothing, and the box ignored the ground, so the fall never stopped.
- The editor never showed any of this because it has no avatar.
- The client's own vehicle mode switches the CharacterController off entirely
  (`NonXRMotionFeature`). The Bridge offers no way to do that.

**What changed.**
- `EtsTruckController.Driven` / `SetDriven`. Nobody in the seat: the truck is *parked*, meaning
  kinematic, parking brake on, solid, and unpushable. Someone in the seat: it runs, and its box
  (and the trailer's) also ignores the avatar's layers 2, 7 (the VR hands' 1 kg physical boxes)
  and 24 (`EtsLayers.SeatedBodyExcludes`). After getting out, the box stays see-through for one
  more second while the Bridge lifts the capsule clear. The editor is `Driven` from the start, as
  before.
- `PlaceOnGround` leaves the parking brake on; the accelerator releases it.
- Wheel rays start 12 cm above the hub (first a tyre radius; see below) (`CastWheel`), so a hub pushed under the road
  still finds it. If no wheel is grounded for 3 s while falling faster than 15 m/s, the truck is
  put back where it was last set down, or held there if that ground is gone.
- `EtsJobs`: when a depot never builds, the truck is no longer let go in mid-air.

**Getting in: click the truck.** The player now starts on foot (`EtsTruckSeat.WantSeated` = false).
`EtsTruckDoor` builds a separate handle on the truck, following the client's source:
- **Desktop.** There is no XR interactor in desktop mode. World clicks arrive as UI pointer
  events, through XRUIInputModule and a PhysicsRaycaster on the desktop camera whose mask leaves
  layers 0, 1, 4, 5, 14 and 16. This is how the client's own `SDKSitInteraction` works.
- **VR.** The pointer is XRI's NearFarInteractor: far-ray mask 0, 5, 6, 10, 14, 16, 23, 5 m reach,
  triggers ignored, interaction layers 144, and hovered-activate on.
- **So the handle is:**
  - on layer 16, a solid box (not a trigger) with `excludeLayers = ~0`, like the client's tablet
    MuteButton;
  - carrying an `XRSimpleInteractable` on interaction layer 4 (`activated` and `selectEntered`)
    and an `IPointerClickHandler`, plus a legacy-Input raycast fallback on left mouse down;
  - saving and restoring the truck's inertia tensor when it is added;
  - active only while a Somnium player is on foot, the truck is placed, the front end has handed
    over to driving, and the Escape menu is shut. The game's order is menu, job, truck.
- In VR the trigger that pointed at the truck is also the accelerator, so the pedals wait for
  both triggers to be released after getting in.
- When a job moves the truck to a depot more than 25 m from a player on foot, the player is stood
  beside it. The stream follows the truck, so the old yard would go.
- While a Somnium player is on foot, the truck's keys stay off and the camera stays the
  client's, checked every frame (EtsMenu).
- F is the client's Interact key, so EtsTruckCamera's free-camera F is editor-only.

**Still not run in the client.** A first run's log should show the heartbeat truck position
staying put, "The truck can be got into: ...", "trigger on the truck: getting in" or "click on
the truck: getting in", then "Player seated in the truck.".

### The spin on getting in, and what the review found (2026-09-21, late)

**What the log said.** The second client run proved the parked truck holds: `(0, 0, 0) 0 km/h`
right through the menus. Pointing at the truck and pulling the trigger got the player in. The
moment the truck went dynamic it spun and flew: `(30, 34, 15) 22 km/h`. It fell through twice, and
the fall recovery caught it both times.

**Two faults, both mine.**
- `EtsTruckDoor` read the Rigidbody's inertia in the same frame the body box was created, and set
  it back after adding the handle. Unity works mass properties out lazily, so it almost certainly
  read the placeholder of a body with no colliders. Setting the tensor also switches the automatic
  calculation off for good. The inertia is now left to Unity. The first driven physics step logs
  "Truck driven: mass ..., inertia (...) (automatic)" to prove it.
- `CastWheel` lifted the ray origin by a whole tyre radius (0.5 m). That is enough to find a second
  ground surface lying over the one the wheel is on, and the bump stop answers that with a force
  big enough to throw the truck. The lift is now 12 cm.

**Adversarial review (three lenses, a skeptic per finding), fixed:**
- **Stuck after the demo.** "Keep driving" on the demo end screen left the player in the cab with
  no keys, no menu and no way out. It now calls the front end's `StartDriving`.
- **Stuck on the loading screen.** A job whose depot never built left the loading screen up for
  good. `EtsJobs.JobFailed` now respawns the truck and returns to driving.
- **Dropped-off trailer shoving the truck.** A trailer frozen on delivery overlapped the truck's
  box with the joint gone, and pushed the truck at 10 m/s. It now ignores that truck until the two
  are clear. The truck also holds on its parking brake at delivery.
- **Respawn leaving the trailer behind.** Respawn and the fall recovery moved only the truck, and
  the locked joint yanked the trailer after it. `PlaceOnGround` now recouples the trailer.
- **Respawn leaving the player behind.** A respawn while on foot could move the truck kilometres
  away. The seat now watches `Placements`, not the spawn point.
- **Getting out while tumbling** froze the truck in mid-air (9 m up in the log). Parking now sets
  an airborne or tipped truck down upright first.
- **Keys during the job's loading screen.** The Escape-menu job button now closes the menu before
  taking the job, so the loading screen keeps the keys locked.

## The holes, and the right eye (2026-09-22, night)

**VR shaders.** The client runs single-pass instanced stereo; its Player.log says
"Stereo Rendering Mode: SinglePassInstanced" on Direct3D11. Neither custom shader was stereo-aware:
- **SomniumEuroTruck/Multi.** Its Forward and DepthOnly passes never wrote the eye
  (`SV_RenderTargetArrayIndex`), so every Multi surface went only into the left eye. That covers
  buildings, trailers, terrain brush pairs and prefab ground. The client's renderer forces depth
  priming (`URP_Renderer.asset m_DepthPrimingMode: 2`), so the right eye got neither depth nor
  colour for them. In the headset that is holes.
- **SomniumEuroTruck/Decal.** It had no instance ID at all. Lane lines, dirt and the route ribbon
  were drawn twice into the left eye and not at all into the right.

Both now carry URP's own stereo macros. This was checked by compiling with dxc against the
installed URP 17.3 includes, with `STEREO_INSTANCING_ON`: the corrected vertex shaders output the
eye index, and the desktop variants' signatures are unchanged. Multi also gained a DepthNormals
pass with the same material constant buffer.

ETS2's own VR mode is in its executable (OpenXR, OpenVR and Oculus back-ends, `r_hmd_*` settings).
The archives hold no VR shader or effect set: of 375,202 files, only `_oculus` camera limits, a
centred route-advisor layout and a stereo UI material. There is nothing to switch to in XR.

**Terrain holes.** Two independent offline replicas reproduced the client's census: 221 of 14,400
cells, the same patches to within 1-2 m. The causes, largest first:
1. **Legacy city roads built too narrow.** The world ignored the look's `road_offset` and the city
   sidewalk.
   - The header flags carry IsCityRoad (bit 19) and the left and right sidewalk sizes (bits 8-9
     and 10-11: 8/4/2/0 m).
   - The `.data` record carries the side's `sidewalk.<token>` material (`2` =
     `/material/road/small_block_pavement.mat`), which the reader had been stepping over.
   - Measured on every straight legacy road: a city road's header box reaches offset + size +
     shoulder + sidewalk (+ about 1 m).
   - About 76 % of the hole cells. Sidewalks are now built as tiled pavement strips, with the road
     terrain outside them. Program990 checks the readers: 31 of 37 roads within 300 m are city
     roads, 23 with 4 m sidewalks both sides, all naming sidewalk.2.
2. **The ground fill.**
   - It skipped 25 m squares whose corners and centre hit.
   - It left a 4.2 m rim round every gap.
   - It counted roofs (prefab buildings on Default) as ground.
   - It counted its own old sheet as ground when rebuilding, then deleted it. After 300 m of
     driving that reopened every gap: measured 539 holes round the start and 1,437 round the truck.
   - Rewritten (see its header). The census now also runs after every rebuild.
3. **Single-quad ground plates had no collider.** The 12-index rule in EtsModelBuilder took it off
   `/model/plaza/pavement_plaza1.pmd` (40 x 50 m, 6 indices) and `grass_square.pmd`, so they were
   drawn but could be fallen through.
4. **Model ground plates were on Environment.** The wheels and the fill could not see them; flat,
   low plates now go on Default.
5. **`EtsRunBuilder.PartLength` read BoundsMax twice.** Every length came out 0, and 1,003 of
   1,006 building runs near the start (hedges, walls, fences, pavements) were skipped.

Replica result with the fill, collider and layer fixes: census 221 -> 9, the second census
171 -> 2, and after a 300 m rebuild 9 and 6 (it was 539 and 1,437).

## Start area, sitting, the wheel, networking and the look (2026-09-22)

**The start is right.** Three sources agree that the game starts this player at the TransiNet yard,
Szczecin, game (14994.0, 46.8, -15235.2), where the scene centres the map:
- the player's own `game.log.txt` ("First job selected from: transinet.szczecin to rt_log.rostock",
  "[actor] Initialized at [14994.0;46.8;-15235.2]");
- their 1.61 save (`hq_city: szczecin`; read-only, decrypted);
- the client log.

The start follows the profile's HQ city (the 1.48 profile had Vienna). "Wizard skipped." in the log
is the controller wizard, not the HQ choice, so the EtsMapViewer tooltip that reads it that way is
wrong. The demo ships the whole base map (282 sectors, 80 cities, no area limit). The start is the
sparsest place measured on it: 765 m from the northern data edge (z = -16000), with nothing north of
that, and 47 % ground-item box coverage against 85-94 % at Rostock, Berlin and Hamburg.

**Sitting.** `SomniumBridge...Features.Poses.SetPose(PoseType.SittingStraight)` is re-applied every
seated frame, because the client drops a pose on its cancel action: left X in VR (the same button as
this world's menu) and probably Escape on desktop. In VR the client's re-centre on entry is undone on
`HeadCustomPoseOffset`, and the rest pose is re-taken. On desktop the root is lowered so the posed
head lands on the driver's eye. `SetPose(None)` runs on release. The client networks the pose, so
others see the driver sitting.

**The wheel (VR).** EtsSteeringGrab reads the grip (UnityEngine.XR) and the Bridge's hand
transforms, after the seat has placed the rig. One hand follows its angle round the axle; two hands
follow the line between them. The mapping is 1:1 at 450 deg per unit of input, with haptic bumps at
the lock. Held, steering is absolute (physics.sii: the speed multipliers are for relative steering
only). Released, the wheel returns by the game's autocentring rule, with a floor. The rim radius is
measured from the model (the DAF's hand slots are at 0.219 m).

**Networking, in AoM's pattern.**
- Photon Fusion, PhotonLibs and the Somnium NetworkBridge scripts were copied from the AoM project
  with their GUIDs.
- The scene gained 'Ets Net', 'Scene Networking', 'Ets Net Truck 0' (NetworkObject +
  NetworkBridgeData64, 26 ints of truck and trailer state) and 'Ets Net Events'.
- Authority is whoever sits in the seat, via RequestStateAuthority. Everyone else draws a kinematic
  puppet on a stamped Hermite timeline.
- Clicking an occupied truck is refused with the driver's name. A driver who leaves is parked by
  the master.
- The map and the ground fill now stream round a walking player (`EtsTruckSeat.StreamFocus`), not
  the truck.

Nothing of this is tested with two players yet. The log lines to look for start
"[SomniumEuroTruck] Network:".

**The look.** In rough order of how much of the view each covered:
- **Weather.** EtsClimate took the first sun profile with fog, `default.nice.01` (sun at -40
  degrees, night), and its overcast slot 3: saturated blue fog, 62 % haze at 300 m. It now takes
  the profile nearest a rising 50 degree sun (`default.nice.15`), slot 0 (clear), exposure 0.065 on
  the file's HDR values: 23.5 % haze at 300 m. The sun, flat ambient and SH are from the file.
- **Sky.** EtsSky plus a stereo-safe `SomniumEuroTruck/Sky` shader draw the profile's own HDR
  panorama, with the fog colour below the horizon and a sun disc.
- **Water** was black (`water_near_color {0,0,0}`, wave normal map used as colour, no
  reflections). It is now blue, reflective and bump-mapped.
- **`eut2.decalshadow`** (the factory yard) was drawn as an opaque grey slab fighting the asphalt.
  It is now a black decal with alpha from the texture's red channel.
- **Specular** is masked by the base texture's mean alpha, so asphalt tiles no longer glare.
- **Texture scale.** Terrain uses 1.61's `texgen_0_gen` (grass 13 m, dirt 3 m), and the fill and
  fallback grass match. `tg0`/`tg1` model pieces get position-generated uvs, as the game's vertex
  program does.
- **Colour.** Vertex colour is applied: baked into Lit materials, multiplied in the Multi shader
  (not mode 3). Material colours are passed as linear values.
- **Rocks.** Grass-topped rocks use Multi mode 4.
- **Trees.** Planted at 20 % of the map's density (was 4.7 %, 611 of 13,087 near the start), with
  LOD to sprites.

**Pre-upload review, fixed.** Three lenses; a skeptic checked each finding.
- **VR hands.** The client's pose entry turns the Left/RightHandCustomPoseOffset by the same angle
  as the head. EtsTruckSeat now undoes all three, so the hands and the wheel grab read true.
- **Shared meshes.** The stream's unload destroyed meshes shared through EtsModelBuilder's caches
  (every tree of a species, every copy of a prefab piece, and their colliders). Now
  `EtsModelBuilder.IsShared` guards the destroy. This bug was older than tonight.
- **Jobs taken on foot.** They waited for a depot that never built, because the stream followed
  the walker. `EtsJobs.Relocating` keeps the stream on the truck while it is being moved.
- **Leftover puppet trailer.** It stayed solid over the tractor when another client took the truck.
  It is now detached with IgnoreCollision, as a real trailer is.
- **Multi's matte switch.** `_ENVIRONMENTREFLECTIONS_OFF` was never enabled on Multi. The shader
  now declares `_EnvironmentReflections`, as URP Lit does.
- **Smaller fixes:**
  - a start cut short by losing the truck now raises JobFailed, so the loading screen cannot stick;
  - a superseded or offline network claim is always answered;
  - the fake-null `??` pattern is gone from EtsNet and the editor setup tool;
  - a parked truck's engine fades with distance for a walker.

Rejected by its verifier: a vegetation frame-spike claim (streaming brings in 1-3 items a tick,
not 6).

## Traffic (2026-09-22)

**What it does.** AI cars drive the map's own lanes round every player and the truck, built from
the game's traffic data on the player's install; nothing is shipped.
- **Lanes** (`Ets/EtsLaneGraph.cs`). Road lanes are offset copies of each road's centre curve, by
  a rule measured on the whole map (99.85 % of road ends meet a junction's lane within 0.3 m).
  Junction lanes are the prefab descriptor's own navigation curves (`Ets/EtsPrefabNav.cs`, 132-byte
  records). Roads flagged "no AI" and anything a DLC guards get no lanes. Offline, 0 lanes lie on
  the 1,148 guarded roads and 518 guarded prefabs of the three sectors that drop the most.
- **Cars** (`Ets/EtsTrafficSim.cs`). They follow each other with the Intelligent Driver Model, and
  reserve a whole way through a junction before entering it. They keep 6 m and 2.2 s from a
  player's truck. Cars appear 320-650 m from every player (60 m in the first minute) and go beyond
  760 m. The mix is Poland's `traffic.sii` (the start is Szczecin): 20 car models.
- **Models** (`EtsTrafficModels.cs`). The game's AI cars, with their LOD1-3 in a LODGroup. Only
  LOD0-1 cast shadows. Separate spinning wheels are hung on LOD0 only, because LOD1-2 have their own
  (harness Program1405). Each car gets one of its own colours on the white-painted body.
- No traffic lights or priority rules yet. The junction rule is greedy, with first come, first
  served after a 30 s wait.

**Networking.** One client runs the cars: the room's shared-mode master, once it holds authority
over all four blocks. It writes them at 10 Hz into four NetworkBridgeData64 (`EtsTrafficWire.cs`):
15 cars of 4 ints each, and only the ints that changed. Offline that is 704 ints/s, 2.8 KiB/s.
Everyone else draws the cars about 170 ms behind on the truck's `EtsNetTimeline`, with `LeadIn`
on, so a new car arrives already moving. When the master leaves, the new master puts every car
back on its lane from the wire alone (27 of 27 offline, within 0.6 cm). Alone, in the editor or
without the blocks, a client runs its own cars (role Local).

**Scene.** `EtsNet` adds `EtsTraffic` to 'Ets Net' in Awake, so no script is placed. The four
blocks are children of 'Ets Net' named `Ets Net Traffic 0` to `Ets Net Traffic 3`. Each has a
Fusion NetworkObject (Flags 524289, ObjectInterest 1) and a NetworkBridgeData64. The menu item
"Somnium Euro Truck/Add network objects (traffic)" adds any that are missing and leaves complete
ones alone.

**The frame (VR, 90 Hz).** Nothing heavy happens in one piece:
- **Background work** runs under `FrameBudgetMs` (1.5 ms) a frame:
  - lanes a road, 24 junction curves or a batch of joins at a time (a sector used to be 23 ms in
    one go);
  - car model templates a material, a mesh or a lod at a time, one car model at a time;
  - worker results at one texture upload each.
  Built in slices, the lanes are line-for-line identical to the one-shot build.
- **One background thread** (`Ets/EtsWorker.cs`) does the archive reads, GDeflate and parsing:
  the car table, junction descriptors, car models and their textures. The pool's textures alone
  are 508 ms of CPU, up to 50 ms for one. `EtsTextureCache.Prepare/Adopt` and
  `EtsEngine.ReadModel/AdoptModel` are the thread-safe halves.
- **Spawn candidates** are gathered 700 cells a step and kept per focus for 2 s.
- **Junction conflicts** are worked out per pair when asked. The full table cost 2.1 s for one
  551-curve descriptor.
- **Clones**: at most one car is cloned and two are shown a frame.
- **Offline step** (300 s, two foci, up to 29 cars): mean 0.13 ms, 99.9th percentile 0.47 ms. The
  worst after the first second is 0.52 ms (median of three runs). The 4.5 ms first step is JIT.

**Paint.** `EtsMaterialCache` now caches a truckpaint material per path and per `PaintColor`. Cars
are built with white, set and restored around each call, and then painted on copies. So a car can
never take the truck's paint material or leave it white, whichever is built first.

**Physics.** Cars are kinematic, on the Vehicle layer (28), outside `EtsLayers.GroundMask`. So
wheel rays, the ground fill, spawn rays and the census never see them. Their boxes collide only
within `ColliderRadius` (120 m) of this client's truck or player. The map's unload never destroys
their meshes, which are EtsModelBuilder's shared meshes (`IsShared`).

**Tunables** (EtsTraffic, on 'Ets Net'): `MaxCars` 48 (the wire carries 60), `PerFocus` 24,
`DrawRadius` 700 m, `ColliderRadius` 120 m, `WriteRate` 10, `FrameBudgetMs` 1.5, `LaneRadius`
1000 m, `LaneDropDistance` 8000 m, `Prewarm`, `UseWorkerThread`, `SectorWait` 20 s.
`EtsTrafficModels.LodHeights` {0.12, 0.045, 0.012, 0.0015} are guesses to tune in the headset. At
a 100 degree view, a car is LOD0 within about 16 m, LOD1 to 42 m, LOD2 to 157 m and LOD3 to
1.3 km. So most cars on screen are LOD2-3 (5-9 and 1-3 renderers against 15-21 at LOD0), and 24
cars round a player cost on the order of a hundred renderers.

**Log lines** start "[SomniumEuroTruck] Traffic:":
- the car table (count, hash) and whether reads run on the thread;
- "N car models ready ... renderers per lod ...";
- role changes ("Local -> Master");
- "took over as master; N cars carried on";
- every 10 s: role, cars, drawn here, built, models ready, lanes and sectors, ints/s written (the
  master), and the worst frame the traffic spent, with the step and background parts.

Offline test: `scratchpad/research/traffic-install` (`TrafficTest.cs`, `run300_final.txt`).

## Phase 1: foundations for the tablet, per-player trucks and saved careers (2026-09-22)

The integrated plan's first phase: seven parts built in parallel, each checked by a second pass, then
merged and compiled together. Almost all of it sits behind `EtsFeatures.NewFlow`, which is **false in
this build**. The world still starts at the TransiNet yard with the DAF and runs the old front end, jobs,
the one networked truck and traffic. Phase 2 turns the flag on. The offline evidence is in
`scratchpad/phase1/` (one folder per part, the checkers' in `<part>X`, the merge in `L`).

**What a player notices with the flag off.** These are the only changes.
- **Pay and XP follow the game's economy.** At the start the three offers pay €2,032, €9,465 and
  €14,104 where they paid €156, €823 and €1,162. Every job distance on screen is now in the game's km,
  the ones pay and XP are counted on (`EtsJobs.Offer.ShownKm`, `ToGameKm`): the board, the offer
  details, the loading line, "km to go" in the route advisor, the status line and the VR dash, and the
  delivery summary. Before, the board showed map km beside game-km pay ("7 km, €2,032"). The remaining
  distance counts down by the route's own ratio of game km to metres driven. The turn distances ("in
  300 m") and the drive-time estimate stay in metres and minutes actually driven.
- **Money is saved.** The desktop shows the saved balance, and the menu and route advisor show
  career totals. Details under "Careers and jobs".
- **Jobs repeat.** There is no demo-ended screen after a delivery (`DemoRun` is off), and Continue
  goes back to the desktop with new offers.
- **A job starts on the depot's trailer bay**, as the game starts one. At TransiNet the tractor
  stands 0.03 m from the game's own spot (game.log "[actor] Initialized at [14994.0;46.8;-15235.2]").
  323 of 365 depots now start on a trailer bay; the other 42 keep the old point.
- **The delivery summary** shows the run's own numbers instead of the layout's sample text
  ("Tomatoes ... 1,300 km"). Its level lines are still placeholders.
- **Trees.** Crowns are full, sprites stay visible past the LOD switch, and leaves sway 0.12 m, once
  the leaves shader is in the upload (see "The leaves"). Grass cards on map models no longer cast
  shadows. Wide trees and hedges swap to sprites at 0.64-0.9 of the old distance.
- **Junctions** on slopes now meet their roads, and traffic rides on their surface. Job points on
  tilted company yards move with the surface: 10 of 236 start points move more than 1 cm, at most
  1.2 m in height.
- **New log lines:** "Map index: ...", "Profile: ...", "Leaves shader: ...", "Prefabs stood on node
  0's full rotation ...", "Seats: ...".

**Careers and jobs.** Money is kept per Somnium account in the world's own Bridge storage
(`SomniumBridge.LocalStorage`), as two check-summed files, `ets_profile_<id>` and `ets_profile_<id>_2`,
in `WorldsLocalStorage\12510`.
- Loading takes the newest good copy and repairs the other. A job is paid once per id, and the last
  ten deliveries are logged.
- If a copy cannot be read, it is kept as `ets_profile_<id>_bad.txt` (or `_bad_2.txt`) before
  anything is overwritten. Renaming it back restores the career: probe 1961 got €52,000 and 700 XP
  back this way, through the client's own encryption.
- That folder is under %TEMP%, and Windows' temp cleaning can empty it. There is no PlayerPrefs copy
  (the user's choice, Q-G1). In the SDK editor the career lasts one Play session.
- A delivery's save costs 7.7 ms median, 16 ms at most, on the delivery frame (Mono, probe 1961).

**Pay like the game.**
- Constants come from `economy_data.sii`, with the community's fixed 350.
- Kilometres are the game's: map metres ×19, or ×3 inside city areas (`europe.mbd`). That comes out
  2.8 % over the player's saved 215 km job.
- XP is one per game km, which matches the save's 16 XP for 16 km.
- Against the one recorded job the pay is 7 % high, until the user reads real offers (Q-G3).

**The map index** (`EtsMapIndex`, on 'Ets Net') is the old whole-map read moved out of EtsJobs,
identical frame for frame: 282 sectors, 141 yields, the same 365 depots and 93,216 road nodes (probe
1954). Its prefab dictionary means an offer run no longer spends about 70 ms searching prefabs.

**Trucks are built by a factory.** `EtsTruckFactory` builds a truck from a recipe or from a preview
identity. `EtsModelViewer` keeps only the scene's configuration and the one scene truck's wiring.
- **Proof that nothing changed:** a throwaway-project run built the DAF both ways and matched node
  for node: 351 nodes, 137,877 vertices, every material and wheel spring. It was re-run on the merged
  tree (checker probe 1871, 20 of 20). The log line is the client's own: "Built 28 pieces (5 skipped),
  45315 vertices, 35447 triangles, 31 materials".
- **Accessory slots come from the definition's folder.**
  `/def/vehicle/truck/iveco.hiway/accessory/sunshld/shape1.sii` is a "sunshld" whatever the save
  calls the unit. Only the DAF's save names its units after their slots, which is why only the DAF
  ever built. Nine trucks have a separate cabin model, which hangs at the truck origin. Seven build
  their wheels from a disc, a hub and nuts.
- **Seats come from the cab's own locators.** Each cab has a "driver*" locator on each side. The left
  one is the driver's seat, and the right one, where a UK driver would sit, is the passenger's. On the
  DAF they are 1.627 m apart.
- **The 18 preview trucks** are listed by `EtsTruckCatalog` with the game's own spec words, including
  `td_info_steering_left` ("Left-hand"). The wire carries the low byte (186) of an FNV-1a hash of the
  sorted stems (0xe2bfc4ba).

**Riders and the passenger seat** (built, not switched on).
- **Why it is needed.** The client keeps players on its elevators by having each one send their
  position relative to the platform, and every client puts them back on the platform it draws itself
  (`MobilePlatformMover.cs:167-189`). None of that is reachable from a world.
- **How `EtsRiders` rebuilds it** for trucks, trailers and AI cars:
  - every rider publishes their body root in the vehicle's frame;
  - the local walker is carried by the vehicle's motion, with `SyncTransforms`;
  - every remote body is moved rigidly onto this client's own drawn vehicle.
- **Result.** A seated rider costs no bandwidth, and the truck's network delay cancels out. Offline,
  a passenger at 90 km/h lands within 0.82 mm of the drawn seat, against 5.35 m uncorrected.
- **Write rate.** Records go out at most 20 times a second, flags included. After stopping, a rider
  keeps writing until two identical records have gone out, so slow movers rest within 5 mm. "Carry
  lost" is counted only above 52 km/h.
- **The passenger** never wakes or parks the owner's truck. Their capsule ignores vehicles while
  seated. They can get out only below 10 km/h, and they get out on the right.
- **Log lines** start "[SomniumEuroTruck] Riders:".

**Sessions and player slots** (wires and a phase machine, built, not switched on).
- **The wire.** Every player gets a slot of 64 ints, "Ets Net Player k", and the master writes one
  session object, "Ets Net Session".
  - Positions are sent as centimetres in game axes, so two clients centred on different points agree.
  - A driving truck with its trailer costs 536-663 B/s at Fusion's 16 Hz, half the old float wire.
    A parked one costs nothing.
  - A coupled trailer is sent as its angle to the tractor and rebuilt on the fifth wheel. It lands
    within 1 mm of the drawn tractor near the origin, and within 4.4 mm at 45 km (Unity's own float
    grid there).
- **Riders on the wire.** A seated passenger lands exactly in the seat, where world positions put
  them 2.5-8.7 m off at 90 km/h. A walker is off by walking speed × the difference between two
  clients' drawing delays: 36-104 mm.
- **The phase machine** is plain code, `EtsSessionLogic`. It is tested against a simulated room for:
  the master choosing, late joiners, the master leaving before and after choosing, playing offline,
  and a join snapshot arriving late.
- **Its rules:**
  - Nobody is master while Connecting, and an Offline player is always their own master.
  - A master who chooses and leaves at once cannot split the room: a Chosen seen while choosing
    always wins (probe 1832).
  - The preview save's own paint travels as int 7 = -1.
- **The scene is not changed yet.** Inserting the 50 slots gives 56 NetworkObjects, not the 57 the
  design said (probes 1820 and 1830).
- **Log lines** start "[SomniumEuroTruck] Network: session:".

**The game's own screens** (built, used from Phase 2).
- The 1.61 locale is plain text: 12,514 keys. Colours are stored AABBGGRR (154 of them).
- `EtsSii` now reads `ui::`-qualified unit types, so all 40 units of `/ui/delivery.sii` have names.
  It also decodes `\r` and `\xHH` (UTF-8) in strings.
- Screens are built from the game's own layout files and look templates. The markup painter
  matches the reference images to the pixel: 0 of 630,000 (PML) and 0 of 126,720 (SDF icons).
- **The HQ catalogue**: 72 cities in 12 countries, each with a yard and 1-8 truck spots (160 in all),
  cached as `ets_hq_catalog_<signature>`.
- **Dijon and Munich stay greyed** until the sector parser finds the node tables their garages stand
  on (probes 1887-1890).

**The tablet** (built, used from Phase 2). The editor menu "Somnium Euro Truck/Tablet/Preview the ETS
2 app (Play mode)" (also "as the instance master", "as a joiner", "blocked (loading)") adds the app in
Play mode as the world's own panel; Tab toggles it.
- **Keys in the new flow:** Escape and left X open the panel only while no Somnium tablet is linked.
  Once the app is on the tablet, Escape, R, left X and left Y are the client's again. With the flag
  off every key does what it did.
- **Log lines** start "[SomniumEuroTruck] Tablet":
  - "(log only): linked - ..." or "MISSING ..."
  - "linked - ..."
  - "removed N objects left by an earlier visit"
  - "app opened (..., size Small->Large)"
  - "app closed (Back | mesh Home | client switched tab | world unloading)"
  - "not linked after N s - ...; N searches, slowest X ms"
  - "no Somnium tablet (...) - using the world's own panel"
  - "the world's own panel was destroyed with what it was pinned to (open|closed) - making it again"
  - "app built into ... in X ms"
  - "the game's menu (10 categories, 40 links) read in X ms on a worker thread"
  - "page X built ... in Y ms"
  - "unlinked (...); desktop re-placed N frames, left M"

**The leaves.** Every tree, bush and grass card in the game is a set of camera-facing cards. The game
pushes each vertex out along the camera's axes by uv set 1, wherever uv set 2 is 0.5 or more. The world
used to draw only the stored points: 4-20 % of a crown, and none of a sprite.
- `SomniumEuroTruck/Leaves` (`Shaders/EtsLeaves.shader`) makes the same push along the camera's
  roll-free right and up. `EtsVegetationLook` sets these for each camera just before it renders.
- It reads colour from the vertex red only (×2), sways with the wind, and discards below the game's
  0.05 alpha.
- **Checked offline:** all 4,767 vegetation models, 0 card corners outside the grown bounds (probe
  1924); 56 of 56 dxc compiles, stereo variants included.
- **The scene's `_leavesShader` field** (Ets Map Viewer) carries it into the upload. Without it the
  log says "Leaves shader MISSING" and trees stay on URP Lit with the grey tint.

**Junctions on slopes.** A prefab now stands on its map node 0 with that node's full rotation, after
the descriptor anchor's own yaw and pitch are taken out (`EtsMapBuilder.TryPrefabFrame`). The model, its
prefab terrain and its spawn points all use this frame.
- **Within 7 km** (probe 1922): every map node is back on its height within 5 mm, where the old yaw-only
  frame missed by up to 3.73 m. 205 junctions tilt and none is refused. Traffic lanes are within
  0.084 m of the drawn surface (was 3.745 m). The start spawn moves by 2 µm.
- **What is left:** a step at road edges of up to 0.236 m (0.694 m within 20 km). It is exactly the
  node's roll × 4.5 m, because roads are still drawn level where the map banks them. Banking the roads
  and the lanes together is a later phase.

**Open and LoadAt.** For the HQ stage, `EtsMapViewer.Open()` mounts the game and reads the map
without building anything, and `LoadAt(origin)` builds and streams round the chosen centre.
`StreamFocusOverride` lets the stream and the ground fill follow the arrival point while loading.

**The merge.** The three compile lists now include `Ets/EtsTabletLayout.cs` and
`Editor/EtsTabletPreview.cs`. All three build with 0 errors and 0 warnings, and at warning level 4
they give the same 9 and 11 warnings as before Phase 1. Every part's acceptance test was re-run on the
merged tree with the same results (`scratchpad/phase1/L`).

**Not yet run in the client:** all of Phase 1 is offline evidence only. The first client run should
show the log lines above, a saved balance after a rejoin ("Profile: ... gen N"), repeating jobs, and
the leaves in both eyes.

### The upload that failed on the master (2026-09-23)

The first upload of Phase 1 failed on Somnium's server: `CS1061: 'NetworkRunner' does not contain a definition
for 'GetMasterClient'`. The server compiles worlds against its own Fusion, which is older than every copy on
this PC (this world's, Age of Mythology's and Quake's are all 2.0.12 and have the call). So a clean local build
proves nothing about the server's API.
- **What the other worlds do:** Quake and Age of Mythology only ever ask "am I the master?"
  (`SceneNetworking.IsMasterClient` → `runner.IsSharedModeMasterClient`), and this world now does the same.
- **Who the master is, for everyone else:**
  - it comes from the session's writer, since the choosing master writes the session itself;
  - after a hand-over it will come from each player's own network slot (Phase 3);
  - the session is never rewritten just to rename its master, because that is what keeps a master who chooses
    and leaves at once from splitting the room.
- **Removed:** the probe's `GetMasterClient` line. The rider code also stopped calling
  `GetAllBehaviours<NetworkTransform>`, which no world that passed the server uses.
- **The guard:** every build is now checked by `scratchpad/fusionapi/check.sh`. It lists each Fusion and Somnium
  member the world uses, and it passes only if Age of Mythology, Quake, Blades Edge or an earlier upload of this
  world already used every one of them. It passes on all 127, and fed the old build it reports `GetMasterClient`.

## Phase 2: the game's own start - headquarters map, loading screen and the ETS 2 tablet app (2026-09-23)

`EtsFeatures.NewFlow` is **true in this build**. The world now starts the way the game does: a black
void with the game's HEADQUARTER SELECTION map, the game's loading screen, then the chosen city's truck
yard. The menus live in the Somnium tablet as an "ETS 2" app. Every truck is the player's own, chosen
in Company > Truck Preference. The same seven parts built it in parallel, each checked by a second
pass, then merged here. The offline evidence is in `scratchpad/phase2/` (one folder per part, the
checkers' in `<part>X`, the merge in `L`). **None of it has run in the Somnium client yet.**

**What a player sees.**
1. **The void.** On load the player is teleported onto a floor in a black sphere, 600 m above the
   map. The large map is 2.4 m ahead in a headset, or fitted to the camera on a monitor.
2. **The choice.** The instance master gets the game's "YOUR HEADQUARTERS" box with OK, then 72
   city pins with a "City / Country" tooltip, then "Confirm selection". Everyone else sees the same
   map, read-only, with the city the master is pointing at lit and "Waiting for <name> to choose
   the headquarters". On a first visit the pins wait for the whole-map read (about 20-30 s); after
   that the list comes from the LocalStorage cache.
3. **The loading screen.** The game's 1.61 screen with real progress: the map up to 90 %, the
   ground 92 %, the teleport 96 %. The yard is built at the chosen city under the void.
4. **The yard.** The player is teleported next to the yard's garage, facing the first truck stand,
   and the void goes only after the client confirms the teleport. The HUD shows "Instance master"
   with the orange star for the master (the VR dash shows it once seated).
5. **The tablet.** "ETS 2" is the first icon on the Somnium tablet's home grid. It opens the game's
   garage desktop: the top bar (name, level, XP bar, master badge, money, "Monday 10:00") and the
   game's menu bar. The first open lands on Company > Truck Preference. A HUD hint says "Open your
   tablet (Tab) - ETS 2 - choose your truck" (in a headset: "Tap B or Y ...") until the player has a
   truck.
6. **The truck.** Truck Preference shows the truck in the game's dark studio, its specs, and
   < Confirm > Random. Confirm builds it over about a second ("Your truck is on its way.") and parks
   it on the garage stand. Close the tablet and click it (VR: point and pull the trigger) to sit in
   the driver's seat, or use ETS 2 > Drive.
7. **Jobs.** Jobs > Quick Job lists 3 offers from the depot nearest the truck, in game km, and says
   why "Take Job" is greyed ("Choose your truck first (Company)", "Get out of <name>'s truck first",
   ...). Take Job shows the loading screen and moves the truck to the depot's trailer bay with the
   trailer. A delivery pays money and XP into the saved career, shows a toast and the delivery
   report, and the top bar and Career show the new balance. Leaving and coming back keeps it.
8. **Alone.** With no room for 20 s the player is Offline: their own master, with nothing written
   to the network. In the SDK editor the flow runs Offline from the first frame.

**Late joiners** see the map read-only ("Connecting ..."), then go straight to the loading screen at
the master's city and arrive at the same yard, each on their own place of a ring round the arrival
point. A master already loading or in the world who finds the room's session unchosen writes their
choice back (log: "wrote this client's headquarters as the session again").

**Trucks are not networked yet.** Each player's truck is local until Phase 3: other players do not see
it, and a passenger seat in someone else's truck is not offered. The slot record (money as int 32) is
composed but not sent.

**The garage roof (fixed while merging).** At all 72 headquarters yards the arrival point and the first
stand are inside garage hall prefab 280, whose roof pieces at +6.92 and +7.38 m are on Default, a
drive-surface layer (probe 2100's sweep). Every ground ray started 40-60 m up and took the roof, so
the player arrived on the roof and the chosen truck was parked 7.4 m up. The part tests passed without
noticing: their own journey test logged "arrival done at Unity (-3.2, 7.4, 1.5)" and "own truck ...
Unity (0.00, 7.47, 0.00)".
- `EtsSession.FindSpot`'s open-sky pass now refuses a first hit more than 1.5 m above the arrival's
  height; the under-roof pass (from 2.5 m) finds the floor when no place has open sky. A last pass keeps
  the old rule for a yard where neither finds ground, and the arrival line then says "ON THE FIRST
  SURFACE FROM ABOVE".
- `EtsTruckController.PlaceOnGround` looks from 3 m above the asked height first, then from 60 m as
  before. Every caller passes a map height (a stand, a depot bay, the last safe spot), so depot bays
  under canopies are fixed too.
- `EtsTruckSeat`'s fallback arrival point looks from 2.5 m first, then 50 m.
- The same journey test on the merged tree: "arrival done at Unity (-7.6, -0.1, -1.9) ... (ring place
  10 of 17)" and "own truck ... Unity (0.00, 0.09, 0.00)".
- Not changed: the lateral places beside the stands still refuse ground under a roof, since the hall's
  walls are on Default and the stand's box check does not see them. Moving building pieces to the
  Environment layer, as `EtsLayers` intends, is a later phase (bridge decks inside prefabs must stay
  drivable).

**Other merge fixes.**
- **`ReadSii`'s cache is locked.** Worker threads read definition files too, and the shared
  Dictionary lost entries under concurrent reads (probe 2055: 2 of 30 rounds). With the lock: 0.
- **The tablet's pages are built ahead only once the flow's caches are warm**, which happens under the
  loading screen. Before, 51-56 ms page builds could land while a player pointed at the HQ map.
- **The delivery report's XP bar** uses the delivery's own before and after, which stay exact while a
  pay is queued for a career that is still opening.
- **F8 on the HQ map** (the master only) closes the message box, then confirms the selected city: the
  career's last headquarters, else Szczecin. It is the way on if the client's mouse does not reach the
  world-space map, which only a client run can prove. Not Enter, which also ends a chat message. Log:
  "HQ stage: F8 - ...".

**From the parts** (details in the part reports):
- **Map (F).** Sectors are read on a background thread ("Read N sectors round ... (reader thread)").
  Loading spends at most 12 ms of a frame and streaming 4 ms; single slow items can still stall a frame
  for up to about 2 s while loading. A zero-rotation node no longer cuts a sector's node table short:
  Bremen's northern sector has 2,903 models instead of 212. The "Blended ground sample ... static-batched
  into ..." line replaces 4 read errors per load. The full-density tree field (Phase B) is left off.
- **Trucks (C).** Built in 1.5 ms slices with the reads on a worker thread: "Truck factory: ... Sliced:
  N ms over F frames, worst frame W ms (step)". Chosen trucks drive with the engine their page shows.
  The sun shield shows outside again. A spawn still has single frames of 4-15 ms.
- **Seats (B).** The seat, camera and wheel follow the player's own truck and the one they sit in.
  Replacing or despawning the own truck lets its driver out beside it. Getting into the watched truck
  keeps its placement count.
- **Tablet (E).** In a headset the truck is held while the Somnium tablet is open, since its trigger is
  the throttle; the HUD says "Your tablet is open, so the truck waits - tap B or Y to close it and
  drive". System > Quit presses the tablet's own Exit World.
- **Jobs (G).** The map index is read on a reader thread and finished a piece per frame, so the HQ map
  does not freeze while it runs ("Map index: 282 sectors in T s"). All 72 HQ cities get 3 offers.
  Free driving adds to "distance driven".

**Log lines to look for, in order:** "Network: session: the game's map is open", "HQ stage: the void
at ...", "HQ stage: player placed at ...", "HQ stage: map placed ...", "Flow: HQ catalogue: 72 cities
...", "Network: session: phase ChoosingHq ... master me" (or WaitingForMaster), "HQ: pointer on <City>
(<Country>) - mouse | VR ray", "HQ: confirmed ...", "Loading screen: shown", "Network: session: loading
the headquarters ...", "Flow: page caches warmed ...", "Network: session: arrival done at Unity ...",
"HQ stage: hidden after ...", "Flow: HUD master badge built", "Tablet: the ETS 2 button is cell 1 of
... inside the mask: yes", "Tablet: first open in the world - Company > Truck Preference", "Truck
Preference: confirmed ...", "Network: own truck: ... stands at yard stand 0", "Jobs: bound to own truck
...", "Jobs: 3 offer(s) at ...", "Profile: delivery ... saved gen N", "Flow: delivered ...".

**Checked offline, on the merged tree** (`scratchpad/phase2/L`): the three builds compile with 0
errors and 0 warnings, and the server API check passes on all 127 members. Every part's probes were
re-run with the same results (two timing checks needed a second run on a busy machine). The
play-mode suites pass: the game UI's 6 of 6 (the whole journey from the real bootstrap, now off the
roof), the seats' 21 of 21, the tablet's 25 of 25 and the jobs' 2 of 2 (an offer taken, delivered and
paid: "balance €3809, saved gen 2"). Probe 2114 keeps €4,617 and 177 XP across a rejoin. With the
flag set back to false the old paths still pass their tests.

**Not yet known, only the client can show it:** mouse clicks and the VR ray on the world-space map;
when the Bridge player first exists for the void teleport; where the loading screen sits in a headset;
the tablet injection on the desktop tablet; and every frame time. A visitor without the ETS2 Demo
installed stays in the empty void (Phase 3 shows them a message).

## Demo fixes (2026-09-23): sound, indicators, mirrors, sky, radio, local quick jobs

None of this has been heard or seen in the Somnium client yet. It is checked offline only: the three
builds compile with 0 errors and 0 warnings, and the server API check passes on all 127 members.

- **Sound (`EtsTruckAudio`).** It no longer uses `OnAudioFilterRead`, which the client never calls for
  world code. That is why there was no sound at all. The clips are built once with `AudioClip.Create`
  and `SetData` and played through ordinary AudioSources, the route Quake and AoM use. That covers
  engine layers at 640, 1200 and 1920 rpm (off-load and on-load), turbo, road, wind, horn, the
  reversing beeper, the relay tick and tock, parking-brake hiss, gear clunk, engine start and stop,
  and the cab door. The controller calls the start/stop, brake and gear sounds; `EtsTruckSeat` plays
  the door when the player gets in or out.
- **Indicators (`EtsTruckController`, `EtsTrailer`).**
  - Headset: right stick flicked left or right. Keyboard: `[`, `]`, and `F` for hazards.
  - They blink at 1.5 Hz and cancel themselves after a turn, using the game's `game_data.sii` limits.
  - Amber lamps light at the truck's corners, on the side repeaters and on the trailer, with green
    telltales above the wheel.
  - Other players see them: `EtsNet` packs `TruckBlinkLeft`, `TruckBlinkRight` and `TruckHazard`, and
    `EtsNetVehicle` applies them to the puppet.
- **Mirrors (`EtsMirrors`, `EtsTruckFactory`, `EtsTruckRig`).**
  - Mirror glass is now recognised when its material name has a " vcol" suffix. Before, that suffix
    made the lookup fail, so some trucks found 0 or 2 of their 4 mirrors.
  - The factory builds the mirrors once the truck is complete, and looks again as late parts arrive.
  - A passenger in another player's truck gets that truck's mirrors.
- **Sky (`EtsMapViewer`, `Ets/EtsSky`).** Before, the sky, fog and sun were set while the client's
  loader scene was active, so they were lost with that scene. They are now applied again when the city
  loads and when the world's scene becomes active.
- **Radio (new `EtsRadio`).**
  - It uses the game's `live_streams.sii` stations when there are any. The 1.61 demo's list is empty,
    so it falls back to 12 public https MP3 streams.
  - It plays them through `UnityWebRequestMultimedia` and a 2D AudioSource.
  - It is controlled from the tablet's Media > Radio page and the new row on the Drive page.
- **Local quick jobs (`EtsJobs`).**
  - Offers come only from the area the truck is in: its city, or depots within 1.5 km. That is up to 6
    companies, with 2 or 3 offers each.
  - Parking for 3 s in a new area refreshes the list.
  - A job never starts in another city.

**Log lines to look for:**
- "Truck sound: clips built in N ms (19 clips ...) (AudioSource route)"
- "Truck sound: playing <truck> (2D, in the cab)"
- "Indicator lamps on '...'"
- "Mirrors: 4 (close_mirror ...)"
- "Map: climate set again ..."
- "Render settings written to the active scene '...' (the world's)"
- "Radio: N stations (...)"
- "Radio: playing '<name>' (<url>)"
- "Jobs: quick jobs for the area '<city>' ..."

**Still open:**
- The truck falling through near Cambridge or Dover. Its put-back or the thin mesh collider is
  suspected, but it is not fixed.
- Whether the client allows https requests from world code, which the radio needs.
- Whether the client also uses the right stick sideways, for snap-turning.

## Details (2026-09-23): cab instruments, signs, traffic lights, poles, life, textures

The user asked for working cab instruments, the map's small details (telephone poles, traffic lights,
pedestrians, birds, animals) and correct textures. Four owners built the parts in parallel and the
integrator wired them together. None of it has run in the Somnium client yet. It is checked offline only:
- the three builds (`runtime.rsp`, `runtime_server.rsp`, `editortool.rsp`) compile with 0 errors and 0 warnings;
- the server API check passes on all 127 members;
- the owners' probes ran against the demo's own files (scratchpad `details/<owner>/`).

### Cab instruments (new `EtsInstruments`, `Ets/EtsInstrumentKit`; changed `Ets/EtsAnimation`, `Ets/EtsSkeletonPose`)

- **What the files say.** Each interior definition names an animated dashboard model and one `.pma` per
  instrument key.
  - Needle ranges come from `*_anim_min/_max`. On the DAF XF Euro 6 they are 0-160 km/h, 0-2500 rpm and
    30-110 C.
  - The needles and lamps are skinned: 4 bones and weights per vertex, and the weights sum to 255. The
    Volvo FH16 2012 has 2,139 vertices on more than one bone, so the dashboard cannot be cut into rigid pieces.
  - As built so far, the bind pose was wrong: the DAF's needles stood 16 degrees past zero and its lamps
    sat half-way out of the cluster.
  - The real cluster display samples `/vehicle/truck/share/dashboard.tobj`. That is a render target the game
    draws into, not a file.
- **Built.**
  - Each cab's kit is read on a worker thread and shared by every truck with that cab.
  - The moving pieces are drawn by SkinnedMeshRenderers over the skeleton, at most 1 ms of setup a frame.
  - Every frame, speed, rpm, fuel, AdBlue, air, coolant, blinkers, parking brake, ignition and the gear
    selector pose their bones, with no allocation. Posing all bindings costs 0.012 ms.
  - The display is drawn dark, showing the gear, speed, clock and fuel %.
  - `EtsAnimation` fix: the frame count is 16 bits followed by 16 flag bits. The Volvo FH16 2012's 36
    animations had been refused, its steering column included.
- **Wiring.**
  - `EtsTruckFactory.BuildInterior` calls `EtsInstruments.Mount` right after the dashboard is added to
    the rig, guarded, for own trucks and puppets alike.
  - `EtsIndicatorLamps`' two green telltale cubes above the wheel stay dark while the cab's instruments
    run, because the cluster now blinks its own arrows. The cubes come back if the instruments fail.

### Signs, traffic lights, poles and wires (new `Ets/EtsSemaphores`, `Ets/EtsSignBuilder`, `Ets/EtsDetailDriver`; changed `EtsMapViewer`, `Ets/EtsMapSector`, `Ets/EtsMapBuilder`, `Ets/EtsRunBuilder`, `Ets/EtsPrefabDescriptor`)

- **What the files say** (within 1.2 km of the Szczecin start, then of Cambridge).
  - Sign items: 167 and 206. Signs inside prefab descriptors: 190 and 172.
  - Traffic lights: 27 on 9 junctions, and 25 on 3.
  - Pole lines: 1 high-voltage line, then 25 wooden telephone lines and 1 more high-voltage line.
  - A sign stands on its node with the node's full rotation. Checked against the stored boxes, the median
    error is 3-4 mm.
  - A light's profile gives its model and its green/amber/red/red-amber timings plus a per-light offset,
    by country. Poland inherits Germany's 15/2/22/1 s with offset 20. The UK version uses the UK post.
  - The lamps are flare hookups whose phases come from `/unit/hookup/tr_light_flares.sii`. All 348 lamps
    face the traffic their light stops, and conflicting lights are never green together.
- **Built.**
  - Signs are placed on their nodes, with LOD culling. Only solid ones taller than 0.5 m get colliders.
  - Traffic lights cycle on a shared UTC clock, so every player sees the same colour with no network traffic.
  - Poles stand along each line, with sagging wires: one mesh a line, no collider.
  - Prefab details are queued and built at most 1.5 ms a frame. Every item is guarded and logged once per kind.
- **AI cars now stop at the lights** (integrator; `Ets/EtsLaneGraph`, `Ets/EtsTrafficSim`, `EtsTraffic`).
  - The lane graph has a host hook, `PrefabWithLights`. When a junction whose curves name a semaphore is
    placed, `EtsTraffic` registers its programmes with `EtsSemaphores.Prepare`.
  - The sim has a host hook, `Light`. `EtsTraffic.LightAt` answers it from `EtsSemaphores.State`, the same
    clock that drives the lamps.
  - A car stops at the start of the light's curve on red and red-amber. On amber it stops only if it can
    stop comfortably; otherwise it goes on. Once it has decided to stop, it keeps to that decision.
  - A car held at a junction's mouth gives up its reservation, so it never blocks the crossing way that has green.
  - A programme with less than 3 s of green, or more than 2 minutes of red, counts as no light. Any failure
    turns the lights off for the cars (logged once).
  - Both classes stay pure, so the offline harness can still run them.
  - Offline test (`details/integrator/probe/ProgramInt1`): 900 s round the start, 20 signalised junctions,
    the start plus the 3 nearest junctions as foci.

    | | Lights off | Lights on |
    |---|---|---|
    | Entries onto a light lane | 475 | 273 green, 20 amber, 3 red |
    | Junction entries | 3,033 | 2,639 |
    | Mean speed | 8.18 m/s | 6.48 m/s |
    | Cars at a red light | - | 3.1 on average |
    | Longest stand | 60.0 s | 61.8 s |
    | Stalls | none | none |
    | Mean step | 0.043 ms | 0.040 ms |

    The 3 red entries were all within 0.4 s of the red starting: cars that were already committed at the amber.
- **Not wired, on purpose:** the night blinking (`EtsSemaphores.GameClockMinutes`). The world's sky is a
  fixed morning, and the session clock runs on into the evening, so amber-blinking lights would contradict the sky.

### Pedestrians, animals and birds (new `EtsLife`, `Ets/EtsMovers`, `Ets/EtsLifeSkin`)

- **What the files say.**
  - All of the game's life is map "mover" items: 9,145 on the map, all of which decode. They are 5,424
    pedestrians, 1,854 animals, 743 birds and flocks, and 2,413 flags, turbines, boats and machines.
  - There are no walker items and no `walker.sii`.
  - The models are skinned. The walking speed is stored in the animation: man 1.12 m/s, woman 1.27,
    dog 1.36, cyclist 3.31.
  - Most mover `.pma` files carry a flag word, so they have their own reader.
  - The skinning is verified both on the CPU and inside Unity 6000.3.5f2, in a throwaway project.
- **Built.**
  - Templates are built over several frames, with file reads and texture decoding on a worker thread.
  - Clones come from a pool, one new clone a frame at most. Unity's legacy Animation plays them and is
    culled off-screen.
  - Movers follow the mapper's path at the item's speed.
  - The game's own lods are used at the game's own distances.
  - Limits: pedestrians within 180 m (40 at most), animals within 260 m (40), birds within 1.5 km (24),
    others within 600 m or 1.5 km (30). Shadows only within 35 m.
- **Wiring.** `EtsMapViewer.Start` calls `EtsLife.Attach(this)`, guarded. There is one per world, and it
  follows `EtsMapViewer.Current`.

### Textures (changed `Ets/EtsDds`, `Ets/EtsPixels`, `Ets/EtsTextureCache`, `Ets/EtsMaterialCache`, `Shaders/EtsMulti.shader`)

- **What the files say.** A census of all 66,723 textures and 72,019 materials in the demo:
  - 208 textures could not be read: 11 R16G16 normal maps on the DAF, Iveco, Volvo 2012 and Actros paint,
    and 197 RGB9E5 HDR skies.
  - Colour spaces and wrap modes were already right.
  - amod weathering (14,354 materials) blends by a separate mask and the vertex alpha, not by the layer's
    alpha. That alpha is a specular value averaging 15/255, so the weathering had all but vanished.
  - Baked materials carry their gloss in a specular map and a reflection mask.
  - oclu multiplies the colour by an ambient-occlusion map on the second uv set.
  - 29 of the 32 decal-shadow materials are BC4/BC1/BC3, which were never read.
- **Built.**
  - Every format in the demo now decodes, at every mip level.
  - All 32 decal shadows are drawn.
  - Baked materials get a per-texel gloss map (at most 128 px), read on a background thread and applied at
    most 1 ms a frame.
  - amod (mode 5) and oclu (mode 6) are drawn by the game's own formulas.
- **Shader (integrator).** The textures owner's `EtsMulti.shader` change is applied: `_AmodMask`,
  `_AmodFactors` and layer modes 5 and 6.
  - Unity's own compile has not run yet. The patched shader was compiled offline, against the installed
    URP 17.3 includes (`details/integrator/shader`), by both:
    - dxc (SM 6.0): every pass, desktop and stereo-instanced, with the shadow, additional-light, probe and
      fog keywords;
    - fxc (SM 5.0, `/Gec`, Unity's D3D11 path): every pass.
  - Every compile succeeded, with no warning the original shader does not also give.
  - `UnityPerMaterial` has the same layout in all four passes (96 bytes), so the SRP batcher still takes it.
- **Prefetch (integrator).** The amod mask is asked for with the other textures in
  `EtsTrafficModels.AskTextures` and `EtsTruckFactory.AskTextures`, so it is not read on the frame.

**Log lines to look for:**
- "Instruments: <truck> - N needles, M lamps, 1 displays (bound: ...)". Failures read "... cannot be decoded,
  the dashboard stays still" or "no cluster display".
- "Indicator lamps on '...': ... 2 telltales above the steering wheel (dark once the cab's instruments run)"
- "Details: N signs, N prefab signs, N traffic lights (N profiles, N lamps) ..., N power lines (N poles, N wires); queue N."
  - A second one appears when the queue drains.
  - Failures read "Details: <what> failed: ... (logged once; carrying on)".
- "Traffic: ... , N at red lights; worst frame ..." (every 10 s, on the master). "the traffic lights could not be read" means the cars ignore them.
- "Life: mover definitions read - 917 movers, 628 models, 313 animations"
- "Life: N pedestrians, M animals, K birds (+O other movers ...) shown; ..."
  - Expected once: "mover model 'flageusmasta' is not in /def/world/mover*.sii", plus 4 other flag kinds.
  - Problems read "Life: could not build ...", or "Life could not start".
- "material: two-layer shader has the amod/oclu modes (5, 6) ..." (the shader change landed), "material: baked gloss maps on", "texture: DXGI 35 (Rg32) uploaded as RG32".
- Should be gone: "DXGI format 35 is not supported", "decal shadow: not a one-channel texture".

**Still open:**
- Everything is unverified in the client. That covers the frame cost with all four parts on at once:
  each has its own budget of 1-1.5 ms a frame while building.
- Headlight, high-beam and retarder lamps never light, because the world has no headlights yet.
- Street lamps are not lit at night.
- Mover sounds, hand props and scripted machine actions are not done.
- People are in the same places for every player, but not in step with each other.
- About 30 colour textures stored linear are still read as sRGB.
- A truck that clips a lamp post or a traffic-light post stops dead, as it does against any solid object.

## Look (2026-09-23): distant trees and the game's lighting and fog

After testing in VR the user reported that some trees are not cut out correctly in the distance, asked for the
game's own lighting and fog, and said the gear-change sound was very loud. Two owners did the work and the
integrator wired it together. None of it has run in the Somnium client yet. It is checked offline only:
- the three builds (`runtime.rsp`, `runtime_server.rsp`, `editortool.rsp`) compile with 0 errors and 0 warnings;
- the server API check passes on all 127 members;
- `EtsLeaves.shader` compiles offline against URP 17.3 in every pass, including stereo instancing: dxc SM 6.0
  56 of 56, and fxc SM 5.0 `/Gec` 24 of 24 (`look/integ/shader`);
- no source files were added, so no new GUIDs. Backups are in scratchpad `look/orig/` (`trees_*`,
  `lighting_*`, `integ_*`).

### Distant trees (`Shaders/EtsLeaves.shader`, `Ets/EtsVegetationBuilder`)

- **Cause, measured on the demo's textures.** Two far-tree atlases have box-filtered alpha in their mips:
  - `trees_far1_fr.tobj` (set v2_16fr): 41.9 % of texels pass the game's 0.05 at mip 0, rising to
    59 / 70 / 91 % at mips 5 / 6 / 7;
  - `treewall_dense1.tobj` (set v2_49's 33 m tree walls): 72.7 %, rising to 84 / 88 / 100 %;
  - their share at alpha >= 0.5 stays at 41-42 % and 72-73 % in every mip;
  - the other leaf atlases keep a hard 0-or-1 alpha down their mip chain.

  The shader also raised alpha by (1 + mip / 4) before the 0.05 test. So far cards were cut out as solid
  rectangles: a far tree 8 px tall covered 100 % of its card against a true 48 %.
- **Fix.** The mip alpha boost is removed. The cutoff is the game's 0.05 at full size or magnified, and rises
  with the sampled mip level to `_CutoffFar` 0.4 at `_CutoffMips` 2. These two are new per-material properties
  in `UnityPerMaterial`. `LeavesClip` runs the same rule in Forward, ShadowCaster, DepthOnly and DepthNormals, so
  forced depth priming and both eyes cut the same pixels. On the CPU against the truth, far trees now come within
  2-3 % and the ordinary trees within 1-5 %; the ordinary trees were 3-11 % too full before.
- `EtsMulti.shader` is unchanged, because no tree on the route uses it.

### The game's lighting and fog (`Ets/EtsClimate`, `Ets/EtsSky`, `Ets/EtsPostFx`, `EtsMapViewer`)

- **Climate.** Each sector's `.desc` names its climate. The start is "default" at 55.6 N. The game starts at
  10:00 on the solstice with summer time, so the sun is 44 degrees up at azimuth 116. The light blends sun profiles
  default.nice.14 and .15 (40 %) in the clear-sky slot. The climate is looked up again for the chosen city. It is
  fixed for the visit, because the session clock does not run.
- **Sun, ambient and exposure.** The sun is turned to its real direction, with shadow strength 1 as in the file.
  The ambient stays flat, as the game adds it. Exposure follows the light the way the game's adaptation does:
  0.0806 at 10:00, so the sun is 1.44 (it was 1.35).
- **Fog.** The game's fog pass clears everything inside `fog_offset` (139 m), then thickens as exp2. Unity's
  Linear fog from 139 m to 2183 m follows it: 8 % at 300 m and 42 % at 1 km, against the game's 10 % and 41 %.
  The colour includes the average share of `fog_color2`. Exp and exp2 fits are also computed. The far clip is
  now 2383 m.
- **Post.** A global Volume "Ets Post" under the map viewer (layer 0, priority 100):
  - Neutral tonemapping;
  - the file's saturation (+7.1);
  - contrast 10;
  - bloom 0.095 at quarter resolution with 3 iterations.

  Its weight is `EtsPostFx.PhaseWeight`, which `EtsFlow` sets: 0 in the HQ stage and loading screens, 1 in the
  world. The client's cameras have post on, HDR on and the Default volume mask. The tablet's layer 9 is drawn
  after post, so it is not tone-mapped.
- **Fog needs the scene (integrator).** Graphics > Fog Modes is Automatic, which is the Somnium SDK's setting.
  So the bundle keeps only the fog shader variants of the mode the scene is saved with. The scene was saved with
  fog off (`m_Fog: 0`, mode Exponential Squared), and the last build's Editor.log shows Multi's Forward pass going
  16 -> 4 at built-in stripping (Decal and Leaves the same). So no fog has ever drawn in the client. **In Unity:**
  1. Open Lighting > Environment > Other Settings.
  2. Tick Fog, set Mode Linear (optionally Start 139, End 2183), and save the scene.
  3. Rebuild.

  The integrator's wiring in `EtsMapViewer` reads the scene's own saved fog once, when its render settings
  first take over (in `Awake`, or when the client activates the scene) and before anything writes them. If
  that fog is on, its mode is used, so a scene saved in Exponential Squared still fogs, with the fitted density.
  If it is off, a warning says so.

### Gear change

The lead lowered the gear-change clunk in `EtsTruckAudio.PlayGearChange` from 0.45 to 0.07 (about -16 dB).
It stays at 0.07.

**Log lines to look for:**
- "Fog: the world's scene is saved with fog ON, mode Linear - the bundle keeps that mode's fog shader variants ..."
  - The warning "Fog: the world's scene is saved with fog OFF ... NO fog can draw" means the scene fix is missing.
- "Climate 'default' (/map/europe/sec+0003-0004.desc, 55.6 N 18.4 E) at 10:00 ... exposure 0.0806 ... Unity linear 139-2183 m"
- "Sky: /asset/skybox/s_nice_00/s_nice00_08_35_g.tobj 2048x512 ..., sun at 44.0 deg azimuth 116."
- "Look: post volume Neutral, ... contrast 10.3 ..., saturation +7.1, bloom 0.095 ... weight 0/1 ..."
- "Render settings: climate 'default' ... fog Linear 139-2183 m ... sun 1.44 ... shadow 1.00 ..."
- "Trees: distance cut-out live on SomniumEuroTruck/Leaves ..."
  - The warning "Trees: ... has NO distance cut-out" means the bundle has the old shader.

**Check in Unity:**
- `EtsLeaves.shader` reimports with no Console errors. A leaves material shows `_CutoffFar` 0.4 and `_CutoffMips` 2.
- After the scene fog fix, the rebuilt Editor.log shows "After built-in stripping: 8" for the Forward pass of
  `SomniumEuroTruck/Multi`, `Decal` and `Leaves`.
- In the world, the "Ets Post" Volume sits under the map viewer.
- Check the frame time in the headset with bloom on.
- If the leaves shader turns pink, restore scratchpad `look/orig/trees_EtsLeaves.shader`.

**Still open:**
- The tone curve is approximate: the game builds it in the exe. Tune `_postExposure` on the map viewer if the
  picture is too dark or too bright.
- Linear fog uses depth, not true distance, so there is slightly less haze at the edges of the view.
- The fog has no height term, and it does not brighten toward the sun.
- The built world ends at 1.2 km, where the game's fog is only about 50 %, so the edge may show.
- Tiny hard-edged trees thin a little: an 8 px `sc_lime` covers 38 % against a true 50 %.
- Map-placed `vegetation_model` trees have no far model. About 4 are near the route.

## Final pass (2026-09-23): smooth streaming, a larger world, buildings and terrain

After the 18:05 VR run, the user reported three problems:
- some buildings are see-through;
- some terrain loads in wrong;
- the view jitters while terrain loads.

They also asked for a larger loaded area and smoother loading while driving. Three owners worked on this in
parallel, and the integrator merged their work. None of it has run in the client yet. The next Player.log is the test.

### Streaming (`EtsMapViewer`, `Ets/EtsEngine`, `Ets/EtsMap`, `Ets/EtsTextureCache`)

- **Cause.**
  - Textures were read on the main thread. Reading and unpacking one on the frame takes 10.8 ms median and
    224 ms at worst (probe `Program5550`, run at the log's start). The 83-336 ms stream items and the 1179 ms
    `prefab 11bus` in the log were each the first use of a texture.
  - The world stopped at 1200 m, but the fog ends at 2183 m.
  - If one builder threw, the stream coroutine died and nothing more loaded for the rest of the visit.
- **Reads on a worker.**
  - A new thread, "Ets map prefetch" (an `EtsWorker`), reads everything an item needs before the item is built:
    models, the look's material files and their textures, decal shadows, prefab descriptors and terrain brushes.
  - The textures it asks for follow the same rules as `EtsMaterialCache.Build`.
  - Plants are read ahead too, but items do not wait for them.
- **Budgeted building.**
  - The main thread only uploads and builds, within `_streamBudgetMs` (2.5 ms a frame).
  - The budget rises to the scene's `_streamFrameMs` (4 ms) while ground within 600 m of the truck is missing.
  - The range scan, static batching and removals are spread over frames.
  - If an item's reads are not in after 10 s (8 s during the load), it is built with main-thread reads, as before.
  - A builder fault now costs only that item.
- **Road ahead first.** The queue is ordered by distance to a point `_leadSeconds` (6 s) ahead of the truck.
  Ground items and items in front of the truck move up the queue.
- **Larger world.**
  - Large items are built out to `_farRadius` (2150 m): roads, terrain, prefabs, house rows, and models 8 m tall
    or 25 m across.
  - Small items stay at `_streamRadius` (1200 m).
  - Each item is removed 450 m past its own build radius.
  - The initial load still covers 1200 m, so load time does not grow. The outer ring streams in after arrival.
  - There are 14-22 % more items (probe `Program5551`).
- **Scene.** The scene is unchanged, so the new fields keep their code defaults. Turning `_prefetch` off runs the
  old path.

### See-through buildings (`Ets/EtsMaterialCache`, `Ets/EtsModelBuilder`, `Ets/EtsMapBuilder`, `Ets/EtsRunBuilder`)

Three causes, measured round Szczecin, Cambridge and Dover (probes `ProgramBld1`-`6`):

1. **Windows.** `eut2.window` is opaque in the game: a picture of a lit room with reflections on top. Here it was
   drawn as glass at about 10 % opacity, so every window was a hole into the hollow building. Windows are now
   drawn solid.
2. **Alpha test.** All 727 game effects with an alpha test use 0.05, but this build used 0.4. That threw away
   161,659 m² of drawn surface: wall panels, trims, sheds and silos. Cut-outs now clip at 0.05. Non-over decals,
   flares and the leaves fallback stay at 0.4.
3. **Glass.** The game lets tint² × (1 − opacity) through, about 34 % for common building glass. This build let
   85 % through. `EtsMaterialCache.WithGameGlass` now draws map glass the game's way, through
   `EtsModelBuilder.Options.GameGlass` (prefabs, map models, runs). Trucks and traffic are unchanged: their cabs
   have inward-facing glass, and the game's formula would darken the driver's view.

Ruled out: mirrored copies, two-sided materials and winding. Unity draws mirrored models correctly, including in
static batches.

### Terrain and trees (`EtsGroundFill`, `Ets/EtsVegetationBuilder`, `Ets/EtsTerrainBuilder`, `Ets/EtsPrefabTerrainBuilder`, new `EtsVegetationStream`)

- **Cause of the jitter.** The old fill was one 2.2 km mesh, measured again every 300 m (about 463,000 rays) and
  swapped with its collider in one frame. Each swap also set off a 14,400-ray ground census in `EtsFrontEnd`.
- **Cause of terrain loading in wrong.** Fill sheets were measured before the terrain under them had streamed in,
  and all of them moved at once at the next rebuild.
- **Fill in 250 m tiles.**
  - Tiles are built nearest first, within 1 ms of work a frame (4 ms while the session loads). Each tile swaps
    on its own.
  - The terrain builders report the ground they build (`EtsGroundFill.NoteGround`). The tiles under it are
    measured again 1.5 s after streaming settles there.
  - Neighbouring tiles share their edge heights, so the joins close.
  - The census now runs once per new centre, not after every rebuild.
- **Fill reach.** The fill now reaches `FarRadius` (2150 m). The integrator applied the hook in `EtsMapViewer`:
  `fill.Extent = _radius * 0.9f` became `fill.Extent = FarRadius`.
- **Trees.**
  - `EtsVegetationStream` (GUID `5a5b5c903ef247d1900fcd15239607b7`) plants trees within 1200 m, a little each
    frame (1 ms), nearest first. It is added to the map viewer's object at runtime.
  - Each tree stands on its own item's ground, never on the fill.
  - Trees beyond 1600 m are switched off.
  - The random draws are unchanged, so the same trees grow in the same places.

### Integration

- `EtsVegetationStream.cs` was added once each to `runtime.rsp` and `runtime_server.rsp` (backups
  `*.bak-final`), and the fill-extent hook was applied.
- No scene edit was needed. The optional explicit-values edit was skipped because it changes nothing.
- No shader changed in this pass.
- All three builds compile with 0 errors and 0 warnings: `runtime.rsp` (editor-like), `runtime_server.rsp`
  (server-like) and `editortool.rsp`. The server API check reports "all 127 Fusion/Somnium members are proven".

**Log lines to look for:**
- `Stream: large items (...) to 2150 m, small ones to 1200 m ...; reads on a worker thread.`
- `Built ... prefetch on a worker thread: N reads asked, W items waited for them, 0 built without them after 8 s.`
- `Streamed N items in, M out over 5.0 s; ... worst build frame X ms (budget 2.5 ms); ... slowest item ...; heap H MB, Unity U MB.`
  Healthy: a worst build frame of a few ms, 0 items built without their reads, and a slowest item in single or
  low double figures.
- `material: eut2.window drawn solid ...`
- `material: map glass as the game draws it (first ...: lets 0.34 through, alpha 0.66).`
- `Runs: ... W window materials drawn solid ..., C cut-out materials at the game's alpha test 0.05 ...`
- `Terrain: ground fill streams in 250 m tiles out to 2150 m ...`
- `Terrain: ground fill ready round the focus in N s - ... slowest tile swap B ms.`
- `Terrain: vegetation planted within 1200 m of the focus, a little a frame: P plants ...`
- These should be gone: the `Ground fill: X % of a 2.2 km square ...` lines, and the census after each rebuild.

**Still open:**
- Building one large prefab (mesh, collider and static batch) is still main-thread work, so a single frame can
  still take 10-50 ms.
- Prefab lamps, signs and traffic lights (`EtsDetailDriver`) are not prefetched.
- The full 2150 m fill may take about 25 s after arrival, nearest tiles first. A tile can show a crack for about
  1.5 s while it waits to be measured again.
- There are no trees between 1.2 and 2.15 km. The fog mostly hides this.
- Parked cars on the map now have the game's dark glass, while traffic cars keep the clearer glass.
- There are 14-22 % more items and colliders, and up to 9 sectors are held in memory instead of about 4. Check the
  heap and Unity MB figures on the "Streamed" line.

## Polish (2026-09-23): cruise control, trailer hook, spawn marker, cab tilt, roads, car collisions, sounds

What the user asked for: cruise control on right stick up, hook or unhook the trailer on right stick down, a marker
over a newly spawned truck, better roads between towns, proper collisions with cars, the rest of the sounds
(ambience and so on), and the player tilting with the truck when it rolls. Five owners built the parts and an
integrator joined them.

### Controls (`EtsTruckController.cs`, `EtsTrailer.cs`)

- **The right stick while driving.** Every switch works only while the local player drives (`Driven` and
  `InputEnabled`).
  - Left or right toggles the indicators, as before.
  - Up toggles cruise control. Down hooks or unhooks the trailer.
  - Up or down must be within about 27 degrees of straight, so a sloppy sideways flick never drops the trailer.
  - A flick fires once. The stick must come back inside 0.35 before the next one.
  - A stick held while getting in must come back to the middle first. This was added by the integrator.
  - Clicking the stick sounds the horn, but only with the stick near the middle (inside 0.5). This was added by
    the integrator, so a hard flick that also presses the stick does not sound the horn. The horn now also needs
    `Driven`.
  - While the player is seated, the seat switches off Somnium's own teleport. That teleport is bound to the right
    stick up, so it cannot fire.
- **Keyboard.** C is cruise control and T is the trailer, the game's own keys. The camera moved to keys 1 (cab)
  and 2 (behind the truck). Somnium's 2D input binds none of 1, 2, C or T.
- **Cruise control.**
  - It engages at the current speed, only when driving forward at 20 km/h or more.
  - It holds the speed with the throttle and the engine brake. The accelerator overrides it while pressed.
  - It cancels on the brake, the parking brake, the switch again, a menu or the tablet, dropping below 10 km/h,
    getting out, a respawn or the network taking the truck.
  - The 20 km/h minimum is not in the game's data; it is the brief's number.
- **Trailer.**
  - It unhooks only under 2 km/h and stays where it stands, with its landing-gear feet down.
  - It hooks when the kingpin is within 1.5 m across and 0.5 m in height of the fifth wheel, the headings are
    within 45 degrees, and the truck is under 5 km/h.
  - Backing in under the trailer's nose, the truck and trailer boxes pass through each other.
  - The job stays with a parked trailer.
  - Other players see the feet down on a parked trailer (`EtsNetVehicle`, integrator).
- **Readouts.**
  - On the desktop status line: `CRUISE CONTROL 80 km/h`, and a 4 s message for each switch.
  - On the VR dash plate: the same messages, plus "Cruise 80" (`EtsVrPanel`, integrator).
  - The help texts in `EtsVr`, `EtsMenu` and the tablet list the new controls. The headset help now gives the
    stick its own line.

### Seat and marker (`EtsTruckSeat.cs`, new `EtsTruckMarker.cs`, GUID `ad97c20b0428468dbea92c5a4b188201`)

- **Tilting with the cab.** Seated, the body follows the cab's pitch and roll through a 0.08 s smoothing filter.
  Jumps over 45 degrees are taken at once. The heading is exact.
  - Getting out stands the rig upright again, turning it about the eyes.
  - The tablet's Options page has a toggle, "Seat: tilt with the cab" (`EtsTruckSeat.FollowCabTilt`, on by
    default). The integrator added it. It lasts for this visit only, like the tablet's own two options.
  - On a monitor the view does not change. Other players see the avatar's body upright, which is the client's
    choice.
- **The marker.** The game's orange map pin, with "Your truck" and the distance in metres, hangs over the
  player's own truck.
  - It is shown while the player is on foot and more than 7 m from the truck. It pulses for 6 s after a spawn.
  - It keeps a constant size out to 96 m and is hidden beyond 2500 m.

### Roads (`Ets/EtsMapBuilder.cs`, `Ets/EtsMapSector.cs`, `Ets/EtsMap.cs`)

- **Why they looked bad.** The roads between towns are legacy roads (all 20.6 km of Felixstowe to Cambridge, and
  651 km map-wide). Each was drawn as one flat strip of the German template asphalt, stretched from edge to edge,
  with no lines and no shoulders.
- **What they are now.** They are drawn as the game draws them.
  - The surface is the road's own cross-section material (`road_mat` at body +116): 18 m per u, 5 m per v, in
    bands from the look's `tex0`/`tex1`, shoulder and transition values.
  - Lines come from `lines.mat` (solid, broken, yellow) at the game's positions, to within about 3 cm.
  - Medians get the centre brush.
  - Railings are the model bent along the road, with end caps. Only the barrier's own upright strip is solid, and
    only outside the lanes.
  - Lamps and reflective posts stand along the road.
- **Integrator hooks.**
  - `EtsMapTerrain.ReadRoad` reads each side's two model slots (`EtsRoadSideModel.Read`) into `road.SideModels`.
  - The viewer's prefetch asks for each road's atlas, lines, railing models and side models, reusing two lists.
  - The viewer's report now adds the "Roads:" line.

### Car collisions (`EtsTraffic.cs`, `EtsTrafficModels.cs`, `Ets/EtsTrafficSim.cs`, `Ets/EtsLaneGraph.cs`)

- **Why the truck stopped dead.** The cars were kinematic, which PhysX treats as infinite mass. The log shows a
  loss of 26.1 kN s at 45 km/h. The car boxes also included the mirrors, 2.2 m wide.
- **What happens now.** One physics step before contact with this client's truck, trailer or a moving wreck, a
  car becomes a dynamic body of its own `vehicle_mass` (1285-2000 kg). The hit is then a real two-body contact:
  the car is shoved, spun or rolled.
  - A hit car becomes a wreck: its hazards blink, the cars behind queue or move over, and it drives on about 15 s
    after it rests with no truck within 8 m.
  - Each car has two fitted boxes, the body without mirrors and the cabin.
  - A car's boxes never switch on inside a truck.
  - The obstacle check sweeps the real boxes along the velocity, reversing included.
- **Network.** Wreck state and pose come from the traffic runner at 10 Hz.
  - A watcher's own hit reaches the runner through the new player-wire int 44, `AtTrafficHit`. `EtsNet.WriteOwnSlot`
    writes `EtsTraffic.HitReport` and `EtsNet.ReadSlots` hands every counted slot's value to
    `EtsTraffic.ApplyHitReport` (integrator). Int 44 lies past `Used`, so it is read straight from the network
    array.
  - A wreck's roll and pitch are not on the wire.

### Sounds (`EtsTruckAudio.cs`, new `EtsAmbience.cs` GUID `d0a56e14f4d54a96b68f3d017052089e`, new `EtsCarAudio.cs` GUID `1aa9b6d6802e4f599894c565aba0d896`)

- **How they are made.** They are synthesised, because the game's FMOD banks cannot be decoded here.
  - `EtsSynth` builds every new clip on one worker thread.
  - The main thread turns at most one finished clip a frame into an AudioClip (Create + SetData).
- **Ambience.** Six crossfading loops: wind, birds by day, crickets by night, town hum, motorway roar and water.
  - The mix is chosen from ground rays around the listener, the game's city areas, nearby fast traffic and the
    sun.
  - One-shots play at random around the listener: birds, an owl, gulls, and in towns horns, air brakes, clanks
    and a rare siren.
- **Traffic.** The nearest six AI cars within 90 m have engine and tyre sound. A car honks when the truck cuts in
  front of it and makes it brake hard.
- **The truck.**
  - The tyres sound different on gravel, dirt and grass.
  - Suspension thuds, crash knocks, trailer rattle and creak.
  - A coupling clunk and air-line hiss.
  - The engine-brake rasp.
  - A cruise-control chime. The integrator added the call in `EtsTruckController.SetCruise`, so every way on and
    off chimes.
  - A crash knock the moment this client's truck or trailer hits an AI car (`EtsTruckAudio.PlayCarHit`, called by
    `EtsTraffic` as the car turns dynamic; integrator). The velocity knock can miss a glancing blow now that cars
    give way.
- **In the cab.** Everything outside is damped to 0.35 in the cab with the interior view.

### Integration

- **Build files.** `EtsTruckMarker.cs`, `EtsAmbience.cs` and `EtsCarAudio.cs` were added once each to
  `runtime.rsp` and `runtime_server.rsp`. The backups are `*.bak-polish`.
- **Builds.** All three compile with 0 errors and 0 warnings: `runtime.rsp`, `runtime_server.rsp` and
  `editortool.rsp`.
- **Server API check.** It reports "all 127 Fusion/Somnium members are proven".
- **Shaders.** No shader changed in this pass. The hazard lamps use URP Unlit, which the indicator lamps already
  use in the client.
- **Not run yet.** None of this has been run in Unity or the live client.

**Log lines to look for** (all start `[SomniumEuroTruck]`):
- **Controls:**
  - `Cruise control on at N km/h.` / `Cruise control off: <reason>.`
  - `Trailer attached by the driver: ...` / `Trailer detached by the driver ...` / `Trailer not attached: ...`
- **Seat and marker:**
  - `Seat: cab tilt follow on (pitch and roll, smoothing 0.08 s).`
  - `Seat: following the cab's tilt (...)`
  - `Seat: body levelled on the way out (...)`
  - `Seat: cab tilt follow switched on|off on the tablet.`
  - `Truck marker: built (...)` / `Truck marker: shown, N m from your truck (the game's pin).`
- **Roads:** `Roads: N ribbons, M template roads (legacy looks drawn the game's way: ...)`
- **Car collisions:**
  - `Traffic: car boxes fitted to the bodies (...)`
  - `Traffic: car hit - <car>, <kg> kg (...) by this client's truck at X km/h; ...`
  - The 10 s report's `K wrecks (H hit, R drove on, X removed) ...`
- **Sounds:**
  - `Sound synth: N sounds (...) built on a worker thread ...`
  - `Ambience: playing (...)` / `Ambience: countryside|town|motorway|waterside|off the map (...)`
  - `Car sound: following the nearest N of M traffic cars ...`
  - `Truck sound: tyres on hard|loose|green|water|unknown ground for the first time ('<collider>')`
  - `Truck sound: knock (...)`

**Risks and open points:**
- **Controls:**
  - Opening a menu or the tablet cancels cruise control.
  - When hooking, the trailer can jump up to 1.5 m onto the fifth wheel.
  - The 20 km/h minimum and the coupling tolerances are not the game's numbers.
- **Seat:** a rolling view can cause motion sickness. It is on by default; the tablet toggle and the smoothing
  are the mitigation.
- **Roads:**
  - Barriers are now solid.
  - City roads (`look8`) get a lane line the measured prefab lacks.
  - Line brightness and draw-call cost have not been seen yet.
- **Car collisions:**
  - The switch from kinematic to dynamic has not been seen in Unity.
  - Wrecks slide on the default friction.
  - A walker can shove a wreck.
- **Sounds:**
  - The ground lookup works from names and paths, and the "tyres on ..." and "Ambience:" lines will show whether
    it is right.
  - The bump and knock thresholds are guesses.
  - There are up to about 12 more voices, at low priority.


## 2026-09-25: the game's look (exposure, clouds, ambient, fog, trees, grass) and VR cab controls + wheel haptics

The user said the lighting felt off, the sky strange and the small details (grass, bushes, trees) wrong, and asked for
hand gestures on the indicator stalk, hazards and parking brake plus subtle wheel vibration. Each part was measured from
the game's own files (the audits are in the session scratchpad: `wf1/lighting.md`, `wf1/vegetation.md`,
`wf1/cab_scout.md`; the owners' results in `wf2/cab_fix.md`). No scene change is needed for any of it.

### Lighting and sky

- **Exposure** follows the game's auto-exposure window instead of the old albedo key:
  `exposure = target_gray x clamp(1 / (0.1013 x (lum(diffuse) sin(el) + lum(ambient))), min_scale, max_scale)`.
  Measured results: 0.193 at Dover slot 8 (was 0.0806, 1.3 EV brighter), 0.264 at Szczecin slot 9, 0.181 for the
  overcast slot 3. Dover road view sRGB before/after: road 134,121,110 -> 179,169,147; sky 151,172,202 -> 200,214,222
  (the real game's u025 sky: 199,217,233).
- **Weather slot and clouds.** Each UTC day and HQ city picks a slot with the game's own weights
  (3,3,3,1,0,3,3,3,3,3,3,3,1), seeded from the HQ centre on the session wire and the UTC day, so every client sees the
  same sky. Cloudy slots load the two panoramas either side of the sun at 4096x1024 and crossfade them as the game does.
  The sun is a small disc with the game's halo (no bloom blob) and the lavender stripe under the horizon is gone.
- **Ambient.** Trilight directional ambient (down-facing 25 %, sun-averted about 77 % of the game's ambient_env), plus
  the game's near-field factor `dist_amb = 0.6 + 0.4 saturate(d*d/22500)`, lifted toward 1 at grazing angles, in
  EtsMulti, EtsDecal and EtsLeaves (global `_EtsAmbientNear`; w = 0 switches it off everywhere).
  Environment reflection intensity is 0 (URP was adding the ambient as a grey-blue film on grass and far ground);
  glass, water and chrome get a sky-only reflection cube (128, importance 0). The truck's own probe (importance 2) wins
  in its box.
- **Fog** fitted to the 2150 m far radius (was 1200 m), coloured by the view's angle to the sun in the world's shaders
  (`_EtsFogColor1/2`, `_EtsFogSunDir`).
- **Per-vertex colour** on about 2,800 map pieces (balcony rails, recesses) through the two-layer shader; anything with
  a normal, gloss, emission, env, glass, decal or lamp map is excluded. Switch: `EtsMaterialCache.VertexAoOnMapPieces`.
- **Mirrors and the route advisor** (LDR targets, no tone curve) get sun, ambient, fog and sky scaled by 0.64 around
  their render only, so they do not clip at the new exposure. World teardown no longer writes ambient/fog into the
  client's next scene.
- Files: `Ets/EtsClimate.cs`, `Ets/EtsSky.cs`, `Shaders/EtsSky.shader`, `Ets/EtsPostFx.cs`, `EtsMapViewer.cs`,
  `Shaders/EtsMulti.shader`, `Shaders/EtsDecal.shader`, `Ets/EtsModelBuilder.cs`, `Ets/EtsMaterialCache.cs`,
  `EtsGarageScene.cs`, `Ets/EtsTerrainBuilder.cs` (PairMaterial).
- Cost: one extra sky sample per pixel; cloudy slots use two 16 MB panoramas (about 28 MB more GPU memory) and about
  1.07 s of main-thread decoding when the climate is applied; a few ALU per pixel in EtsMulti/EtsDecal.

### Trees and bushes (new `Ets/EtsTreeField.cs`, GUID adb270de4c438413c25349a8073142d9)

- Planted at the map's own density (x1.00, was 0.2 with a cap of 60 per band) out to 2150 m (was 1200 m): about
  56-60k trees round Dover, 35k round Szczecin. Roadside belts read as solid walls like the game's loading photos.
- Leaves lit the game's way: plain saturate(N.L) sun on the bent crown normal (no wrap, no translucency), ambient
  not scaled by 2 x mask.x (that only scales the rain/snow ambient), the near-field factor above, the directional fog.
- Near models cast sun shadows to the shadow distance + 15 m (cards face the sun in the shadow pass, lookup pushed by
  mask.z toward the sun); sprites never cast. Near models fade to sprites over 240-300 m with a dither; hi-poly within
  the band's 50 m.
- A plant whose top hit is a road, pavement or yard within 3 m above its ground is skipped (all random draws happen
  before the test, so every client grows the same trees).
- **Why `_ETS_TREE_BUFFER`:** the project strips unused instancing variants (`m_InstancingStripping: 0`), and the
  leaves shader reaches the bundle only through the scene's `_leavesShader` field, so INSTANCING_ON variants would
  most likely be dropped silently. Every field draw goes through a plain `multi_compile_local_vertex _ETS_TREE_BUFFER`
  variant (matrices in a StructuredBuffer, `Graphics.RenderMeshPrimitives`), which the build always keeps. It needs
  `#pragma target 4.5` on the four leaves passes. In XR each draw asks for 2x instances and the shader collapses ids
  past the list, so each eye draws the list once whatever Unity does with the count. A throwing draw falls back to one
  GameObject per tree.
- Files: `Shaders/EtsLeaves.shader`, `Ets/EtsTreeField.cs` (new), `Ets/EtsVegetationBuilder.cs`,
  `Ets/EtsVegetationLook.cs`, `EtsVegetationStream.cs`, `Ets/EtsMaterialCache.cs` (leaves `_LodSelector`).
- Cost (lab, RTX 3080 Ti, 1080p mono): 0.5-1.3 ms GPU with trees on vs off; 183-315 draw calls, 0.12-0.23 ms submit a
  frame; the near sort about 1 ms four times a second. Caps: 5,000 plants a band, 250,000 in the field.

### Grass and detail vegetation

- The game's grass tufts, flowers and weeds (`.grass` density 35, max_distance 105; all 458 `detail_vegetation_def`
  units matched by brush base texture, `model[]`/`occurrence[]` weights), on verges and fields inside the side's
  detail band (5..100 m from the inner edge by default), never where the quad bit (byte3 & 0x10), road flag bits 28/29
  or terrain item bits 11/28 say no. Lit by the terrain normal, fade 89-105 m desktop / 52-68 m headset, no shadows
  cast. Layout from an integer hash of (item uid, side, quad, card): identical on every client.
- `EtsVegetationLod.DensityScale` 2.5 (0.875 cards per m2, calibrated against the loading photos u021/u024 because the
  files do not give the unit); card cap 30k desktop, 16k headset; cap-thinned cells are rebuilt when 500 cards free up.
- **Terrain item detail band (integrator, this pass):** `Ets/EtsMapSector.cs` terrain item side block now reads
  `DetailBandFrom/To` from the last 4 bytes of the 78-byte side (u16 / 10 at +74 and +76; the game's right_dvdm /
  right_dvd). Probes G1/G2 read 5..100 on 1,378 of 1,418 sides.
- Files: `EtsVegetationLod.cs`, `Ets/EtsDetailVegetation.cs`, `Ets/EtsDetailPatch.cs`, `Ets/EtsTerrainBuilder.cs`,
  `Ets/EtsPrefabTerrainBuilder.cs`, `Ets/EtsMapTerrain.cs`, `Ets/EtsMapSector.cs`, `Shaders/EtsLeaves.shader` (band 3).
- Cost: a grassy Dover verge is 19,806 cards (39.6k triangles), 37 cells; 0.4-1.1 ms per cell on a worker thread,
  at most 0.37 ms upload a frame. The headset GPU cost at such verges is estimated at 0.4-1.0 ms, not measured; lower
  `DensityScale` (e.g. 1.75) or `CardCapHeadset` if it hurts.

### VR cab: engine start, hand controls, wheel haptics (headset only; desktop unchanged)

New files: `EtsCabControls.cs` (GUID 07323b5f97b55de28845ba6f53090cf4) and `EtsWheelHaptics.cs`
(GUID eda2e23cf77d688c97d4f0ca111ffa05). Changed: `EtsVr.cs`, `EtsSteeringGrab.cs`, `EtsTruckController.cs`,
`EtsInstruments.cs`, `Ets/EtsInstrumentKit.cs`; integrator: `EtsTabletApp.cs`, `EtsNet.cs`, `EtsNetVehicle.cs`.

1. **Engine start.** In a headset the engine stays off after sitting in (dash plate: "Engine off - take the wheel or
   press a pedal to start") and starts on the first grab of the wheel, pedal (after the triggers are let go), stick or
   driving key. The revs follow `EtsTruckAudio.StartWave` (crank, catch at 0.85 s, flare at 1.2 s, settle by 1.7 s);
   no drive torque and no parking-brake release until the catch at 1.2 s. While off: no vibration, no torque, revs 0,
   engine loops silent; the tablet, mirrors, stalk, hazards, parking brake and horn work.
2. **Controls.** A free (not gripping) hand on a control makes it glow and ticks (0.18 for 30 ms, at most every
   0.25 s); only when the trigger can act. Stalk: trigger held, hand up 3.5 cm = one place toward right
   (off -> right, left -> off), down 3.5 cm = toward left; 7 cm takes a second step. Parking-brake lever and hazard
   switch: a half pull toggles. Zones: stalk 8.5-9.5 cm (moved up to 5 cm toward the driver when the stalk is more than
   10 cm behind the rim), hazard 8.5 cm, lever about 6-7 cm + 8.5 cm round the knob; a hand more than 3 cm nearer the
   rim than the control is on the wheel. Reach model (`vrcab2/reach/reach4_out.txt`): a palm 5 / 6.5 / 8 cm in front
   of the stalk is caught 100/100/100 % on the DAF XF Euro 6 and 100/83/20 % on the Scania S 2016.
3. **Trigger capture.** A trigger already held as brake or throttle never works a control. A press that has not acted
   is handed back only where that pedal is a brake at that moment (left while rolling forward, right while rolling
   back); a stalk press that switched nothing gives the brake back after 0.8 s still (under 1.5 cm) or when the hand
   leaves the zone.
4. **Vibration** (only the hand on the rim, only with the engine running): idle 0.06, revs up to +0.12 with the
   throttle, turning up to +0.10 scaled by the wheel's turn rate (25 % of it at a standstill, all of it at 80 km/h),
   start peak 0.25, cap 0.20; impulses of 0.12 s every 0.05 s. Strength (tablet Options page, "Wheel: vibration
   strength"): normal 1.0 -> strong 1.6 -> off -> light 0.6.
5. **Network.** The own truck's Motion flags now carry `EtsPlayerWire.TruckEngine` (2048, already reserved in the
   wire), and `EtsNetVehicle.Draw` applies it to a driven puppet (`if (s.Has(TruckDriven)) EngineRunning = ...`).
   `ApplyPuppet` also reads the engine from the revs (off under 60 rpm, start edge through 150 rpm), so both agree.
   A parked remote truck keeps idling as before.

Proofs: `vrcab/probe/VrCab1.cs` (vc1_out.txt), `vrcab2/probe/vc1_out.txt`, `vrcab2/sim/Sim.cs` (sim_out.txt, 14
scenarios), `vrcab2/reach/Reach4.cs`. Cost: a few microseconds a frame, no allocations (EtsVr.Present is now read once
a frame).

### Integration

- `runtime.rsp` and `runtime_server.rsp`: appended `EtsCabControls.cs`, `EtsWheelHaptics.cs`, `Ets/EtsTreeField.cs`.
- Builds: runtime, runtime_server and editortool 0 errors, 0 warnings. Server API check: "all 127 Fusion/Somnium
  members are proven". dxc (every pass and variant incl. STEREO_INSTANCING_ON): EtsSky 14, EtsMulti 56, EtsDecal 14,
  EtsLeaves 56 compiles, 0 failed.
- Render lab (the live folder, all nine views at Dover and Szczecin): nothing pink, black or missing; 0 shader errors and 0 script errors in both Unity logs (the exception list matches the untouched run). Dover slot 8 / Szczecin slot 9 cloud panoramas at 4096x1024, trees drawn through `_ETS_TREE_BUFFER`, 19,806 grass cards at the Dover verge. The lab's d_tree view now picks a grass cell as "the tree" and lands under the embankment; that is the lab's view picker, not the world.

**Log lines to look for** (all start `[SomniumEuroTruck]`):
- **Climate/sky:** `Climate 'default' (...) at 10:00 ... slot N (weighted pick ...); exposure 0.19xx = target_gray
  0.256 x ...`; `Sky: /asset/skybox/s_nice_0N/... crossfaded ... 4096x1024 ...` (bad sign: `Sky: procedural kept`);
  `Render settings: climate ... fog Linear ... ambient Trilight ... near-field x0.6 within 150 m ...; reflection
  intensity 0 ...`; `Built ... materials, ... (per-vertex colour on N pieces, M two-layer twins)`.
- **Trees:** `Trees: GPU-instanced field on: density x1.00 ... leaves drawn from GPU buffers (EtsLeaves
  _ETS_TREE_BUFFER, x2 instances for the eyes|mono)`; `Terrain: vegetation planted within 2150 m ... kept off a road
  or yard surface ...`; failure: `Trees: GPU-instanced drawing failed (...); falling back to one GameObject per tree`.
- **Grass:** `Grass: N cards in N cells (...) within 105 m of the eye (headset|desktop) ... density 35 x 2.50 / 100
  m2, cap N ...`; failure: `Grass: ... is not on the leaves shader`, `Grass: building a cell failed`.
- **Cab:** `Engine: off after sitting in (headset) ...`; `Engine: started by the first grab of the wheel (headset).`;
  `Wheel haptics: ready (left impulse yes, right impulse yes), idle 0.06 ...`; `Wheel haptics: starter rumble on the
  left hand ...`; `Cab controls: <truck> - stalk tip at ...`; `VR controls: ready in <truck> - stalk zone ...`;
  `[You] cab: stalk up -> right indicator (left hand).` / `parking brake set|released by hand` / `hazard lights
  on|off by hand`; `Wheel haptics: normal|strong|off|light (scale x.x).`; warnings: `Cab controls: <truck> - could
  not be found: ...`, `Cab controls: no highlight ...`.

**Untested / risks:** nothing here has run in a headset or the live client yet. Watch: the single-pass stereo path
of the tree buffer draws (far trees halved or in one eye only), `#pragma target 4.5` in the client bundle, headset
GPU time at grassy verges, the 1.07 s panorama decode during loading, the overcast slots 3 and 12 (still gloomy),
traffic and truck lamps that may look weak against the 2.4x brighter daylight, the mirror/advisor 0.64 scale
(reasoned, not measured), the cab zone sizes against the real hand and the vibration levels on Quest 3 through
Virtual Desktop, and a player who joins across UTC midnight (may see another weather slot until
`EtsClimate.SharedDay` is set from the session).

## 2026-09-25: holes

The user, after VR at the Dover and Prague headquarters: "There are still holes, is it because we only have the demo?"

**Not the demo.** Measured with the world's own readers (probe `N/holes/probe/ProgramH1`): within 2.5 km of both
map centres every sector square is in the demo (Dover 3 of 4: the fourth is the Channel; Prague 4 of 4), and every
item there resolves - Dover 155 roads, 38 prefabs, 1,287 models, 2,492 buildings, 710 curves; Prague 181, 68, 1,037,
2,620, 8 - with every prefab_desc, model_desc, .pmd/.pmg, building scheme and road look present. No DLC-guarded item
lies within 2.5 km of either (map-wide the demo's map holds 17,051 guarded items, all in the DLC regions). The only
"missing" builds are 288 / 106 vegetation-row buildings (tree rows, hedges, vines: `bld_model.22`, `.185`,
`dlc_fr_27/28`), whose plant models the demo ships; the world does not plant them (EtsRunBuilder hands them to the
scatterer, which has no code for them). The client log agrees: every build line says `0 skipped`. The holes were ours.

**Finding them.** Render lab survey (N/holes, `render.sh` sites `doverh`, `prague`): at the user's drive points from
`Player.log` (Dover 4, Prague 9), 8 compass views at cab eye height and 4 from 30 m up, each rendered once more with
the sky cleared to magenta and post off, so every magenta pixel below the horizon is a place where nothing is drawn;
rays through them are located, binned and named. Dover (cab) was clean apart from the sea's far edge. Prague was not:

1. **Terrain steps: a lake of sky beside the road.** Strips meet in plan but not in height. At the Prague HQ the west
   corner of prefab `road_sidewalk_end_4m` (002935DEE4604D6A) grows on profile14 and is 8-9 m up where the road ending
   at it (002935DE11704D6C, left side profile0, flat) lies at road level: a 9 m open step along 90 m. From the road
   the view went through the step, under the higher strip (whose underside is not drawn), into the void - a sky-blue
   "lake" 100-200 m from the cab: 6,370 and 7,838 hole pixels (1.3-1.6 % below the horizon) in two cab views at the
   roundabout the truck fell at, 16,573-19,797 from 30 m up at two other spots (p5, p9; pond-shaped). From above the
   ground is whole, so the ground fill (which fills only columns with no ground at all) never saw it. **Fix:**
   `EtsTerrainBuilder.AddSkirts`, called for every road side, terrain item side and bezier patch (`EmitQuads`) and every
   prefab corner (`EtsPrefabTerrainBuilder`): each strip's outer row and both end columns get a wall in the adjacent
   quad's brush where a ray just outside the edge finds terrain more than 0.4 m lower (down to 0.6 m into it) or
   nothing (20 m). Never onto a road, yard or model found below, never where the neighbour meets the edge; no collider,
   no shadow. Lab: Prague 5,507 skirt segments (1,933 edges left alone), Dover 4,918 (2,035).
2. **The median slit.** A legacy divided road's median lies 3 cm under the carriageways and ended exactly at their
   inner edges, so the step was an open slit: a 2-3 px line of sky along the far side of the median on every divided
   road (Prague motorway look1: 1,017-3,202 hole pixels a cab view). **Fix:** `EtsMapBuilder.BuildMedian` reaches
   `MedianTuck` = 0.6 m under each legacy carriageway (template roads unchanged: their inner edges are modelled).
3. **The ground fill's "-40 %".** Only a counting bug: `KnownPoints` counted the tile's 2-point measuring margin too
   (up to 225 known of 121). Nothing else reads it (`LastGapShare`, the ready line). Fixed in `EtsGroundFill.Measure`.

Before -> after, hole pixels below the horizon (1280 x 720): Prague cab views p5 6,370 -> 0 and 7,838 -> 0, p5 2,257
-> 0, p3 2,039 -> 8 and 1,539 -> 0, p7 3,202 -> 44, 2,468 -> 36, 2,061 -> 57, 1,017 -> 11; from 30 m up p3 10,411 -> 3,
8,592 -> 224, p5 16,573 -> 116, p9 19,797 -> 168, p8 5,100 -> 303, p4 2,756 -> 15. Dover unchanged (it had none but
the sea edge). Images: N/holes/before/b_* (and _void), N/holes/after1/a_*; crops N/holes/crops/p7_near_edge_*.

**Left:** the sea (and all ground) ends at the 2150 m stream radius - from 30 m up at Dover a thin band of sky under
the horizon, sub-pixel from the cab; the ground fill's 4.2 m sampling still misses thin wedges seen only from above
(Prague p2 beside the railway, p6 beside the underpass); single-pixel cracks along legacy road outer edges where the
asphalt and terrain are cut into different segments; the vegetation rows above. The truck's fall at the Prague
roundabout (`fell through the world at (-396, -99, 123)`) came after a 58 km/h knock on the island kerb; the lab's
2 m census finds no open cell within 60 m there, so it was pushed through, not a hole (truck code not touched).
**Risk:** a skirt is decided when its strip is built; a road or tunnel that streams in later under an open edge could
end up behind a wall (none seen at Dover or Prague). **Log:** `Runs: ... skirts under open terrain edges N (M edges
met or over a road).`

## 2026-10-04: truck damage

The user: "Add the truck damage." Everything below is the game's own rule unless marked. Code: `EtsDamage.cs` (glue,
HUD, delivery, repair, the tablet page `EtsPageService`) and `Ets/EtsDamageModel.cs` (Unity-free rules and state; the
offline probe in `DevTools/work/damage/sim` runs it against the archives, output `sim_out.txt`).

**How the game does it (1.61 demo files).** `/def/damage_data.sii` (`vehicle_damage_data`): `truck_damage_coef` and
`trailer_damage_coef` 0.0007 "per hit, impact speed squared modified by weights", `truck_to_trailer_dmg` 0.2 (either
way), `side_damage_factor` 6, `roof_damage_factor` 11; each hit shared over the parts - cabin 0.8, chassis 1.0, engine
0.5, transmission 0.3, wheels 0.15 as damage (repairable) and 0.04/0.05/0.02/0.01/0.01 as wear (only a new part fixes
it); trailer body 0.8, chassis 1.0, wheels 0.15 (0.04/0.05/0.01); `cargo_damage_ratio` 1.0; wear per km: engine,
transmission, wheels 2e-6 and 2e-7/2e-7/2e-5 as wear, trailer wheels 2e-6/2e-5. `/def/economy_data.sii`:
`cargo_damage_cost` 5 EUR and `cargo_damage_cost_factor` 4 % of the pay per 1 % of cargo damage, `exp_damaged_cargo`
5 XP and `exp_damaged_cargo_factor` 4 %, `reward_damage_level[]` 0.5/2/5/10/20/40/100 % -> Excellent ... Damaged,
Reckless. `/def/tutorial_hint_conditions.sii` `cond.damage.core_max10` (core damage at most 10 %) gates the "start the
engine" hints - above it the engine may be off by malfunction (locale `engine_malfunction` "Vehicle malfunction! ...").
HUD: the route adviser's damage group (`/ui/adviser_gps.sii`: truck, trailer, cargo icon + "0%" + bar), the
engine/transmission damage lamps (`/ui/adviser_tachometer.sii`, legend `adv_hud_lgd_engine/_transmission`). The
diagnostics/service screen (`truck.diag` in the Services category, `/ui/truck_service.sii`): rows engine,
transmission, chassis, cabin, wheels; columns Wear, Damage, Action, Cost; "Total to pay:", "Repair all". Repair
prices: each part's accessory `price` (DAF XF: chassis 4x2 26,160, cabin space 19,440, engine mx11_270 12,000,
transmission zf_12tx2420td 10,500, tyres 1,430 + 3,640). No damage look or deformation exists for a player's truck in
the files (the only "damaged" look is an AI bus paint), so none is drawn.
**Not in any file** (the exe names them, no def sets them): `damage_price_ratio` - this world charges part price x
damage (ratio 1.0); `warning_core_damage_treshold` - the lamps use the hint's 10 %; the malfunction rate (one roll per
20 s of driving, chance (d - 10 %) / 90 %, deterministic hash, engine stall >= 2.5 s or gearbox slip 1.5 s);
`dragged_trailer_damage_coef` is unused (a trailer is coupled or not here); integrity wear ("Maintenance") and the
police's damaged-vehicle fine are not built.

**This world.** Hits: the own truck's and its coupled trailer's `OnCollisionEnter` (never puppets or passengers);
impact speed = relative velocity along the contact normal; a contact pushing up from under the box is skipped; one
pushing down is the roof, one across a side; a dynamic body (a traffic car once hit, its `vehicle_mass`) weighs
m/(m_truck + m), anything else 1; colliders touched in one 0.3 s knock count once. Probe numbers (DAF XF, 7.5 t):
wall 10/30/60/90 km/h = 0.5/4.9/19.4/43.8 % to the hit -> HUD truck 0.3/2.8/11.2/25.2 %, repair 279/2,509/10,038/22,585
EUR; a 1.3 t car closed on at 60 km/h 2.9 % (HUD 1.7 %); a 10 deg scrape at 30 km/h 0.9 %; 1,000 game km of wear: tyres
2.2 %, engine 0.22 %. Delivery: 3 % cargo damage on 10,000 EUR pays 8,785 (Satisfactory), 15 % pays 3,925, 25 %+
nothing (never below 0 - whether the game charges below 0 is unknown). HUD truck figure = the parts' average (the
diagnostics' "Average damage"). Shown: the status line and the VR dash plate carry "Damage n%  Trailer damage n%  Cargo
damage n%" (+ ENGINE/TRANSMISSION DAMAGE above 10 %) while any is > 0; a hit that changes a figure says the full line.
Repair: tablet - Services - Vehicle Diagnostics, Repair all (`EtsProfileStore.Spend`), truck standing still; the game
repairs only at a service station or own garage, which this world does not have yet. A job's trailer is the company's:
its damage feeds the cargo and resets with the next trailer. Saved: `EtsProfileRecord.Damage`, key `damage=` written
only when the truck has any, format stays 1 (probe `DevTools/work/damage/savetest`: an undamaged record is byte-identical
to the old codec's; the old codec reads a damaged record (MAC ok) and drops the line on re-save; the new reads both).
**Log:** `Damage: rules from /def/damage_data.sii ...`, `Damage: ready on <truck> ...`, `Damage: the career's truck
condition loaded ...`, `Damage: truck hit <collider> (layer n) on its front/back|side|roof at v m/s ... -> +x %`,
`Damage: engine/transmission malfunction ...`, `Damage: delivered with cargo damage ... pay a -> b EUR`, `Damage: part
prices of ...`, `Damage: repaired the truck for n EUR`.

## 2026-10-04: speeding fines

User: "Add the speeding fines exactly like the game." Files: `Ets/EtsFinesRules.cs` (pure rules: EtsPoliceData,
EtsSpeedCamera, EtsSpeedRules, EtsFineMap, EtsFineJudge), `EtsFines.cs` (the world side), one line each in
`EtsTruckController.Configure` (`EtsFines.Ensure()`) and `EtsVrDash.Update` (the limit on the dash). Probes and output:
`DevTools/work/fines/` (`probe.cs` -> `probe_out.txt`, `conv.cs`, `allsigns.cs`, `xref.cs` for the exe).

**The game's numbers.** `/def/police_data.sii`: fine_amounts[3] "speeding (camera)" 200, [6] "speeding" 200; level
factor min(0.2 + 0.08 level, 1.0); over-speed progression x min(1.877 ^ (over / 2.777778 m/s), 12.5); rounding up to
20; police_nearby_fine_rate 2; offence_probabilty[3] 1.0, [6] 0.0 (speeding is only fined by a camera or by police);
offence_police_probabilty[6] 0.9 after offence_police_check_delay[6] 3 s; offence_message[3]
"@@offence_speeding@@<color value=@@clr_sel@@>" ("Speeding offence."), offence_icon[3] /material/ui/hud/speed_camera.mat.
No per-country fine figures exist (country/*.sui have none). Read off eurotrucks2.exe (demo 1.61; file offsets): the
fine is ceil(amount x level factor x (police near ? 2 : 1) x progression / 20) x 20, and an over-speed under one step
(10 km/h) takes no progression at all (0x82b1c6..0x82b2b0); the text is offence_message + " (+N km/h)" (only from a
step up) + "<br>" + the money as a loss (" (+%s)<br>%s", "%u%s%s", 0x82b390..0x82b431).
**Cameras.** A speed camera is a map SIGN item whose sign model (category "speedcamera" mostly, but 10 Dutch portal
gantries too - 63 of 8,551 sign models) has a model locator ("ml1".."ml3") hooked to `/unit/hookup/speed_camera.sii`:
speedcam.20 (trigger_distance 8, measurement_distance 20), speedcam.r (the same + reverse_direction), speedcam.toll
(measurement 0). The hookup name is in the .pmg string pool (header +88; locator +40). Not in the file: tolerance - the
speed_camera_u constructor sets 0.1 (and measurement 30) (0xa9fbb3; attribute table 0x2d9d6b0 names +0x130
measurement_distance, +0x134 tolerance; the vtable at rva 0x23051f8 holds the check at 0xaa0200). The check: limit x
(1 + tolerance) <= speed fires, the lane limit "for the vehicle" read at the camera ("[traffic] Unable to determine speed
limit for speed camera"), the direction test dot >= 0 negated by reverse_direction; the over-speed reported is
|speed| - the vehicle's current limit (0xaa0199). Direction measured (conv.cs, 9 of 9): speedcam.20's locator -Z points
against its lane's traffic, speedcam.r's along it - watched traffic moves along +Z (-Z when reversed).
Not decoded: what measurement_distance does (this world uses the 8 m trigger sphere and the speed there).
**Limits.** Truck rows of `/def/country/<c>/speed_limits.sii` (limit, urban_limit, max_limit; UK in mph: the country has
imperial_units), the lane's speed_class from its road look's lanes_right/left and `/def/world/traffic_lane.sii`, signs'
`traffic_rule` (`/def/world/traffic_rules.sii`: limit_N, *_t for trucks, limit_100_60 = 60 for trucks, limit_end,
city_start/end = urban + impl_reset, motorway/expressway start/end = impl_reset). A limit sign's node -Z runs with the
traffic it governs (196 of 198 near a lane). This world: a sign applies when the truck crosses it driving its way within
16 m; a sign figure is capped by the truck max_limit; junctions keep the last road's class; country = nearest city item
in the 3 x 3 sectors (EtsLaneGraph.CountryAt looks only in the lane's own sector and falls back to Poland - Dover's
motorway lanes come out Polish there). How the program ties signs to lanes and decides "urban" is not decoded.
**Probe (probe_out.txt, 8 km round each).** Dover 25 camera locators: 4 within 820 m (sign.1086/1130 UK hw cameras,
30 mph sign limits -> fire from 53.1 km/h), the French A16 cameras at 4.6-5.3 km (sign limit_110 capped to 90 for trucks,
70, 80), M20 cameras at 50/60 mph. Prague 11: all speedcamera_hw_cz at 80 km/h (truck max) -> fire from 88 km/h. Szczecin
33, none within 5 km: two Polish hw cameras at 5.5 km and 26 Swiss-model cameras (sign.ch_37007) in a German town at
6.8-8 km, 50 km/h urban (one 40). **Simulation** (Prague, the lanes into the 706 m camera, which passes the 1,039 m one
first): 70/80/87.2 km/h nothing; 88.1, 89.9, 90.0 km/h: two offences, €40/140/200/200 at level 0/5/10/20 (over-speed under
one step); 100 km/h €160/440/720/720 "(+20 km/h)"; 110 €280/800/1,340; 140 and 180 €500/1,520/2,500 (the x12.5 cap);
the same path driven the wrong way never fires.
**What the player sees.** Status line and VR dash plate: "Speeding offence. (+20 km/h) -€720" (the tail in clr_sel). On a
desktop also the game's HUD notification (`/ui/hud_hint.sii` group 570..870 x 792..860, txt.hud.background + txt.hud.icon
with the red camera icon) for 5 s - its colour is the hint handler's red kind (the handler has clr_notice, clr_sel,
clr_red; which an offence uses is not decoded). No flash, no sound: the camera models have no light part and no file
holds a camera flash (police_color_effect in color_feedback.sii is RGB-keyboard lighting). The limit in force: VR dash
"   limit 80"; desktop the advisor's sign (txt.adv_navi_spd, limit_sign.mat) at 1079..1111 x 223..255 while the advisor
is up. Money: `EtsProfileStore.Spend`; the game overdraws (bank_data.sii overdraft_duration 5 days), the store refuses,
so a refused fine is owed and taken when the balance covers it (not saved across visits).
**Not built:** police cars (the world's traffic has none: offence 6 is inert), prefab-descriptor signs/cameras, the
offence timers of the save (offence_timer/counter), mph display. **Log:** `Fines: police_data.sii read ...`,
`Fines: 63 sign models carry a speed camera ...`, `Fines: sector x,z gathered - n speed camera locators ...`,
`Fines: limit now 80 km/h (czech, motorway, sign traffic_rule.limit_80)`, `Fines: caught by sign.1011 ml1 ... -> €n, paid`.

## 2026-10-04: vehicle indicator lamps

The user: "The indicators should be on the vehicles, not orange squares, please do it the same as in the game." The amber
cubes (EtsIndicatorLamps, the traffic's wreck lamps) and the green telltale cubes are gone; the vehicles' own lenses light.

**How the game does it (read from the files, not guessed).**
- A lens is a model piece with an `eut2.lamp*` effect (1,662 pieces in the demo's 6,647 vehicle models). Its program's
  "defpost" pass (`/effect/eut2/lamp/eut2.lamp.rfx` -> `/effect/eut2/_shd/e8270e473ec3d07227ce490b07b385fd.glsl.vso` +
  `505acf8833b01726fe5018c637660f14.glsl.fso`, render state post_add) adds a glow: `texture_mask` is read through
  fract(texcoord1); the whole part of texcoord1.u picks a group (0-5); `lamp_color_array[group*4 + channel]` gives a colour
  per mask channel R, G, B, A; glow = sum colour x pow(mask, lamp_contrast) (alpha x 2.2) x (0.125 + 0.875 nz^2), nz the
  view-space normal's z.
- The colours: `/def/vehicle/truck/<truck>/lamp_setup.sii` (10 trucks) or `/def/default_vehicle_lamp_setup.sii`
  (named in the exe). `wire_composites[g]` is group g, its `channel_r/g/b/a_wire` the wire on each channel: 0 front left
  (R front_left_turn_signal, G high beam, B low beam, A front positional), 1 front right, 2 rear left (R rear_left_turn_signal,
  G brake, B reverse, A rear positional), 3 rear right, 4 middle (B daytime running, A side positional). `color_*` are linear
  HDR: turn signals 32, 8.194, 0.025 (Scania R/S 2016 front 48, 12.291, 0.0375), brake 35, 0.35, 0.025, reverse 11 x3.
- The masks pack the lamp types into the 4 channels (decoded and looked at: Scania `shape2_mask` R = the turn strip under
  the headlamp, G = high-beam reflector, B = low-beam reflector, A = the daylight strip). 266 of 272 lamp masks are sRGB
  BC3/BC1. Groups 0/2 lie on the vehicle's left (x < 0) in 1,111 pieces, 1/3 on its right in 1,165 (probe ind/Side);
  side repeaters are front-turn texels of groups 0/1 (Scania f_fender).
- texcoord1 is found through the pmg piece's texcoord mask (+8, nibble 1 = the uv set; now `EtsModelPiece.TexcoordMask`).
- The glow sprites ("flare_vehicle" hookups, `/model/flare/uniform_orange.pmd` + `/asset/flare/flare.tobj`,
  luminance_output 5000 / luminance_night 10, intensity from the engine) are NOT built - see below.

**Built.**
- `Ets/EtsLampSetup.cs` (pure): the lamp setup files. `Ets/EtsVehicleLamps.cs`: EtsModelBuilder notes every eut2.lamp
  piece; for a vehicle each lens gets one overlay mesh per wire this world drives (front/rear left/right turn, brake,
  reverse) - the lens's triangles of the groups that carry the wire, uv = texcoord1 less the group, vertex colour = which
  mask channels - drawn with the scene's decal shader in its new lamp mode (`Shaders/EtsDecal.shader` `_LampMode` 1:
  additive, the game's formula, colour x EtsClimate.AppliedExposure, fog-faded; all other decal materials draw as before).
  `lamp_contrast` is not in any file; 1 (the value under which "alpha x 2.2" is the sRGB curve).
- `EtsVehicleLampDriver.cs`: one object; keeps each truck's (own and puppets') and its coupled trailer's lamps with the
  truck: turn signals with Indicator/IndicatorLit (the controller also switches them on every blink edge), brake lamps with
  Brake > 0.05, reverse lamps with Gear < 0. Built the first time anything burns.
- Traffic (EtsTrafficModels/EtsTraffic): every car template's lenses (all LODs, each overlay in its lens's LOD level);
  Blink() lights a wreck's hazards, a turning car's turn signal (the sim's FlagBlinkLeft/Right: the nav curve's blinker
  within 40 m) and its brake lamps (FlagBrake), from the sim on the runner and the wire's flags elsewhere.
- EtsTruckAssembly: accessories' `defaults[]` are fitted when the save does not name the slot. The Scania R/S 2016 saves
  name no r_light: their rear bumper brings `r_light/stock.sii`, hung on the chassis model's r_light locator (turned 180).
  The Scania had no rear lamps at all before (only these two of 18 preview trucks were affected; f_fendr_chs also comes in).
- Lab: `RL_LAMPS=1 render.sh <code> <prefix> dover` builds the Scania S (puppet) and an AI a3 on the b_road spot and
  renders lamps_truck_off/_left/_hazard/_rear_left_brake and lamps_car_off/_rear_left_brake/_front_left
  (DevTools/work/indicators/r2_*.png, crops crop_*.png). Exposure 0.211: the turn strips glow amber, the brake lamps red.

**Log lines.** "Indicator lamps: scania.s_2016 - 5 lamp pieces lit the game's way (of 5 eut2.lamp pieces; overlays: left
turn 4 front + 1 rear, right turn 3 front + 1 rear, brake 1, reverse 1; colours from /def/vehicle/truck/scania.s_2016/
lamp_setup.sii, x exposure 0.211)." - the same for "trailer '...'" and "traffic car ..." (first 4 car types);
"Assembly: default r_light from r_bumper: ...". A warning "the scene's decal shader has no lamp mode" means an old shader.

**Not done / untested.** Not client-tested (only the lab, Direct3D11 desktop; single-pass stereo uses the decal shader's
existing stereo path). Flare glow sprites not built (their day/night luminance is engine-driven). Animated LED lamps
(`eut2.lamp.anim*`, 9 pieces, e.g. Scania r_light/led) stay dark. Beams, positional and daytime-running wires are not
driven (no headlights in this world). The blink rates are the world's (truck 1.5 Hz, cars 0.8 s), not the flare units'.
AI cars standing still show brake lamps only while the sim's FlagBrake is set (decelerating).
