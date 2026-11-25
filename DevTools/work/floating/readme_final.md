

## 2026-10-27: floating ground, tunnels, invisible colliders

User (VR, Austrian motorways, gantries "Salzburg / Graz"): "There are MANY gaps and terrain bleeding into the tunnels etc. Lots of
invisible colliders ... CHECK IT ALL". The map's only "Salzburg / Graz" gantries are at the A9/S6 junction Knoten St. Michael
(signs 42cf13e4744d0377 game (16822, 37, 18338), 42cf13e4b5cd0384 (17037, 41, 18249), 42cf13e40f4d0223 (14884, 49, 18582);
probe DevTools/work/floating/probe/t2). Render lab site `austria` (RL_CENTRE "gx,gy,gz", default the junction) reproduces the
screenshots there: flat khaki sheets with stair-stepped edges 20-90 m over the motorway (shot t5, from the carriageway; t3 from
above), khaki slabs hanging from them, panels standing over the forest.

**What they were (measured, RL_PROBE / RL_GROUND):**
1. **The ground fill's own sheets.** EtsGroundFill took the HIGHEST surface of every column as "the ground" and lent it to the
   gap columns round it. Beside a viaduct that is the deck; at St. Michael it is a hillside: terrain item 42f33d6841c50001
   (profile114 "45 degree down" x -0.6, 157 vertex records, 80-107 m over its start node; the game's own kdop says y 30.5..150.3,
   so this is the map's data) rises over the older town roads and ends in a crest in the air - every gap beyond the crest was
   filled at crest height. Sub-cells were then "tucked" down to the ground beside them by any depth: the hanging khaki slabs.
2. **Terrain skirts hanging into nothing.** AddSkirts (2026-09-25 holes) hung a 20 m curtain wherever ONE ray under the edge's
   middle found nothing; the survey found 89 % of all skirt segments (177,629 of 199,133 along the 45-point route) were such curtains (their foot has nothing within 2 m),
   and a ray at the middle missed roads / portals under the ends.
3. **Invisible colliders**, every collider type the world builds checked (survey part D): roads, medians, terrain strips, railing
   collision strips (the game's own centercol parts), model / prefab / run / sign pieces, ground fill. Two kinds have no visible
   counterpart and are not the game's intended collision:
   - "inv"-brush terrain (/material/terrain/invis.mat, eut2.none) is drivable invisible ground, kept since 2026-09 for the cells
     under the bay's dirt decals - but the mappers also paint inv wherever a strip pokes through a road or a viaduct deck:
     174 + 37 survey cells (16 m2) over roads with an invisible surface 0.3-4.75 m above the asphalt, e.g. 'profile112 right
     invisible' 0.3-3.6 m over the A9 lanes by Graz (road ger11) - invisible kerbs and walls on the motorway;
   - far-LOD parked-car groups (/model/parked_cars_groups/*_far*.pmd, *_veryfar*.pmd: dlc_it_386, dlc_no_712/714/718, ...) draw
     only their shadow decal and carry a 1.5-4 m eut2.none box per car; the game fills them with cars spawned through a
     hookup, we do not: invisible blocks of up to 29.9 x 4.1 x 20.2 m in car parks.
   The other undrawn colliders are intended: /model/wall/invisible.pmd runs (bld_model.30, the game's invisible walls), railing
   collision strips, building hulls (collpartN inside their drawn partN), flat gate plates.
4. **Terrain in tunnels.** The map has no tunnel flag on roads (none of the 32 road / body flag bits follows "covered by
   terrain", probe t17); tunnels are portal models (/model2/tunnel/*, tunnel_shadow_*) under terrain items. What stands inside a
   tunnel's clearance is map data (e.g. terrain item 42bf29e0b54f1f7b, a 17 m strip hanging 10 m down to the rails at the
   railway tunnel by (14684, 49, 18090)) or ours: skirts across the portal and fill seeded at tunnel depth - both gone (below).

**Fixes.**
- EtsGroundFill.GroundAt: every column still counts as covered by anything in it (never filled), but only its GROUND layer lends
  a height: one layer (all within 6 m) its lowest surface; several layers the lowest (valley under a deck, town under a hill,
  road under a flyover) - except terrain over a road / model surface (a mountain over a tunnel): no height. A road, yard or model
  surface with nothing under it lends only when no terrain in the tile's window does (the viaduct deck by Graz). A ground point
  more than 30 m over the lowest surface of a known neighbour (one 25 m cell: steeper than 50 degrees) is an overhang crest and
  lends nothing (DropCliffSeeds). A border sub-cell that would have to be pulled more than 4 m down to tuck under the ground
  beside it is left open, and no vertex is pulled more than 4 m (MaxTuck): no slabs, no lids. A road, yard or model surface with nothing under
  it standing more than 3 m over the ground the terrain round it gives (a deck over a gap) is filled UNDER at that ground's
  height instead (UnderDeck).
  Log (the "ground fill ready" line): "heights from the ground layer only: N covered points lent none (terrain over a
  tunnel), N overhang crests and N road or yard surfaces with terrain round them lent none, N border cells over much lower
  ground left open, N points under a deck filled at the ground's height".
- EtsGroundFill: a column holding a map model on the Environment layer (buildings, fences, and the Alpine mountain models
  such as /model2/rock/hills/mountain8.pmd, /asset/hill/alpine_mountain_ridge1) is covered too: nothing is filled through or
  over it (the fill saw only the drive surface and laid sheets 140-190 m over those mountains by Innsbruck and Salzburg). It
  lends no height.
- EtsTerrainBuilder.SkirtEdge: three rays (0.1 / 0.5 / 0.9 along the edge). No wall unless the middle finds terrain within
  SkirtDepth; no wall if any ray finds a road, yard, portal or model first; the wall reaches the lowest terrain found.
  Log: "... skirts under open terrain edges N (M edges met or over a road, K left open over nothing)".
- Invisible ground (EtsTerrainBuilder.DeferInvisible / PumpInvisible, also prefab corners): its collider is cooked once the
  map's own road and junction footprints round it are known (EtsRoadClearance, map data - streaming order does not matter;
  pumped from EtsGroundFill's loop), without the triangles whose middle lies on or up to 5 m over a road or junction surface.
  Log once: "Terrain: invisible ground ('inv' brush) gets its collision once the map's road footprints are known; N of its
  triangles so far lie on or within 5 m over a road or junction surface and stay without".
- EtsModelBuilder (NoneGuard): an eut2.none collision piece is built only inside the box of what its model DRAWS as a solid
  (decals, shadow casters, night-only pieces do not count; 0.5 m slack); a model that draws nothing at all keeps it (the game's
  invisible walls, gate plates, collbox blockers) - except a parked-vehicle stand-in (path with "/parked_": /model/parked_cars/physics,
  /model/parked_trucks/physics, the parked_cars_groups), whose car the game spawns or draws elsewhere. Counter:
  EtsModelBuilder.NoneCollidersDropped.
- EtsMapViewer.StaticBatching (default true): off only in the render lab's ground survey, which reads meshes one by one.

**Survey (render lab RL_GROUND="name,gx,gz,r;...", DevTools/renderlab/README.md).**
45 points of 1.4 x 1.4 km along the motorways
(39 at least 1.5 km apart on every Austrian motorway road ("ger hw ..." looks, country 1), 6 on Swiss ones; DevTools/work/floating/route45.txt;
the 53-point run crashed the editor at the 46th in the "before" run; the "after" run predates UnderDeck), before -> after:
fill floating (3+ m of air under it, or 5+ m over all terrain 25 m round it) 1,011,882 -> 166,798 m2 (fill drawn
19.2 -> 16.3 km2); fill over all terrain round it 82,416 -> 40,495 m2; skirt segments hanging in the air 177,629 -> 0
(of 199,133); skirt segments over a road 1,691 -> 85; cells (16 m2) with an UNDRAWN collider 0.25-4.75 m over a road
1,252 -> 302 (inv terrain 1,139 -> 247; parked-vehicle proxies 13 -> 0; the rest railing collision strips, the game's own);
items whose colliders stand with nothing drawn 1,432 -> 1,068 (left: invisible walls, collbox blockers, flat plates).
Files: DevTools/work/floating/lab/RB_austria_ground.txt (before), RA2_austria_ground.txt (after); compare with
DevTools/work/floating/cmp.sh. Worst spots left: fill 170-190 m over the mountain models by (2708, 178, 21067) and
(9408, 235, 18067) where the model is under the cell's middle but not its corners.
The four squares round the junction and the Graz viaduct (B -> A3, same metric for the fill): fill floating 67,623 -> 28,334 m2,
undrawn colliders over roads 218 -> 46 cells, skirt segments over a road 300 -> 11. Images (before B_shot_*, after A3_shot_*,
DevTools/work/floating/lab/): t5 = cab on the S6/A9 carriageway at St. Michael (the khaki sheets of the screenshots: gone),
t3 / m270 / m0 = from 80-160 m up (sheets, hanging slabs and 20 m curtains: gone; the hill item itself stays), cab80 = in
the A9 tunnel before the Graz viaduct (clean before and after), tw0 / tw180 = the railway tunnel by (14684, 49, 18090).

**Left (map data, not changed):** terrain items over town roads and junctions at St. Michael and Salzburg (the hill above; the
lab's "terrain over open road" 142 cells in the four squares round the junction / viaduct are drawn terrain 0.6-4.75 m over a
road surface - visible, as in the data); the railway tunnel's hanging strip; far floating where a hill's crest itself overhangs
nothing (the hill is drawn as the data says). Visible terrain over roads still collides. A 1-2 m slit between the two decks of
the S6/A9 bridge at (16701, 47, 18420) shows the void (the old sheet at deck height had closed it; it is narrower than the
fill's 4.2 m sampling). Hole pixels in the lab views rose a little where the curtains and sheets had covered voids (m270
18,389 -> 21,471, g90 33,046 -> 35,857 of 883,042 below the horizon). Not client-tested.
