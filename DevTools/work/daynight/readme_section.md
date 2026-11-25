

## 2026-10-14: time of day, weather, headlights

The user asked for a running day/night cycle and weather like ETS2, all players seeing the same, plus headlights for night.
Every rule below was read from the 1.61 demo's files (probes in DevTools/work/daynight: clim.exe dumps every sun profile,
hk.exe a model's locator hookups, topng.exe any texture); where the engine decides something the files do not state, it says so.

### What the game does (evidence)

- **Clock.** /def/initial_save game_time 600 (Monday 10:00); /def/env_data.sii day_in_year 172, summer_time 1,
  lamps_on_elevation 5, thunderstorm_probability 0.37. /map/europe.mbd holds time_compression_normal 19 and
  time_compresion_city 3 (the exe's names; floats at bytes 60/64): 19 game minutes a real minute, a day in 76 real minutes.
- **Profiles.** /def/climate/default/nice.sii and bad.sii: 34 sun_profile units each (elevations -40 .. 89, rising and
  setting), every field an array over the weather slots (nice 13, bad 10) with weights. Night profiles: diffuse is moonlight
  (0.0003, blue-grey), ambient 0.00002, exposure window min_scale 456 .. max_scale 1744 (day 0.37 .. 1.1), stars_color 0.007,
  starmap_color 0.001 (stars_texture /asset/skybox/shared/stars01.tobj: a 4096x2048 HDR celestial sphere, the Milky Way),
  moon_color 0.003, moon_halo_color, moon_halo_scale 2.4; skybox_texture at night is one of three shared gradients
  (/asset/skybox/shared/skies/clear_0x_*_g.tobj) and the clouds are only the skycloud mask. bad.sii: rain_intensity
  0.2 .. 0.94, rain_max_wetness 0.19 .. 1, lightning_intensity, rain_additional_ambient 5 (day) / 18 (night), a lightning
  mask per slot, fog 0.0037 from 12 m (nice 0.0009 from 139 m), cloud_shadow_weight about 0.9.
- **Sky passes.** eut2.sky.stars (opaque) under eut2.sky (/effect/deferred/rs/sky_over_premula.rs: one,
  one_minus_src_alpha; colour = panorama, alpha = skycloud mask): stars and moon show through 1 - mask. Stars:
  /asset/skybox/star_list.txt, 8,786 stars on a 35.6-unit sphere; its y axis is the celestial pole (the brightest stars'
  y/35.6 = sin(declination): Sirius -0.29, Canopus -0.80, Arcturus 0.33, Vega 0.63, Capella 0.72) and right ascension =
  atan2(x, z) + 90 deg; the star program (eut2.stars) draws 2.8 px points x intensity x colour x 2. Moon: moon.pmd is a
  0.18-unit quad (0.15 deg radius), moon.tobj a full moon; its position is the engine's.
- **Weather switching.** climate.sii bad_weather_factor 0.07 (default; arid 0.03), wetting_factor 0.1, drying_factor 0.01;
  the exe sets g_bad_weather_factor from it (game.log). How long a weather lasts is engine-only
  ("[env] Regular weather: ... weather %.1fmin" in the exe, no file).
- **Wet roads.** The deferred reflection pass (/effect/deferred/_shd/eea84f9cc27dd2d0e0b428104341d608.glsl.fso): r = the
  material's "reflection" (asphalt 1.0 in 206 of 239 road materials, ground mostly 0), W = wetness: diffuse x
  (1 - 0.55 W r (1 - specular)), specular power p -> 500 (1 + (1 - 0.4 r W)(p/500 - 1)), wet weight sat(5 W) r, plus the
  sky cube and a planar reflection by Fresnel.
- **Rain.** /material/environment/rain.mat (eut2.dif.flat.billb.over, rain.tobj) billboards; windscreen drops
  /def/vehicle/interior_glass_config_rain.sii (blobs_rain, 5000 drops a metre, wiping); ground splashes
  /material/decal/raindrops (eut2.grounddrops); wheel spray hookups *_rain.
- **Lamps.** Street lamps: a locator hookup "template.light.lamp.y.normal.fadelong.deg90#height:9.92;target:10.1"
  (/unit/hookup/lights/lamp/template.light.lamp.sii: lumen_hue_saturation 1600-8300 lm, hue 41 / sat 54 for y, range 33,
  outer angle 90/130), drawn by the engine as deferred spot lights with flares. "lum" materials carry luminance_night /
  luminance_output; eut2.lightmap.night is a lamp's fake light added to the light buffer. Vehicles: lamp_setup.sii wires
  high_beam 64, low_beam 25, positional 4, daytime 25; the truck's head_light accessory
  (/def/vehicle/truck/TRUCK/head_light/standard.sii: reflectors_offset, reflectors_distance, low_beam_color 6/5.4/3.2,
  range 80, angle 65; hi_beam 12/10.8/6.5, 180 m). Keys (controls.sii): light = L (off / parking / dipped), hblight = K,
  wipers = P. AI: config.cfg g_auto_traffic_headlights 1.

### What is built

- **Shared clock** (EtsDayNight.cs): EtsSession.ClockScale (new property) = 19, so the master's EtsSessionLogic writes each
  new game minute (session int 21, existing code); every client runs between writes at the same rate, clamped to the
  written minute. No session (editor, lab): a local clock. Weather (EtsWeatherClock in Ets/EtsWeatherTable.cs, pure): one
  game hour per weather period, bad with the chance bad_weather_factor from a hash of the session centre and the hour,
  turning over 20 game minutes; a bad run keeps one slot (bad.sii weights); wetness steps wetting/drying per game minute up
  to rain_max_wetness; thunder in thunderstorm_probability of bad runs. The day's nice slot: the viewer's pick for game
  day 0, a hash after. Nothing new on the wire for the weather.
- **Continuous light** (EtsClimate.Evaluate / ApplyContinuous, every 0.25 s in the world phase only): nice and bad profiles
  either side of the sun blended, then nice to bad by the weather; the same adaptation law as before (night: max_scale);
  the light from the moon (opposite the sun) below -5.5 deg; fog refitted; the ambient probe refit into reused buffers.
  Tables parsed once on a worker (Ets/EtsWeatherTable.cs). LegacyScale/OffscreenScale use the start's exposure
  (ReferenceExposure), so mirrors and the HQ stage stay right at night.
- **Sky** (Ets/EtsSky.cs, Shaders/EtsSky.shader): panoramas read and unpacked on a worker ("Ets sky decoder"), uploaded when
  the sun reaches a profile (about 4 ms each on the main thread, every 2-6 real minutes), prefetched ahead, freed after
  40 s unused; the bad pair crossfaded by the weather; cloud masks hide stars, moon and sun; stars baked once from
  star_list.txt into a 4096x2048 R8 celestial texture plus the Milky Way (stars01 mip 2), turned by latitude and sidereal
  time; the moon disc and halo; a lightning flash lights the bad slot's clouds. The reflection cube is recomputed on the
  worker every 20 s or when the exposure moved x1.5, the probe's intensity carrying the exposure in between.
- **Rain** (EtsRain.cs; Shaders/EtsDecal.shader mode 2): 6000 drops of rain.tobj in a 24x16x24 m box following the head,
  stereo-correct (oriented to the head centre), share shown = rain_intensity, lit by ambient x rain_additional_ambient,
  kept out of the occupied cab. One draw call.
- **Wet roads** (Ets/EtsWetSurfaces.cs): road/ground materials with reflection > 0 registered by EtsMaterialCache; base
  colour, smoothness and environment reflection follow the game's soaked law while wet.
- **Night lights**: street lamps (Ets/EtsStreetLamps.cs, hooked in EtsModelBuilder.Build): per lamp hookup a ground light
  pool (decal mode 3, cos^3 falloff, radius height x tan(outer/2), laid on the ground by a ray once the lamps are on) and a
  glow (decal mode 4); eut2.lightmap.night pools are built now (were skipped); night-only "lum" surfaces (luminance_night
  above 0, output 0) are dark by day. Pool level (EtsNightLights.PoolLevel 0.012 x exposure) and glow level (2.2) set by
  eye - the engine's lumen scale is not in the files - against base_19 / iberia_12 (DevTools/refshots/loading: the game's
  133 loading pictures, extracted from the install for comparison only).
- **Headlights**: EtsLampSetup wires 6-11 (high, low, front/rear/side positional, daytime); EtsVehicleLamps.SetHead;
  EtsVehicleLampDriver lights trucks and trailers from EtsTruckController.Lights / HighBeam (new fields; L cycles, K main
  beam, Shift+P wipers - P stays the parking brake); exposed lamp colours capped at 8 so a brake lamp stays red at night.
  EtsHeadlights.cs: the own truck's two spot lights from its head_light accessory (no shadows, only when dark), automatic
  dipped beams with the lamps or rain until L/K is used, automatic wipers in rain (1.4 s sweep) driving the cab's
  wiper_anim (EtsInstrumentKit signal Wiper). Wire: EtsPlayerWire int 9 bits TruckLowBeam / TruckHighBeam (declared,
  unused until now) and new TruckParkingLights (1 << 15), TruckWipers (1 << 16); puppets read them (EtsNetVehicle). AI
  cars: EtsTrafficModels.SetLamps adds dipped beams and positional lamps while EtsDayNight.TrafficHeadlights (lamps on or
  rain above 0.25); the traffic sim is untouched.
- **Tablet** Options: "Time: game clock / fixed 10:00" and "Weather: game's / clear / rain" (this player only, this
  visit), and a line with the time and weather; the desktop help lists L / K / Shift+P.
- **Lab**: RL_DAYNIGHT="name:minutes:weather[:badslot];..." renders each view at several times; every other run is held at
  10:00 clear (RL_TIME / RL_WEATHER change that). Renders: DevTools/work/daynight/lab/r*_*.png.

### Log lines to look for

"Day/night: running - time compression 19 ...", hourly "Day/night: hh:mm sun ... exposure ... lamps on/off; sky ...",
"Day/night: night - ... stars x..., moon ...", "Day/night: rain - ...", "Day/night: lightning - ...", "Sky: 8786 stars of
/asset/skybox/star_list.txt baked ...", "Rain: falling - 6000 drops ...", "Street lamps: N light sources read ...",
"Headlights: own truck's dipped beams cast ...", "Wipers: running ...".

### Not done / untested

Not client-tested (lab only: desktop D3D11, no headset, no session - the master's minute writes are an existing but never
exercised path). Not built: windscreen rain drops, ground splashes, wheel spray, puddle models, planar reflections, the
lightning bolts and the .mask lightning masks (TGA), moving cloud shadows (only their mean dims the sun), moon phases
(always full, opposite the sun), a VR light stalk, the head-light beam masks (plain cones). Thunder: EtsDayNight raises
LightningStruck for a sound, nothing plays it yet. The weather period (1 game hour) and the wetting/drying unit (per game
minute) are not in the files.
