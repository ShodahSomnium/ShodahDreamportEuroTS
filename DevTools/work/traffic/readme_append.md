
## 2026-10-14: AI traffic like the game

User: "AI traffic like ETS2" - the right country, trucks with trailers, buses, police, the other classes, the game's
proportions, better junction priority, all networked. Files: `Ets/EtsTrafficCatalog.cs` (rewritten), `Ets/EtsLaneGraph.cs`,
`Ets/EtsTrafficSim.cs`, `Ets/EtsPrefabNav.cs`, `EtsTrafficModels.cs`, `EtsTraffic.cs`, `EtsTrafficWire.cs`, `EtsFines.cs`,
`Ets/EtsFinesRules.cs`. Probes and output: `DevTools/work/traffic/` (`probe/*.cs`, `dump/` = the def files read, `img/`).
Originals: `DevTools/work/traffic/orig/`.

**How the game does it (evidence).**
- *Country.* The map's road and prefab nodes carry the country id (country_data `country_id`) in their flags: bits 8-15
  the country ahead of the node, 16-23 behind it (probe `nodes2`: round Dover, Calais, Prague, Szczecin 1,356 of 1,575 roads
  carry it; borders read "8,3 -> 8,8" Czech|Poland, "2,2 -> 4,2" Belgium|France; ids 10 uk, 4 france, 3 czech, 8 poland,
  5 germany from /def/country/*.sui). A lane takes its road's start node's 'ahead' id (else the end node's 'behind'), a
  junction its first node that has one; fallback the nearest city item in 3 x 3 sectors (city def `country`), then the
  nearest country_data `pos`. Offline: every lane within 1.3 km of Dover is uk now (was poland), Prague czech.
- *What spawns.* `/def/world/traffic_lane.sii` lane types name density rules (`traffic_rules_spawn.sui`): str_params[1] =
  vehicle type + tag filters, num_params = (from hour, frequency, count limit) triples. Motorway: s_car_h 30, s_trl_r 1
  (truck_light), s_trm_r 1 (truck_medium), s_truck_h 10, s_bus_h 2, s_police 1 ("police !customs") by day; local road
  s_car_l 8, s_trl_r 1, s_truck_l 3, s_bus_l 1, s_police 1. Fire/medical/truck_heavy appear only in urban rules no lane
  type uses. A spawn picks a rule by its count limit (the frequency's unit is not determined; Hour 12 slot), then a
  vehicle of that type.
- *Mix.* `/def/vehicle/traffic_storage_<type>.sii` (+ family files, e.g. `.daf_2021`): the table keeps car 89, police 63,
  truck_light 25, truck_medium 13, truck_heavy 5, truck 72 (= the game's "Loaded traffic vehicles of type 'truck': 72",
  game.log.txt), bus 17, plus the 24 semi-trailers of `traffic_storage_trailer_semi.sii` (= "Loaded traffic trailers of
  type 'truck_semi': 24"). Left out: variant[] children (spawned as their parent's variant), spawn_condition units (a
  named city), and units towing anything but a semi-trailer (caravans, car trailers). A vehicle's weight = its own
  spawn_ratio, overridden by `/def/country/<c>/traffic.sii` (last wins). No spawn_ratio = 1.0: the exe's
  traffic_vehicle_u constructor stores 1.0f at the attribute's offset 0x260 (eurotrucks2.exe 1.61 file offset 0x97f611,
  beside displacement_limit = -1; attribute table entry at 0x2d865e8); traffic_trailer_u the same at 0x208 (0x979c7d). This
  is why the UK names a3/audi_a6/bmw5 with 0. UK mix: 65 cars, 2 police (jaguar_xf.pol_uk, betrude_m5.22.pol_uk), 15 vans,
  5 lorries, 4 heavy, 72 trucks, 5 buses; Czech 67/2/15/5/4/72/5.
- *Trailers.* traffic_vehicle `trailer_chains` "truck_semi" (or "truck_semi low": tag filter) -> traffic_trailer_type
  truck_semi's storage, weighted by the trailers' spawn_ratio (curtain 4, reefer 2, dry van 2, ...). Coupling from the
  models' locators: tractor fifth wheel `hook_t*` (AI scania_r_2016 hook_t_0 at z 1.48), trailer kingpin `hook` (curtain
  ch_3 at z -5.07), trailer wheels `wheel_r_*` (z 1.54..4.16).
- *Speeds.* speed_limits.sii rows per vehicle speed class (traffic_vehicle_type.sii: car/police/truck_light = car,
  truck_medium/heavy/truck = truck, bus = bus), urban and max_limit; UK figures are mph (uk.sui imperial_units) and are
  converted now (UK motorway: car 113, truck 97, bus 113 km/h; was 70 "km/h"). max_speed per unit (trucks 90). Pull:
  engine_power / (mass incl. trailer_mass + cargo_mass) / speed, capped at the car figure 1.6 m/s2.
- *Junction priority.* Prefab nav curve flags bits 16-19 are a priority modifier (17,819 of 60,111 curves; probe `nav`;
  TruckLib NavCurve): on ger_r1_plus_1_to_r2_no_offset_tshape_tmpl the main road's straights are 12, the side road's turns
  6 and 8. Bits 5/6 = small / large vehicles (1,986 small-only, 880 large-only curves).
- *Police.* `/unit/hookup/police_car.sii` police_car.basic: trigger_distance 55, fov 60; every AI police model has a
  locator hooked to it (jaguar_xf police.pmd 'policecar', octavia_iii 'policeloc', facing the car's front).
  police_data.sii: offence 6 "speeding" offence_probabilty 0 (only police fine it), offence_police_probabilty 0.9 after
  offence_police_check_delay 3 s, police_nearby_offence_timer 15 s, police_nearby_fine_rate 2.

**Built.**
- Catalog: all of the above, read on the worker (133 ms; preparing a country's bounds + coupling locators 0.8 s the first
  time, ~1 ms for the next country). Vehicles carry kind, speed class, large, max speed, power, tags, trailer tags.
- Lanes: Country id, per-class limits (SpeedLimit car, SpeedTruck, SpeedBus), lane type (its spawn rules), and inside
  junctions every curve but the player-only ones, with Allowed (small/large) and Priority.
- Sim: spawn = lane's rules x lane's country (NeedCountry hook prepares a new country on the worker); Dress (size, class,
  max speed, power, total mass, trailer, Back); IDM pull per vehicle; gaps to the leader's Back (the trailer's tail);
  Untrail: a lane behind is given back only when the vehicle's tail has left it (cars too - a curve used to be released
  when the centre left it); large vehicles take the large curves; priority: a way's priority = its curves' highest
  modifier; it asks PriorityLead (1.5 s, NOT from the game) x priority/15 earlier, and a lower way gives way to a higher
  waiting one (one number per way, so no two wait for each other; the old 30 s first-come rule remains among equals).
- Wire: the vehicle index is 9 bits (low 7 where they were, top 2 in the two unused flag bits 29-30); no new ints. The
  trailer is not sent: EtsTrafficCatalog.TrailerFor(vehicle, slot*16 + serial) gives every client (and a new master) the
  same trailer.
- Drawing: a semi-trailer is its own pooled clone (root "Ets Traffic Trailers", so EtsCarAudio does not hear it as an
  engine) with its own LODs, kinematic interpolated body and boxes (collisions with its own tractor ignored). Its
  kingpin sits on the tractor's fifth wheel; its axle is dragged after it (tractrix: cuts the inside of a bend like a
  real trailer); its height is the road's under the axle from the tractor's last 24 positions. Same lamp path as the cars
  (EtsVehicleLamps via EtsTrafficModels.AddLamps; Blink drives the trailer's turn/brake lamps with the tractor's).
- Fines: EtsPoliceWatch (EtsFinesRules) + EtsTraffic.PoliceAround: a police car within 55 m whose front 60 degree cone
  (+-30) holds the truck, while the truck is 10 % over the limit (the camera's tolerance - the police's own is not
  decoded) for 3 s -> one 0.9 roll -> offence 6 fine (police_data formula, offence_message[6], speed_camera icon), then 15 s
  quiet. A camera fine with a police car within 55 m is x2 (police_nearby_fine_rate).
- Budget: at most MaxModels 64 distinct vehicle models built per visit (not from the game; the mixes hold up to 174
  vehicles, the old car pool 20); Prewarm builds the start country's 24 likeliest vehicles and 6 likeliest trailers.

**Measured offline** (`probe/sim.cs`, 300 s round one focus, 30 Hz steps, mono): Dover step mean 0.075 ms, p99.9 0.66
ms; Prague 0.088 / 0.83 ms (old: 0.072 / 0.72). Dover seed 1 spawned 78 cars, 6 police, 5 vans, 1 lorry, 17 trucks with
trailers, 8 buses; Prague 74/2/2/6/13/4. Tractor boxes overlapping (0.3 m in): 0 pairs (old 0). Stopped share 24-38 %
with priorities (12-35 % without, seed noise is large; the old car-only sim 18-21 %): trucks pull slower and main roads
now hold junctions longer.

**Log lines.** "Traffic: 284 vehicles and 24 semi-trailers in the table ... the start's mix: uk (id 10, mph): 168 vehicles,
65 car, ...", "Traffic: now spawning in france (id 4) ...", the 10 s report's "spawned N (cars, police, ... ; N with a
trailer), N junction give-ways by priority, N trailers drawn", "Indicator lamps: traffic car Ets trailer ...",
"Fines: police watch on - a police car within 55 m and 60 degrees ...", "Fines: a police car saw the truck speeding ...",
camera catches "... x2 (a police car near) -> €n".

**Not done / untested.** Not client-tested; nothing in a headset. Variants (traffic.transit_16.dhl, t_car_trans2..9 ...)
never spawn; trailer cargo accessories (the cars on a car transporter, the company paint jobs) are not built; caravans
and car trailers are not towed (those units are left out); the UK right-hand-drive variant (variant_uk) is not used;
the "lanes: 0.6 0.35 0.05" lane distribution is not used; fire/medical/escort/special/trams/trains are not spawned; the
spawn hour is fixed at 12 (no game clock to read yet); trailers are kinematic (a player's truck hitting one meets an
immovable box, as cars once did); a wrecked tractor's trailer follows it kinematically; police cars have no beacons,
sirens or chases (the game's police just fine). The trucks use the cars' engine sound (EtsCarAudio, not touched).
