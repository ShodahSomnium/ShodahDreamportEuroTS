

## 2026-10-14: sign texts

User: "There are sometimes signs with no text on them". Cause: the boards of the direction signs are blank in their
models; the game writes on them at run time and the world never did. Two systems, both now drawn
(new `Ets/EtsSignTemplates`, `Ets/EtsSignRaster`, `Ets/EtsSignFaces`, `Ets/EtsSignFacePump`, `Ets/EtsSignFaceRef`;
changed `Ets/EtsMapSector` (sign record), `Ets/EtsSignBuilder` (calls EtsSignFaces.Decorate, Details line)).
Probes and outputs: `DevTools/work/signs/` (near.cs, allsec2.cs, tally.cs, faces.cs, census.cs, shots.cs, lab/).

**1. Legacy boards** (6,642 signs on the demo map; round the Dover HQ every direction sign within 2 km - 91 signs).
- The sign record's boards are 3 tokens each: road, city1, city2 (`road_0=uk_a2 city1_0=uk_londn_l`).
- The model's locators hook `/unit/hookup/sign_*.sii` `sign_overlay` units: `sign.city1_0` = model_type city1,
  board_index 0; the `_small` ones add scale 0.85. The LOCATOR NAME is not the slot (sr_left_uk's locator "city5"
  hooks sign.city2_0) - the hookup is.
- Each slot shows `/material/overlay/<token>.mat` (eut2.retroreflective.decal.over over a ready-made picture:
  city_uk_dover_b.tobj 256x64 black "Dover" centred; _l / _bl left-aligned from x 10; road_uk_a2 128x128 yellow "A 2").
- Size: the exe names /model/sign/overlay/city.pmd, road.pmd, city_small.pmd, road_small.pmd - none is in the
  1.61 archives - so the quad is 1 cm per texel x the hookup scale, centred on the locator. Evidence: sr_left_uk's two
  lines are 0.68 m apart for 0.64 m pictures; board_straight_left_g_uk's 0.74 m; the A20 plate (hw_mark_g_uk, plate
  1.39 x 0.77 m) takes the 1.28 m picture's lettering (0.90 x 0.53 m) inside it.
- Census (census.cs): Dover 6 km 286 legacy signs, 392 overlay quads on 166 of them, 0 pictures missing; the other 120
  are traffic signs (barriers, bends) whose boards hold stale tokens and whose models have no overlay hookup - the
  game shows nothing there either. Prague 6 km: 447, 217 quads on 114. No navigation sign is left blank.

**2. Templates** (12,503 signs; Calais, the Czech distance boards at Prague, fuel price boards).
- THE SECTOR READING WAS WRONG: after the template name a templated sign has TWO arrays, not one: u32 area count,
  per area { u64 area token ("sb1", "ml11" - a model locator), u8 flags, flags&1: u16, flags&2: u64 board token
  (replaces the board bound there) }, then u32 override count, per override { u32 item id, u64 area token, u32 n,
  n x { u16 type, u32 index, value } }. The old code took the first count (non-zero on only 1,038 signs) as the
  override count, so every override (58,517 on the map: the names, numbers, distances) was lost. All 282 sectors: 12,236 of
  12,503 templated signs now end exactly on the next item header, the rest on an item the header test does not know.
- /def/sign: templates*.sii (12,053 sign_templ.* -> sign_template: model_name, boards[] bindings area -> sign_board,
  items[]), boards*.sii (12,540 sign_board_template: width, height, items[]), atlas/ (image_atlas: material,
  images_coords left right bottom top in px), frame/ (sign_frame_template nine-slice), /unit/hookup/sign_area.sii
  (625 sign_editable_area "sign.420x225.reflective": width, height). The area locator is the area's centre
  (sr_right_fr ml3 at x 0.298 y 3.011, its board piece 0.295 / 3.01); item coords are metres from the area's
  bottom-left; a bound board's bottom-left sits there too (cz_distance_v02: 4.4 x 3 m board in a 4.2 x 2.25 area,
  items at y 0.116..1.929 fit only that way).
- Items: text (font_face /font/sign/*.font, a 1-atlas MSDF face; font_size = the face's vert_span line box - the
  Czech board's 0.6 names sit in rectangles exactly 0.6 tall; line/letter spacing in metres), image (atlas, tint),
  frame (nine-slice), mileage (a text; the editor stores the computed name/distance as overrides). Colours 0x00BBGGRR,
  nil = the font material's aux[1] (white). Texts may be locale keys ("@@fp_cz_natural95@@" = 40.70, EtsLocale).
- Override attribute index (tally.cs over all 58,349 matched overrides, by item kind): text/mileage 0 text, 1 font,
  2 size, 3 colour, 4 opacity, 5 halign, 6 valign, 7 line spacing, 8 letter spacing, 100 mileage target token,
  102 variant, 104 uppercase; image 0 atlas, 2 colour, 3 opacity, 4 hflip, 5 vflip; frame 0 colour, 1 opacity.
- Drawn on the "Ets sign faces" worker (EtsSignPlanner.Plan -> EtsSignRaster: SDF glyphs, bilinear images, nine-slice
  frames, markup <br>, <sub scale=>, <sup>, <font xscale= yscale=>), at most 128 px/m and 512 px a side (multiples of 4),
  uploaded with mips and DXT5-compressed (2 ms/frame budget, EtsSignFacePump), shared by every sign with the same
  model + template + overrides (FNV key), released when the last one unloads (EtsSignFaceRef). Census: Dover 6 km
  69 template signs -> 160 faces, Prague 42 -> 72, 0 missing glyphs, ~10 ms a sign on the worker (first font ~300).
- Faces and overlays are quads in the locator's frame (face looks along the locator's -Z, text runs along its -X; the
  board locators are turned 180 degrees about Y), drawn with the world's decal shader, no shadows, in the sign's LOD group.
  Each quad owns its 4-vertex mesh: the map viewer destroys every non-shared mesh of an item it unloads, so one cached
  quad shared by all signs vanished from every sign as soon as the first one streamed out (the lab's first pass: text on
  the first shots, blank boards on the later ones).

Assumptions (not in the files): sub/sup without a scale = 0.7; a mileage DISTANCE with no override is not drawn (the
game's route search is not replicated); valign/halign override value 3 = centre.

Log lines: `Sign texts: legacy board overlays on (first: ...)`, `Sign texts: template faces on (first: ..., N ms on the
worker, N ms upload)`, and the Details line now carries `N sign overlays, N template signs (N faces, N shared, N live
textures)`.

Lab proof (10:00 clear; DevTools/work/signs/lab, before = this code with EtsSignFaces.Enabled = false):
`pairs_dover1.png` (HQ roundabout "London A 2", the port's tall board "London / ferries", the A20 board "Southampton
Plymouth A20 / Dover London", the A20 gantry), `pairs_calais.png` (HQ "Dover" + ferry, Calais "Port Est", the
roundabout diagram board, the "Terminal Ferry / CALAIS" gantry), `pairs_prague.png` (WROCLAW, Centrum, the Czech
distance boards "8 E55 DRESDEN 82", "10 E65 WROCLAW 224 / ML. BOLESLAV 34"). Still blank: the fuel-price digits in
the lab (their text is a locale word, @@fp_cz_natural95@@ = 40.70, and the lab never loads the locale; the client
does, EtsFlow), mileage distances without an editor override, signs inside prefab descriptors (no board data).
