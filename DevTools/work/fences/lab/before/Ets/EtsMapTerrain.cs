using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The painted quad grid of one terrain strip. The game has no heightmap: terrain is a strip
    /// beside every road, terrain item and prefab corner, shaped by a profile (a height curve
    /// across the strip in fixed steps) and painted quad by quad. Rows run across the strip,
    /// away from the road, one per profile step that fits the strip's size; columns run along it,
    /// one per 15 m on a normal road (three per 15 m on a high-poly one) and one per 16 m on a
    /// terrain item. Quad (row, col) is stored row-major, 4 bytes each: byte 0 holds the main
    /// material index (low nibble) and a blend material (high nibble), byte 1 the blend opacity
    /// and a corner colour, bytes 2-3 more corner colours and the vegetation setting.
    /// Layout after the TruckLib map library, checked byte for byte on the demo's version 898.
    /// </summary>
    public sealed class EtsTerrainQuadData
    {
        /// <summary>Brush materials, named by token: unit "terrain_mat.&lt;token&gt;" in terrain_material.sii.</summary>
        public ulong[] MaterialTokens = new ulong[0];

        public ushort[] MaterialRotations = new ushort[0];

        /// <summary>Brush colours, r g b a per entry.</summary>
        public byte[] Colors = new byte[0];

        public int Rows;
        public int Cols;

        /// <summary>
        /// Four bytes per quad, stored COLUMN-major: the quad at (row, col) is at index
        /// col * Rows + row. TruckLib puts it as "indexing begins in the bottom left corner in
        /// forward direction" - the file walks one step along the strip and then lists every quad
        /// across it. Measured on the demo's own sectors: over the 7,318 grids near the bay that
        /// are not square, neighbouring quads share a brush 86.8 % of the time read this way
        /// against 73.9 % read row-major, where a random shuffle of the same quads scores 64.8 %.
        /// Read row-major the brushes land on the wrong quads wherever the grid is not symmetric,
        /// which is what put stripes of the wrong surface across roads and verges.
        /// </summary>
        public byte[] Quads = new byte[0];

        /// <summary>Hand-moved vertices: column index, row index, then dx dy dz, five floats per entry.</summary>
        public float[] Offsets = new float[0];

        public int OffsetCount => Offsets.Length / 5;

        int Quad(int row, int col)
        {
            return (col * Rows + row) * 4;
        }

        public int MainMaterial(int row, int col)
        {
            return Quads[Quad(row, col)] & 15;
        }

        public int BlendMaterial(int row, int col)
        {
            return Quads[Quad(row, col)] >> 4;
        }

        public int BlendOpacity(int row, int col)
        {
            return Quads[Quad(row, col) + 1] & 15;
        }

        public static EtsTerrainQuadData Read(byte[] b, ref int p, int limit)
        {
            var q = new EtsTerrainQuadData();

            int materials = BitConverter.ToUInt16(b, p);
            p += 2;
            if (materials > 64) throw new FormatException("terrain brush materials " + materials);
            q.MaterialTokens = new ulong[materials];
            q.MaterialRotations = new ushort[materials];

            for (int i = 0; i < materials; i++)
            {
                q.MaterialTokens[i] = BitConverter.ToUInt64(b, p);
                q.MaterialRotations[i] = BitConverter.ToUInt16(b, p + 8);
                p += 10;
            }

            int colors = BitConverter.ToUInt16(b, p);
            p += 2;
            if (colors > 4096 || p + colors * 4 > limit) throw new FormatException("terrain brush colours " + colors);
            q.Colors = new byte[colors * 4];
            Array.Copy(b, p, q.Colors, 0, colors * 4);
            p += colors * 4;

            q.Rows = BitConverter.ToUInt16(b, p);
            q.Cols = BitConverter.ToUInt16(b, p + 2);
            p += 4;

            uint quads = BitConverter.ToUInt32(b, p);
            p += 4;
            if (quads != (uint)(q.Rows * q.Cols) || quads > 1 << 20 || p + quads * 4 > limit) throw new FormatException("terrain quads " + quads + " for " + q.Rows + "x" + q.Cols);
            q.Quads = new byte[quads * 4];
            Array.Copy(b, p, q.Quads, 0, (int)quads * 4);
            p += (int)quads * 4;

            int offsets = (int)BitConverter.ToUInt32(b, p);
            p += 4;
            if (offsets < 0 || offsets > 1 << 20 || p + offsets * 16 > limit) throw new FormatException("terrain offsets " + offsets);
            q.Offsets = new float[offsets * 5];

            for (int i = 0; i < offsets; i++)
            {
                q.Offsets[i * 5] = BitConverter.ToUInt16(b, p);
                q.Offsets[i * 5 + 1] = BitConverter.ToUInt16(b, p + 2);
                q.Offsets[i * 5 + 2] = BitConverter.ToSingle(b, p + 4);
                q.Offsets[i * 5 + 3] = BitConverter.ToSingle(b, p + 8);
                q.Offsets[i * 5 + 4] = BitConverter.ToSingle(b, p + 12);
                p += 16;
            }

            int normals = (int)BitConverter.ToUInt32(b, p);
            p += 4;
            if (normals < 0 || normals > 1 << 20 || p + normals * 16 > limit) throw new FormatException("terrain normals " + normals);
            p += normals * 16;

            return q;
        }
    }

    /// <summary>
    /// A band of vegetation on a terrain side: a vegetation set (unit "veg.&lt;token&gt;" in
    /// vegetation.sii, a list of "vegetmodel.&lt;name&gt;" models) scattered at a density between two
    /// distances from the road. 16 bytes: token, u16 density / 10, u8 high-poly distance, u8 scale,
    /// u16 from / 10, u16 to / 10.
    /// </summary>
    public sealed class EtsVegetationRecord
    {
        public ulong Token;
        public float Density;
        public int HighPolyDistance;
        public int Scale;
        public float From;
        public float To;

        public string Name => EtsToken.Decode(Token);

        public static EtsVegetationRecord[] Read(byte[] b, int p, int count)
        {
            var records = new EtsVegetationRecord[count];

            for (int i = 0; i < count; i++)
            {
                records[i] = new EtsVegetationRecord
                {
                    Token = BitConverter.ToUInt64(b, p),
                    Density = BitConverter.ToUInt16(b, p + 8) / 10f,
                    HighPolyDistance = b[p + 10],
                    Scale = b[p + 11],
                    From = BitConverter.ToUInt16(b, p + 12) / 10f,
                    To = BitConverter.ToUInt16(b, p + 14) / 10f
                };
                p += 16;
            }

            return records;
        }
    }

    /// <summary>One side of a road or terrain item: how wide the terrain is, which profile shapes it, how it is painted.</summary>
    public sealed class EtsTerrainSide
    {
        /// <summary>Up to three vegetation bands, from the road side or terrain item.</summary>
        public EtsVegetationRecord[] Vegetation = new EtsVegetationRecord[0];

        /// <summary>Unit "terrain.&lt;token&gt;" in terrain_profile.sii: a step array and a height array.</summary>
        public ulong ProfileToken;

        public float Coefficient = 1f;

        /// <summary>Width of the strip in metres from the road edge. Zero means no terrain on this side.</summary>
        public float Size;

        /// <summary>
        /// A legacy city road's sidewalk on this side, metres (0 on rural roads and template roads):
        /// from the road header's flags, see EtsMapSector. The terrain starts outside it.
        /// </summary>
        public float SidewalkSize;

        /// <summary>The sidewalk's "sidewalk.&lt;token&gt;" unit (EtsMap.Sidewalk), from the sector's .data road record.</summary>
        public ulong SidewalkMaterial;

        /// <summary>0 none .. 3 strong, from the item flags.</summary>
        public int Noise;

        public int Transition;

        /// <summary>Null until the sector's .data file has been read, or when the strip has no data.</summary>
        public EtsTerrainQuadData Quads;

        /// <summary>
        /// Whether the game grows detail vegetation (grass tufts, flowers) on this side where its brush has a
        /// detail_vegetation_def. A road's head flags bit 28 (right) / 29 (left) switch it off, a terrain item's
        /// bit 11 (right) / 28 (left) (TruckLib RoadSerializer kflag4 bits 4/5, TerrainSerializer kflag2 bit 3 and
        /// kflag4 bit 4, "Right.DetailVegetation = !karr4[4]"). Set by EtsMapData.ReadRoad for roads and by
        /// EtsTerrainBuilder for terrain items; true on prefab corners, which carry no such flag.
        /// </summary>
        public bool DetailVegetation = true;

        /// <summary>
        /// The band across the side, metres from its inner edge, IN WHICH detail vegetation grows. The game's own field
        /// names (eurotrucks2.exe) are "detail_veg_distance_min" / "detail_veg_distance" on prefab corners and
        /// "right_dvdm" / "right_dvd" on terrain items; 1,378 of 1,418 road and terrain sides in four 3 km discs store
        /// the editor's 5 .. 100, the edits being to the far end (31 .. 200 m), and 118 of 318 prefab corners 5 .. 0
        /// (none) - probe N/grass/G2. A road stores it as 4 x u16 / 10 in its .data record after the centre vegetation
        /// (L-from, R-from, L-to, R-to; TruckLib RoadSerializer, which calls them NoDetailVegetationFrom/To), a prefab
        /// corner at +2/+4 of its 38-byte block, a terrain item at +74/+76 of its side block. Negative = not read (a
        /// terrain item until EtsMapSector reads it): the editor's 5 .. 100 is used.
        /// </summary>
        public float DetailBandFrom = -1f, DetailBandTo = -1f;

        public string Profile => EtsToken.Decode(ProfileToken);
    }

    /// <summary>
    /// A terrain item (type 1): a strip of ground between two nodes with nothing on it, used
    /// where roads do not reach. Body (version 898): start node, end node, two float3 node
    /// offsets, f32 length, u32 seed, 3 railings (token + i16), then per side (right, left):
    /// u16 size / 10, profile token, f32 coefficient, a second token and float, 3 vegetation
    /// records of 16 bytes, u16 detail-vegetation from and to; then u32 vegetation sphere count
    /// and spheres of 20 bytes, quad data for the right then the left side, and four edge tokens.
    /// Every terrain item of the first sector parses to the byte with this layout.
    /// </summary>
    public sealed class EtsMapTerrainItem : EtsMapItem
    {
        public ulong StartNode;
        public ulong EndNode;
        public float[] StartOffset = new float[3];
        public float[] EndOffset = new float[3];
        public float Length;

        /// <summary>
        /// 907 only: the summed length of the terrain items before this one in its chain. Measured
        /// round the 1.61 start: equal to that sum on 3,052 of 3,052 items whose chain is loaded,
        /// exactly 0 on the chain starts, never negative or NaN. Zero on 898, which has no such field.
        /// </summary>
        public float PreviousLength;

        public uint Seed;

        /// <summary>Bits 4-5 of the first flag byte. 1 is a 16 m column step on every item measured.</summary>
        public int StepSize;

        public EtsTerrainSide Right = new EtsTerrainSide();
        public EtsTerrainSide Left = new EtsTerrainSide();
        public ulong RightEdge, RightEdgeLook, LeftEdge, LeftEdgeLook;

        public float ColumnStep => StepSize == 0 ? 4f : StepSize == 1 ? 16f : StepSize == 2 ? 12f : 2f;

        /// <summary>Head flags bit 11 off: detail vegetation on the right side (TruckLib TerrainSerializer kflag2 bit 3).</summary>
        public bool RightDetailVegetation => (Flags & (1u << 11)) == 0;

        /// <summary>Head flags bit 28 off: detail vegetation on the left side (TruckLib TerrainSerializer kflag4 bit 4).</summary>
        public bool LeftDetailVegetation => (Flags & (1u << 28)) == 0;
    }

    /// <summary>
    /// Reads a sector's .data file: a 16-byte header (u32 version, u64 game id, u32 3) and then,
    /// in ascending uid order, u64 uid + a payload whose layout depends on the item's type. Roads
    /// and prefabs carry their terrain widths and quad grids here. An item this parser did not
    /// find, or a type without a known layout, is skipped by scanning to the next known, larger
    /// uid - uids are ascending, so a stray match on payload bytes is rejected.
    /// </summary>
    public static class EtsMapData
    {
        public static void Read(EtsMapSector sector, byte[] d, out int records, out int skipped)
        {
            records = 0;
            skipped = 0;

            if (d == null || d.Length < 24)
            {
                return;
            }

            var byUid = new Dictionary<ulong, EtsMapItem>();

            foreach (EtsMapItem item in sector.Items)
            {
                byUid[item.Uid] = item;
            }

            // The .data file states its own map version in its first four bytes: 898 on game
            // 1.48, 907 on 1.61. Read it here rather than trust the .base, so the payload layout
            // follows the file it is actually reading.
            int version = (int)BitConverter.ToUInt32(d, 0);

            int p = 16;

            while (p + 8 <= d.Length)
            {
                ulong uid = BitConverter.ToUInt64(d, p);

                if (uid == 0)
                {
                    break;
                }

                int start = p + 8;
                int end = -1;

                if (byUid.TryGetValue(uid, out EtsMapItem item))
                {
                    try
                    {
                        if (item is EtsMapRoad road)
                        {
                            end = ReadRoad(d, start, road, version);
                        }
                        else if (item is EtsMapPrefab prefab)
                        {
                            end = ReadPrefab(d, start, prefab);
                        }
                    }
                    catch (Exception)
                    {
                        end = -1;
                    }

                    // The payload must land on the next record or the end of the file.
                    if (end > d.Length || (end >= 0 && end + 8 <= d.Length && BitConverter.ToUInt64(d, end) != 0 && !(BitConverter.ToUInt64(d, end) > uid && byUid.ContainsKey(BitConverter.ToUInt64(d, end)))))
                    {
                        end = -1;
                    }
                }

                if (end < 0)
                {
                    skipped++;
                    int next = -1;

                    for (int q = start; q + 8 <= d.Length; q++)
                    {
                        ulong u = BitConverter.ToUInt64(d, q);

                        if (u > uid && byUid.ContainsKey(u))
                        {
                            next = q;
                            break;
                        }
                    }

                    if (next < 0)
                    {
                        break;
                    }

                    p = next;
                    continue;
                }

                records++;
                p = end;
            }
        }

        /// <summary>
        /// Road payload (898): overlay token; per side (right, left): 2 models (token, i16, u16),
        /// u16 terrain size / 10, 3 vegetation records of 16 bytes, sidewalk material token, quad
        /// data; centre material token, colour, u16 rotation, u32 seed, centre vegetation (token,
        /// u16, u8, u8), 4 x u16 detail vegetation bounds, vegetation spheres (u32 count, 20 bytes
        /// each), then two lists each of additional parts (8), edge overrides (20) and variant
        /// overrides (12) with u32 counts.
        ///
        /// 907 changes two things and nothing else: an f32 previous_length straight after the
        /// seed, and four edge-look tokens (32 bytes) closing the record. Without the first, the
        /// vegetation-sphere count is read out of the detail bounds, SkipList throws, and every
        /// road record in every sector is skipped - measured 0 of 1,886 roads round the 1.61 start
        /// with any terrain, which is why the verges were bare and vegetation never ran. With both,
        /// 74,693 of 74,698 roads map-wide carry terrain. Both are gated on 907 exactly; 899-906
        /// are unmeasured, and 1.48's files are gone from this machine, so 898 could not be
        /// re-measured - its path below is byte for byte what it was.
        /// </summary>
        private static int ReadRoad(byte[] d, int start, EtsMapRoad road, int version)
        {
            int limit = d.Length;
            int p = start + 8;
            var sizes = new float[2];
            var quads = new EtsTerrainQuadData[2];
            var vegetation = new EtsVegetationRecord[2][];
            var sidewalks = new ulong[2];
            EtsRoadSideModel[] sideModels = null;

            for (int s = 0; s < 2; s++)
            {
                // The side's two model slots (lamps, reflective posts: token, offset, spacing), read for EtsMapBuilder.
                sideModels = EtsRoadSideModel.Read(d, p, s == 1, sideModels);
                p += 24;
                sizes[s] = BitConverter.ToUInt16(d, p) / 10f;
                p += 2;
                vegetation[s] = EtsVegetationRecord.Read(d, p, 3);
                p += 48;

                // The side's sidewalk material, a "sidewalk.<token>" unit in road_sidewalk.sii
                // ('2' = small_block_pavement on every city road round the Szczecin start). It was
                // stepped over, and with it the sidewalk itself.
                sidewalks[s] = BitConverter.ToUInt64(d, p);
                p += 8;
                quads[s] = EtsTerrainQuadData.Read(d, ref p, limit);
            }

            // After both sides: the brush laid between the carriageways of a divided road, its
            // colour and uv rotation (TruckLib RoadSerializer: center_material, color, rotation).
            road.CenterMaterial = BitConverter.ToUInt64(d, p);
            p += 8 + 4 + 2;
            road.Seed = BitConverter.ToUInt32(d, p);
            p += 4;

            if (version >= 907)
            {
                p += 4;
            }

            p += 8 + 2 + 1 + 1;

            // The detail-vegetation bands across the sides, u16 / 10 m, in TruckLib's order (RoadSerializer.DeserializeDataPayload):
            // left from, right from, left to, right to (EtsTerrainSide.DetailBandFrom: grass grows INSIDE the band).
            float leftFrom = BitConverter.ToUInt16(d, p) / 10f, rightFrom = BitConverter.ToUInt16(d, p + 2) / 10f;
            float leftTo = BitConverter.ToUInt16(d, p + 4) / 10f, rightTo = BitConverter.ToUInt16(d, p + 6) / 10f;
            p += 8;
            p = SkipList(d, p, 20, 4096);
            p = SkipList(d, p, 8, 64);
            p = SkipList(d, p, 8, 64);
            p = SkipList(d, p, 20, 64);
            p = SkipList(d, p, 20, 64);
            p = SkipList(d, p, 12, 64);
            p = SkipList(d, p, 12, 64);

            // In the same order as the body's edge tokens at +44, +52, +60, +68. Of 384 non-zero
            // slots map-wide, all 377 whose road_edge resolves name a look of that edge's own
            // model; no other ordering comes close. Almost always zero - 238 of 74,550 roads set any.
            if (version >= 907)
            {
                road.RightEdgeLook = BitConverter.ToUInt64(d, p);
                road.RightInnerEdgeLook = BitConverter.ToUInt64(d, p + 8);
                road.LeftInnerEdgeLook = BitConverter.ToUInt64(d, p + 16);
                road.LeftOuterEdgeLook = BitConverter.ToUInt64(d, p + 24);
                p += 32;
            }

            road.Right.Size = sizes[0];
            road.Right.Quads = quads[0];
            road.Right.Vegetation = vegetation[0];
            road.Right.SidewalkMaterial = sidewalks[0];
            road.Left.Size = sizes[1];
            road.Left.Quads = quads[1];
            road.Left.Vegetation = vegetation[1];
            road.Left.SidewalkMaterial = sidewalks[1];
            road.Right.DetailBandFrom = rightFrom;
            road.Right.DetailBandTo = rightTo;
            road.Left.DetailBandFrom = leftFrom;
            road.Left.DetailBandTo = leftTo;

            // Head flags kflag4 bits 4/5 (TruckLib RoadSerializer: "road.Right.DetailVegetation = !karr4[4]").
            road.Right.DetailVegetation = (road.Flags & (1u << 28)) == 0;
            road.Left.DetailVegetation = (road.Flags & (1u << 29)) == 0;
            road.HasTerrainData = true;
            road.SideModels = sideModels;
            return p;
        }

        /// <summary>
        /// Prefab payload (898): look token; six corner blocks of u16 size / 10, u16 from, u16 to and
        /// two vegetation records (38 bytes); vegetation parts (u32 count, 32 bytes each); u32 seed;
        /// vegetation spheres; six times model, variant and look tokens; six quad grids. The corner
        /// count is a fixed six in this version whatever the node count (measured on 20 prefabs).
        /// </summary>
        private static int ReadPrefab(byte[] d, int start, EtsMapPrefab prefab)
        {
            const int corners = 6;
            int limit = d.Length;
            int p = start + 8;
            var sizes = new float[corners];
            var bandFrom = new float[corners];
            var bandTo = new float[corners];

            for (int i = 0; i < corners; i++)
            {
                sizes[i] = BitConverter.ToUInt16(d, p) / 10f;

                // The corner's detail-vegetation band, u16 / 10 m (TruckLib PrefabSerializer: DetailVegetationFrom/To).
                bandFrom[i] = BitConverter.ToUInt16(d, p + 2) / 10f;
                bandTo[i] = BitConverter.ToUInt16(d, p + 4) / 10f;
                p += 38;
            }

            p = SkipList(d, p, 32, 64);
            p += 4;
            p = SkipList(d, p, 20, 4096);
            p += corners * 24;

            var quads = new EtsTerrainQuadData[corners];

            for (int i = 0; i < corners; i++)
            {
                quads[i] = EtsTerrainQuadData.Read(d, ref p, limit);
            }

            prefab.Corners = new EtsTerrainSide[corners];

            for (int i = 0; i < corners; i++)
            {
                prefab.Corners[i] = new EtsTerrainSide
                {
                    Size = sizes[i],
                    Quads = quads[i],
                    ProfileToken = i < prefab.TerrainProfiles.Length ? prefab.TerrainProfiles[i] : 0,
                    Coefficient = i < prefab.TerrainCoefficients.Length ? prefab.TerrainCoefficients[i] : 1f,
                    DetailBandFrom = bandFrom[i],
                    DetailBandTo = bandTo[i]
                };
            }

            return p;
        }

        private static int SkipList(byte[] d, int p, int recordSize, int maxCount)
        {
            uint count = BitConverter.ToUInt32(d, p);
            if (count > maxCount) throw new FormatException("list of " + count);
            return p + 4 + (int)count * recordSize;
        }
    }
}
