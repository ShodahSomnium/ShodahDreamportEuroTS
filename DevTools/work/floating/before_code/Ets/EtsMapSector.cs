using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>A point the map hangs things on: a road end, a prefab corner, a model's place.</summary>
    public sealed class EtsMapNode
    {
        public ulong Uid;

        /// <summary>x, y, z in metres, in the game's frame.</summary>
        public float[] Position = new float[3];

        /// <summary>x, y, z, w. The file stores w first; reordered here.</summary>
        public float[] Rotation = new float[4];

        public ulong BackwardItem;
        public ulong ForwardItem;
        public uint Flags;

        /// <summary>
        /// Where the node points, in the game's frame. Measured, not assumed: across 109 roads
        /// the rotation applied to (0, 0, -1) runs along the road toward the end node on 106 of
        /// them and (0, 0, +1) on none, which is also the direction the truck model faces.
        /// </summary>
        public float[] Forward()
        {
            return Rotate(Rotation, 0f, 0f, -1f);
        }

        public static float[] Rotate(float[] q, float vx, float vy, float vz)
        {
            float x = q[0], y = q[1], z = q[2], w = q[3];
            float cx = y * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - y * vx;
            float ccx = y * cz - z * cy, ccy = z * cx - x * cz, ccz = x * cy - y * cx;
            return new[] { vx + 2 * w * cx + 2 * ccx, vy + 2 * w * cy + 2 * ccy, vz + 2 * w * cz + 2 * ccz };
        }
    }

    /// <summary>Every item shares this head: a type, an id, a five-axis bounding volume, flags.</summary>
    public class EtsMapItem
    {
        public const int Terrain = 1;
        public const int Building = 2;
        public const int Road = 3;
        public const int Prefab = 4;
        public const int Model = 5;
        public const int Company = 6;
        public const int Service = 7;
        public const int CutPlane = 8;
        public const int Mover = 9;
        public const int City = 12;
        public const int Sign = 36;
        public const int BezierPatch = 39;
        public const int Compound = 40;
        public const int Curve = 44;
        public const int Hookup = 47;

        public int Type;
        public ulong Uid;

        /// <summary>Axes: x, y, z, (x+z)/2, (x-z)/2 - confirmed by the relation holding on every item.</summary>
        public float[] KdopMin = new float[5];

        public float[] KdopMax = new float[5];
        public uint Flags;
        public byte ViewDistance;
        public byte DlcGuard;

        /// <summary>Where the body starts and how long it was taken to be, for diagnosis.</summary>
        public int BodyOffset;

        public int BodyLength;

        /// <summary>True when the body was measured by scanning for the next header, not decoded.</summary>
        public bool BodyScanned;
    }

    /// <summary>
    /// A road: one curve between two nodes, drawn with a look. Body is a fixed 212 bytes in
    /// version 898 - every one of 105 roads in the first sector, and the two nodes and the
    /// length sit at the end in every one.
    /// </summary>
    public sealed class EtsMapRoad : EtsMapItem
    {
        public uint RoadFlags;
        public ulong LookToken;
        public ulong VariantToken;
        /// <summary>
        /// Shoulder models. A road body names four: the right carriageway's right (outer) and
        /// left (inner) edge, and the left carriageway's right (inner) and left (outer) edge. A
        /// single-carriageway road is all "right carriageway", so its left shoulder is the right
        /// carriageway's left edge; a divided road's inner edges face the median (on the tram
        /// road they are the tram tracks) and its outer left is the left carriageway's left.
        /// </summary>
        public ulong RightEdgeToken;
        public ulong LeftEdgeToken;
        public ulong RightInnerEdgeToken;
        public ulong LeftInnerEdgeToken;
        public ulong LeftOuterEdgeToken;

        /// <summary>907 only: the four look tokens that close the sector .data road record, one
        /// per edge token (+44, +52, +60, +68 in the body). Zero on 898, and on nearly every 907 road.</summary>
        public ulong RightEdgeLook, RightInnerEdgeLook, LeftInnerEdgeLook, LeftOuterEdgeLook;

        /// <summary>
        /// Terrain beside the road, right and left of its forward direction. Profile and
        /// coefficient come from the body; size and quad grid from the sector's .data file.
        /// </summary>
        public EtsTerrainSide Right = new EtsTerrainSide();
        public EtsTerrainSide Left = new EtsTerrainSide();

        /// <summary>Quad columns per 15 m of road: one normally, three high-poly, nine superfine.</summary>
        public bool HighPoly;

        public bool Superfine;

        /// <summary>A legacy road the game treats as a city street: it carries sidewalks (EtsTerrainSide.SidewalkSize).</summary>
        public bool IsCityRoad;
        public bool HasTerrainData;
        public ulong MaterialToken;

        /// <summary>Brush (terrain_mat unit) laid between the carriageways of a divided road.</summary>
        public ulong CenterMaterial;
        public ulong StartNode;
        public ulong EndNode;
        public float Length;

        /// <summary>Random seed from the .data file, used for vegetation placement.</summary>
        public uint Seed;

        /// <summary>
        /// The legacy road material at body +116 (TruckLib RoadSerializer: "Material", after the two side looks):
        /// unit road_mat.&lt;token&gt; in /def/world/road_material.sii, whose path is the asphalt atlas a legacy look is
        /// drawn with ("0" = /material/road/asphalt.mat on most roads between towns in the UK and Poland; 1.61 map,
        /// harness ProgramRd5). Empty on nearly every template road.
        /// </summary>
        public ulong LegacyMaterialToken;

        /// <summary>
        /// The road's railings (crash barriers, bridge rails, walls): body +124, three slots, each the right side's
        /// railing token and i16 offset / 100 then the left side's (TruckLib RoadSerializer). Null when every slot is
        /// empty, which is most roads. A token names railing.&lt;token&gt; in /def/world/railing.sii.
        /// </summary>
        public EtsRoadRailing[] Railings;

        /// <summary>Side height offsets (body +184, +188, i32 / 100); read for diagnosis, not applied.</summary>
        public float RightHeightOffset, LeftHeightOffset;

        /// <summary>
        /// The road's side models - street lamps and reflective posts, sign.&lt;token&gt; units with road_model: true -
        /// repeated every Distance metres at Offset metres from the centre line. They live in the sector .data payload
        /// (two per side, straight after the overlay token: token, i16 offset / 100, u16 distance / 100), which
        /// EtsMapData reads; null until it fills them in, and null when every slot is empty.
        /// </summary>
        public EtsRoadSideModel[] SideModels;

        public string Look => EtsToken.Decode(LookToken);

        // Header flag bits (kdop flags: byte 0 terrain noise/transition, byte 1 sidewalks and traffic side, byte 2
        // railing inversion, city road, model shifts; TruckLib RoadSerializer) and body flag bits (road flags: byte 0,
        // DLC guard, byte 2 model flips, byte 3 railing double-sided / centre-part-only).
        public bool LeftHandTraffic => (Flags & (1u << 15)) != 0;
        public bool RightRailingInverted => (Flags & (1u << 17)) != 0;
        public bool LeftRailingInverted => (Flags & (1u << 18)) != 0;
        public bool LeftRailingDoubleSided => (RoadFlags & (1u << 24)) != 0;
        public bool RightRailingDoubleSided => (RoadFlags & (1u << 25)) != 0;
        public bool LeftRailingCenterOnly => (RoadFlags & (1u << 26)) != 0;
        public bool RightRailingCenterOnly => (RoadFlags & (1u << 27)) != 0;

        /// <summary>Side model slot shifted by half its spacing: slot 0 from the header flags, slot 1 from the road flags.</summary>
        public bool SideModelShifted(bool left, int slot)
        {
            return slot == 0 ? (Flags & (1u << (left ? 21 : 20))) != 0 : (RoadFlags & (1u << (left ? 6 : 5))) != 0;
        }

        /// <summary>Side model slot turned round (road flags byte 2: right 0, right 1, left 0, left 1).</summary>
        public bool SideModelFlipped(bool left, int slot)
        {
            return (RoadFlags & (1u << (18 + (left ? 2 : 0) + slot))) != 0;
        }
    }

    /// <summary>One railing slot of a road: the railing unit, its side and its offset in metres.</summary>
    public sealed class EtsRoadRailing
    {
        public ulong Model;
        public float Offset;
        public bool Left;
        public int Slot;

        public string Name => EtsToken.Decode(Model);
    }

    /// <summary>One side model slot of a road (a lamp or a reflective post repeated along it).</summary>
    public sealed class EtsRoadSideModel
    {
        public ulong Model;
        public float Offset;
        public float Distance;
        public bool Left;
        public int Slot;

        public string Name => EtsToken.Decode(Model);

        /// <summary>
        /// One side's two model slots of a road .data record, starting at <paramref name="p"/> (straight after the
        /// overlay token for the right side; after the right side's quad grid for the left): u64 token, i16 offset / 100,
        /// u16 distance / 100 each (TruckLib RoadSerializer.DeserializeDataPayload). Non-empty slots are appended to
        /// <paramref name="list"/>, which is returned (null while every slot so far is empty).
        /// </summary>
        public static EtsRoadSideModel[] Read(byte[] d, int p, bool left, EtsRoadSideModel[] list)
        {
            for (int slot = 0; slot < 2; slot++)
            {
                int q = p + slot * 12;

                if (d == null || q + 12 > d.Length)
                {
                    break;
                }

                ulong model = BitConverter.ToUInt64(d, q);

                if (model == 0)
                {
                    continue;
                }

                var entry = new EtsRoadSideModel
                {
                    Model = model,
                    Offset = BitConverter.ToInt16(d, q + 8) / 100f,
                    Distance = BitConverter.ToUInt16(d, q + 10) / 100f,
                    Left = left,
                    Slot = slot
                };

                if (list == null)
                {
                    list = new[] { entry };
                }
                else
                {
                    var grown = new EtsRoadSideModel[list.Length + 1];
                    Array.Copy(list, grown, list.Length);
                    grown[list.Length] = entry;
                    list = grown;
                }
            }

            return list;
        }
    }

    /// <summary>A prefab: a junction, a company yard, a bridge - a model with several road ends.</summary>
    public sealed class EtsMapPrefab : EtsMapItem
    {
        public ulong PrefabToken;
        public ulong VariantToken;
        public ulong[] AdditionalParts = new ulong[0];
        public ulong[] NodeUids = new ulong[0];

        /// <summary>The service items this prefab owns - fuel pumps, weigh stations, the company's own item.</summary>
        public ulong[] SlaveItems = new ulong[0];

        /// <summary>Which descriptor node map node 0 stands on; map node i is descriptor node (Origin + i) mod n.</summary>
        public int Origin;

        public ulong[] TerrainProfiles = new ulong[0];
        public float[] TerrainCoefficients = new float[0];

        /// <summary>Six corner terrains from the .data file (the count is fixed in version 898); null until read.</summary>
        public EtsTerrainSide[] Corners;

        /// <summary>
        /// The traffic light programme the mapper chose for this junction (tr_sem_prof.&lt;token&gt; in
        /// /def/world/semaphore_profile.sii), or 0 for "the descriptor's own". The last u64 of the body
        /// (TruckLib PrefabSerializer: SemaphoreProfile after the per-node terrain). Round the 1.61 start:
        /// 0 on 493 of 494 prefabs, cr_2x1_low on one (harness Program1106); the descriptor's semaphores
        /// name their own profile, so this only overrides.
        /// </summary>
        public ulong SemaphoreProfile;

        public string Name => EtsToken.Decode(PrefabToken);
    }

    /// <summary>A static model placed on one node.</summary>
    public sealed class EtsMapModel : EtsMapItem
    {
        public ulong ModelToken;
        public ulong LookToken;
        public ulong VariantToken;
        public ulong[] AdditionalParts = new ulong[0];
        public ulong NodeUid;
        public float[] Scale = { 1f, 1f, 1f };
        public ulong TerrainMaterial;
        public uint TerrainColor;

        public string Name => EtsToken.Decode(ModelToken);
    }

    /// <summary>A building scheme stretched between two nodes: fences, walls, hedges.</summary>
    public sealed class EtsMapBuilding : EtsMapItem
    {
        public ulong SchemeToken;
        public ulong LookToken;
        public ulong StartNode;
        public ulong EndNode;
        public float Length;
        public uint Seed;
        public float Stretch;
        public float[] Heights = new float[0];
    }


    /// <summary>A city area (type 12): the city's token and the extent of its centre around one node.</summary>
    public sealed class EtsMapCity : EtsMapItem
    {
        public ulong CityToken;
        public float Width;
        public float Height;
        public ulong NodeUid;

        public string Name => EtsToken.Decode(CityToken);
    }

    /// <summary>
    /// A company (type 6): a depot of a company in a city, standing on a prefab, with spawn points
    /// for trailers and the trucks that deliver to it. The first three fields swapped round at map
    /// version 903: up to 902 (898, game 1.48, measured on that demo) the body is company token,
    /// city token, prefab uid; from 903 on (907, game 1.61) it is city token, prefab uid, company
    /// token. Then, either way, the node uid, a u32-counted list of spawn point node uids and a u32
    /// flag per point. Measured on 907 over all 480 company items of the map: the uid at +8 is a
    /// real prefab item 480 times and is the prefab whose slave-item list holds this company on
    /// 470 of the 470 times that list exists.
    /// </summary>
    public sealed class EtsMapCompany : EtsMapItem
    {
        public ulong CityToken;
        public ulong PrefabUid;
        public ulong CompanyToken;
        public ulong NodeUid;
        public ulong[] SpawnPoints = new ulong[0];

        public string CityId => EtsToken.Decode(CityToken);
        public string Name => EtsToken.Decode(CompanyToken);
    }
    /// <summary>
    /// One map sector: the items and nodes of one 4 km square, read from its .base and .aux files.
    ///
    /// The layout was learned from the demo's own sectors rather than from any document, and
    /// every claim below was checked against them:
    ///
    ///     0   u32 version (898), u64 game id ("euro2" as a token), u32 3, u32 item count
    ///     20  items: u32 type, u64 uid, 10 floats kdop, u32 flags, u8 view distance, body
    ///     ... u32 node count, nodes of 56 bytes (uid, 3 x i32 position / 256, quaternion w x y z,
    ///         backward item, forward item, u32 flags), u32 trailer
    ///
    /// Items whose bodies are decoded here - roads, prefabs, models, buildings, cut planes -
    /// have their exact sizes; anything else is stepped over by finding the next header, which
    /// is recognised by its kdop: axes 3 and 4 must be (x+z)/2 and (x-z)/2 of the box, and the
    /// box must sit inside the sector's neighbourhood. That test put 218 of 223 headers in the
    /// first sector where a hand count put them.
    /// </summary>
    /// <summary>
    /// A bezier patch: a bicubic surface of ground painted like a terrain strip - the mapper's
    /// tool for the irregular ground between roads, yards and verges (502 of them within the
    /// nine sectors round the bay). Sixteen control points relative to the item's node, a quad
    /// grid of tesselation rows and columns, and the same quad data (brushes, blends, vertex
    /// records) a road side carries. Layout by TruckLib's BezierPatchSerializer for version 898.
    /// </summary>
    public sealed class EtsMapBezierPatch : EtsMapItem
    {
        public ulong NodeUid;

        /// <summary>Sixteen points, x index outer and z index inner (point = x * 4 + z), three floats each, game frame, relative to the node.</summary>
        public float[] ControlPoints = new float[48];

        public int TesselationX;
        public int TesselationZ;
        public uint Seed;
        public ulong[] VegetationTokens = new ulong[3];
        public EtsTerrainQuadData Quads;
    }

    /// <summary>
    /// A curve: one model repeated nose to tail along a Hermite path between two nodes - guard
    /// rails, kerbs, fences, cable runs, painted road stripes. Version 898 stores a FLAT single
    /// subcurve, not TruckLib's master-branch list: the "subcurve use mask" that layout expects
    /// reads zero on all 3,647 curves in the nine sectors, while this reading ends exactly on the
    /// next item header on 3,699 of 3,750 and decodes every token to a name that exists in
    /// /def/world/curve_model.sii. Body is 112 + 4 * height offsets. Version 907 (game 1.61)
    /// changed to TruckLib's masked subcurve list; DecodeCurve907 reads that.
    /// </summary>
    public sealed class EtsMapCurve : EtsMapItem
    {
        public ulong ModelToken;
        public ulong NodeUid;
        public ulong ForwardNodeUid;
        public ulong Locator0;
        public ulong Locator1;
        public float Length;
        public uint Seed;
        public float Stretch;
        public float Scale;
        public float FixedStep;
        public ulong TerrainMaterial;
        public uint TerrainColor;
        public float TerrainRotation;
        public ulong FirstPart;
        public ulong LastPart;
        public ulong VariationToken;
        public ulong LookToken;

        /// <summary>907 only: the subcurve flag word. Bit 0 inverts the geometry along the node axis
        /// (1,918 of 9,428 curves round the 1.61 start), bit 1 steps a sloped item rather than
        /// stretching it (141), bit 2 is vertex tint noise (14); no higher bit is ever set. 898 had
        /// nowhere to put it. Read, not yet acted on by the run builder.</summary>
        public uint SubcurveFlags;

        public float[] Heights = new float[0];

        public string Name => EtsToken.Decode(ModelToken);
    }

    /// <summary>
    /// A hookup: a named spawn point the game fills from a pool - almost all of them parked cars.
    /// Body is a Pascal string whose length is a u64 (no terminator) followed by the node uid;
    /// all 461 in the nine sectors decode to a clean pool name and a node that resolves.
    /// </summary>
    public sealed class EtsMapHookup : EtsMapItem
    {
        public string PoolName = string.Empty;
        public ulong NodeUid;
    }

    /// <summary>
    /// A sign. Body is 41 + 24 * boards when untemplated - the template's length is a u64, which
    /// is what makes the size come out four bytes short if it is read as a u32 - and a templated
    /// sign carries TWO arrays after the template name (2026-10-14, sign texts; the old reading took the first
    /// count, almost always 0, for the override count and lost every override): u32 area count, per area
    /// { u64 area token (the model locator, "sb1", "ml2"), u8 flags, flags &amp; 1: u16, flags &amp; 2: u64 board token },
    /// then u32 override count, per override { u32 item id, u64 area token, u32 attributes, attributes x
    /// { u16 type, u32 index, value } }. Over all 282 sectors of the 1.61 demo: 11,475 templated signs, every
    /// one now ends exactly on the next item header or the node table (README "2026-10-14: sign texts").
    /// Boards: 3 tokens each - road, city1, city2 - the legacy overlays /material/overlay/&lt;token&gt;.mat that a
    /// model locator hooked to /unit/hookup/sign_*.sii (sign_overlay: model_type, board_index, scale) shows.
    /// </summary>
    public sealed class EtsMapSign : EtsMapItem
    {
        public ulong ModelToken;
        public ulong NodeUid;
        public ulong LookToken;
        public ulong VariantToken;
        public string Template = string.Empty;

        /// <summary>3 per board: road, city1, city2 (0 = none).</summary>
        public ulong[] Boards = System.Array.Empty<ulong>();

        public EtsMapSignArea[] Areas = System.Array.Empty<EtsMapSignArea>();
        public EtsMapSignOverride[] Overrides = System.Array.Empty<EtsMapSignOverride>();
    }

    /// <summary>A templated sign's per-area record: flags 2 replaces the board the template binds there.</summary>
    public struct EtsMapSignArea
    {
        public ulong Area;
        public byte Flags;
        public ushort Value;
        public ulong Board;
    }

    /// <summary>One attribute of a template item the map item overrides. Type 1 byte, 2/3 u32, 4 float, 5 string, 6 u64.</summary>
    public struct EtsMapSignAttribute
    {
        public ushort Type;
        public uint Index;
        public uint Number;
        public float Float;
        public ulong Token;
        public string Text;
    }

    public sealed class EtsMapSignOverride
    {
        public uint Id;
        public ulong Area;
        public EtsMapSignAttribute[] Attributes;
    }

    /// <summary>
    /// A compound: a bag of items the mapper grouped together, carrying ITS OWN node table. The
    /// children's nodes are in that table and in no sector's, which is why they could never be
    /// placed: of 2,750 non-zero child node references in the nine sectors, 2,750 resolve in the
    /// compound's own list and none in any sector table. The children's positions are absolute -
    /// the compound's parent node is a tether, not an origin.
    /// </summary>
    public sealed class EtsMapCompound : EtsMapItem
    {
        public ulong NodeUid;
        public readonly List<EtsMapItem> Children = new List<EtsMapItem>();
        public readonly List<ulong> OwnNodeUids = new List<ulong>();
    }

    public sealed class EtsMapSector
    {
        public const float SectorSize = 4000f;

        public int SectorX;
        public int SectorZ;
        public string Name;

        public readonly List<EtsMapNode> Nodes = new List<EtsMapNode>();
        public readonly Dictionary<ulong, EtsMapNode> NodesByUid = new Dictionary<ulong, EtsMapNode>();
        public readonly List<EtsMapItem> Items = new List<EtsMapItem>();
        public readonly List<EtsMapRoad> Roads = new List<EtsMapRoad>();
        public readonly List<EtsMapPrefab> Prefabs = new List<EtsMapPrefab>();
        public readonly List<EtsMapModel> Models = new List<EtsMapModel>();
        public readonly List<EtsMapBuilding> Buildings = new List<EtsMapBuilding>();
        public readonly List<EtsMapTerrainItem> Terrains = new List<EtsMapTerrainItem>();
        public readonly List<EtsMapCity> Cities = new List<EtsMapCity>();
        public readonly List<EtsMapCompany> Companies = new List<EtsMapCompany>();
        public readonly List<EtsMapBezierPatch> BezierPatches = new List<EtsMapBezierPatch>();
        public readonly List<EtsMapCurve> Curves = new List<EtsMapCurve>();
        public readonly List<EtsMapHookup> Hookups = new List<EtsMapHookup>();
        public readonly List<EtsMapSign> Signs = new List<EtsMapSign>();
        public readonly List<EtsMapCompound> Compounds = new List<EtsMapCompound>();

        /// <summary>Items announced by the headers of both files.</summary>
        public int DeclaredItems;

        /// <summary>Items found. Fewer than declared means a header the scan did not recognise.</summary>
        public int FoundItems;

        public int ScannedBodies;

        /// <summary>The map version both files state in their first four bytes: 898 on game 1.48,
        /// 907 on 1.61. Item layouts that changed between the two branch on it.</summary>
        public uint MapVersion;

        /// <summary>Records read from the .data file, and records skipped for want of a layout or an item.</summary>
        public int DataRecords;
        public int DataSkipped;

        public readonly List<string> Warnings = new List<string>();

        public static string FileName(int sectorX, int sectorZ, string extension)
        {
            return "/map/europe/sec" + (sectorX < 0 ? "-" : "+") + Math.Abs(sectorX).ToString("0000") +
                   (sectorZ < 0 ? "-" : "+") + Math.Abs(sectorZ).ToString("0000") + "." + extension;
        }

        /// <summary>The sector a world position falls in.</summary>
        public static void SectorOf(float x, float z, out int sectorX, out int sectorZ)
        {
            sectorX = (int)Math.Floor(x / SectorSize);
            sectorZ = (int)Math.Floor(z / SectorSize);
        }

        public static EtsMapSector Parse(int sectorX, int sectorZ, byte[] baseBytes, byte[] auxBytes)
        {
            return Parse(sectorX, sectorZ, baseBytes, auxBytes, null);
        }

        private static EtsMapSector ParseFiles(int sectorX, int sectorZ, byte[] baseBytes, byte[] auxBytes)
        {
            var sector = new EtsMapSector { SectorX = sectorX, SectorZ = sectorZ, Name = "sec" + sectorX + "," + sectorZ };

            // Nodes from both files before items from either: a road in the base file can stand
            // on a node the aux file owns, and an item that names a node the parser has not seen
            // yet must not be mistaken for a header.
            int baseTable = baseBytes != null ? sector.ReadNodes(baseBytes, "base") : -1;
            int auxTable = auxBytes != null ? sector.ReadNodes(auxBytes, "aux") : -1;

            if (baseBytes != null && baseTable >= 0)
            {
                sector.ParseItems(baseBytes, "base", baseTable);
            }

            if (auxBytes != null && auxTable >= 0)
            {
                sector.ParseItems(auxBytes, "aux", auxTable);
            }

            return sector;
        }

        /// <summary>As above, then the .data file: terrain widths and quad grids for roads and prefabs.</summary>
        public static EtsMapSector Parse(int sectorX, int sectorZ, byte[] baseBytes, byte[] auxBytes, byte[] dataBytes)
        {
            EtsMapSector sector = ParseFiles(sectorX, sectorZ, baseBytes, auxBytes);

            if (dataBytes != null)
            {
                EtsMapData.Read(sector, dataBytes, out sector.DataRecords, out sector.DataSkipped);
            }

            sector.DropGuarded();
            return sector;
        }

        /// <summary>Which DLC guards this install shows: the demo mounts no map DLC, so only 0.</summary>
        public static Func<int, bool> GuardShown = guard => guard == 0;

        /// <summary>Uids of items dropped by their DLC guard (roads, prefabs, buildings).</summary>
        public readonly HashSet<ulong> GuardDropped = new HashSet<ulong>();

        private void DropGuarded()
        {
            foreach (EtsMapRoad r in Roads) if (!GuardShown(r.DlcGuard)) GuardDropped.Add(r.Uid);
            foreach (EtsMapPrefab p in Prefabs) if (!GuardShown(p.DlcGuard)) GuardDropped.Add(p.Uid);
            foreach (EtsMapBuilding b in Buildings) if (!GuardShown(b.DlcGuard)) GuardDropped.Add(b.Uid);
            if (GuardDropped.Count == 0) return;

            Roads.RemoveAll(r => GuardDropped.Contains(r.Uid));
            Prefabs.RemoveAll(p => GuardDropped.Contains(p.Uid));
            Buildings.RemoveAll(b => GuardDropped.Contains(b.Uid));
            Companies.RemoveAll(c => GuardDropped.Contains(c.PrefabUid));
        }

        // ---------------------------------------------------------------------- reading

        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);
        private static ulong U64(byte[] b, int o) => BitConverter.ToUInt64(b, o);
        private static float F32(byte[] b, int o) => BitConverter.ToSingle(b, o);

        private static EtsRoadRailing[] AddRailing(EtsRoadRailing[] list, ulong model, float offset, bool left, int slot)
        {
            var railing = new EtsRoadRailing { Model = model, Offset = offset, Left = left, Slot = slot };

            if (list == null)
            {
                return new[] { railing };
            }

            var grown = new EtsRoadRailing[list.Length + 1];
            Array.Copy(list, grown, list.Length);
            grown[list.Length] = railing;
            return grown;
        }

        /// <summary>TruckLib's SidewalkSize: Meters8 = 0, Meters4 = 1, Meters2 = 2, Meters0 = 3.</summary>
        private static float SidewalkMetres(int code) => code == 0 ? 8f : code == 1 ? 4f : code == 2 ? 2f : 0f;

        /// <summary>Reads a file's node table into the sector. Returns the table offset, or -1.</summary>
        private int ReadNodes(byte[] b, string label)
        {
            if (b == null || b.Length < 24)
            {
                Warnings.Add(label + ": too short.");
                return -1;
            }

            uint version = U32(b, 0);
            MapVersion = version;

            // 898 is game 1.48, which every layout here was derived against. 907 is game 1.61.
            // Roads, prefabs, models, buildings, signs, compounds and hookups read the same on both
            // (0 desyncs, 0 unknown tokens round the 1.61 start). Terrain grew one float, the road's
            // .data payload grew a float and four tokens, curves became a masked list of subcurves,
            // and companies swapped their first three fields (at 903); each is branched where it is
            // read. NOTE: "found" matching "declared" is NOT evidence the walk is complete - found
            // includes compound children and declared does not.
            if (version != 898 && version != 907)
            {
                Warnings.Add(label + ": map version " + version + " (decoded against 898 and 907; layouts may differ).");
            }

            DeclaredItems += (int)U32(b, 16);

            int tableStart = FindNodeTable(b, out int nodeCount);

            if (tableStart < 0)
            {
                Warnings.Add(label + ": node table not found.");
                return -1;
            }

            for (int i = 0; i < nodeCount; i++)
            {
                int o = tableStart + 4 + i * 56;
                var node = new EtsMapNode { Uid = U64(b, o), BackwardItem = U64(b, o + 36), ForwardItem = U64(b, o + 44), Flags = U32(b, o + 52) };
                node.Position[0] = I32(b, o + 8) / 256f;
                node.Position[1] = I32(b, o + 12) / 256f;
                node.Position[2] = I32(b, o + 16) / 256f;
                node.Rotation[3] = F32(b, o + 20);
                node.Rotation[0] = F32(b, o + 24);
                node.Rotation[1] = F32(b, o + 28);
                node.Rotation[2] = F32(b, o + 32);

                // A rotation of four zeros (ZeroRotationNode, FindNodeTable) carries no direction, and a zero quaternion
                // handed to a Transform is not a rotation at all: it reads as none (identity). The demo has one such
                // node, a compound's parent in sec-0002-0005 (probe 2106), and nothing builds compounds today.
                if (node.Rotation[0] == 0f && node.Rotation[1] == 0f && node.Rotation[2] == 0f && node.Rotation[3] == 0f)
                {
                    node.Rotation[3] = 1f;
                }

                if (!NodesByUid.ContainsKey(node.Uid))
                {
                    Nodes.Add(node);
                    NodesByUid[node.Uid] = node;
                }
            }

            return tableStart;
        }

        /// <summary>Walks a file's items, now that every node of the sector is known.</summary>
        private void ParseItems(byte[] b, string label, int tableStart)
        {
            int declared = (int)U32(b, 16);
            float sx = SectorX * SectorSize, sz = SectorZ * SectorSize;
            int pos = 20;
            int found = 0;

            // Walk to the node table, not to a count. Everything between the header and the node
            // table is items, so reaching the table is the only end that cannot be wrong. This used
            // to stop at "found == declared" - but found includes each compound's children and the
            // declared total does not, so the counter was satisfied with items still unread. Round
            // the 1.61 start that left 5,557,772 bytes of 6 of the 9 .aux files unread, with a valid
            // item header sitting exactly at every place it stopped, and 7,846 item uids named by the
            // sectors' own node tables never registered; walking to the table brings that to 130.
            while (pos + 57 <= tableStart)
            {
                if (!IsHeader(b, pos, sx, sz, tableStart))
                {
                    // Lost the thread. Find the next header and carry on from there.
                    int next = ScanHeader(b, pos + 1, sx, sz, tableStart);

                    if (next < 0)
                    {
                        break;
                    }

                    pos = next;
                    continue;
                }

                var head = ReadHead(b, pos);
                int body = pos + 57;
                EtsMapItem item = DecodeBody(head, b, body, tableStart);

                if (item == null)
                {
                    // Unknown or unverifiable: measure the body by finding the next header.
                    int next = ScanHeader(b, body, sx, sz, tableStart);
                    int end = next < 0 ? tableStart : next;
                    item = head;
                    item.BodyLength = end - body;
                    item.BodyScanned = true;
                    ScannedBodies++;
                }

                item.BodyOffset = body;
                Items.Add(item);
                found++;

                // A compound's children are items in their own right, so they go on the item list
                // and into the count. The count is a diagnostic only - see the loop condition.
                if (item is EtsMapCompound compound)
                {
                    Items.AddRange(compound.Children);
                    found += compound.Children.Count;
                }

                pos = body + item.BodyLength;
            }

            FoundItems += found;

            if (found < declared)
            {
                Warnings.Add(label + ": found " + found + " of " + declared + " items.");
            }
        }

        private EtsMapItem ReadHead(byte[] b, int pos)
        {
            var head = new EtsMapItem { Type = (int)U32(b, pos), Uid = U64(b, pos + 4), Flags = U32(b, pos + 52), ViewDistance = b[pos + 56] };

            for (int i = 0; i < 5; i++)
            {
                head.KdopMin[i] = F32(b, pos + 12 + i * 4);
                head.KdopMax[i] = F32(b, pos + 32 + i * 4);
            }

            return head;
        }

        /// <summary>
        /// Decodes the body of a type whose layout is known, checking every node reference
        /// against the table, or returns null so the caller can measure it instead.
        /// </summary>
        private EtsMapItem DecodeBody(EtsMapItem head, byte[] b, int o, int limit)
        {
            try
            {
                switch (head.Type)
                {
                    case EtsMapItem.Road:
                        {
                            if (o + 212 > limit) return null;
                            ulong start = U64(b, o + 192), end = U64(b, o + 200);
                            float length = F32(b, o + 208);

                            // Checked by shape, not by node presence: a road may stand on a node
                            // of the neighbouring sector, which this sector's table does not hold.
                            if (start == 0 || end == 0 || start == end || float.IsNaN(length) || length <= 0f || length > 10000f) return null;

                            var road = Copy<EtsMapRoad>(head);
                            road.RoadFlags = U32(b, o);
                            road.DlcGuard = b[o + 1];
                            road.LookToken = U64(b, o + 4);
                            road.VariantToken = U64(b, o + 28);
                            road.RightEdgeToken = U64(b, o + 44);
                            road.LeftEdgeToken = U64(b, o + 52);
                            road.Right.ProfileToken = U64(b, o + 76);
                            road.Right.Coefficient = F32(b, o + 84);
                            road.Left.ProfileToken = U64(b, o + 88);
                            road.Left.Coefficient = F32(b, o + 96);
                            road.Left.Noise = (int)(head.Flags & 3);
                            road.Right.Noise = (int)((head.Flags >> 2) & 3);
                            road.Left.Transition = (int)((head.Flags >> 4) & 3);
                            road.Right.Transition = (int)((head.Flags >> 6) & 3);
                            road.HighPoly = ((head.Flags >> 24) & 1) != 0;

                            // A legacy (pre-template) city road carries a sidewalk each side, sized
                            // in the header flags (TruckLib RoadSerializer: Left.Sidewalk.Size bits
                            // 8-9, Right bits 10-11, IsCityRoad bit 19; SidewalkSize 0 = 8 m,
                            // 1 = 4 m, 2 = 2 m, 3 = 0 m). Measured on every straight legacy road of
                            // the 1.61 map: a city road's own header box reaches road_offset +
                            // road_size + shoulder + that sidewalk (+ about 1 m), 5.21 m past the
                            // shoulder for 4 m, 9.16 m for 8 m, with or without terrain - so the
                            // game's terrain starts outside it. Not reading it left a 4 m strip of
                            // nothing along both sides of every city street round the start.
                            road.IsCityRoad = ((head.Flags >> 19) & 1) != 0;
                            road.Left.SidewalkSize = SidewalkMetres((int)((head.Flags >> 8) & 3));
                            road.Right.SidewalkSize = SidewalkMetres((int)((head.Flags >> 10) & 3));
                            road.Superfine = (road.RoadFlags & 2) != 0;
                            road.MaterialToken = U64(b, o + 100);

                            // TruckLib's RoadSerializer, field by field: road type +4, two traffic
                            // rules, right and left variants +28/+36, then FOUR edges - the right
                            // carriageway's right and left, the left carriageway's right and left
                            // (+44, +52, +60, +68) - profiles, looks +100/+108, and the legacy
                            // material at +116 (which an earlier reading took for the centre brush;
                            // that one lives in the .data payload after the two quad grids).
                            road.RightInnerEdgeToken = U64(b, o + 52);
                            road.LeftInnerEdgeToken = U64(b, o + 60);
                            road.LeftOuterEdgeToken = U64(b, o + 68);
                            road.LegacyMaterialToken = U64(b, o + 116);

                            // Three railing slots from +124, each the right side's (u64 token, i16 offset / 100) and
                            // then the left side's, then the two side height offsets (i32 / 100) - which puts the
                            // start node at +192, where it has always been read.
                            for (int slot = 0; slot < 3; slot++)
                            {
                                int q = o + 124 + slot * 20;
                                ulong rightRailing = U64(b, q), leftRailing = U64(b, q + 10);

                                if (rightRailing != 0) road.Railings = AddRailing(road.Railings, rightRailing, BitConverter.ToInt16(b, q + 8) / 100f, false, slot);
                                if (leftRailing != 0) road.Railings = AddRailing(road.Railings, leftRailing, BitConverter.ToInt16(b, q + 18) / 100f, true, slot);
                            }

                            road.RightHeightOffset = I32(b, o + 184) / 100f;
                            road.LeftHeightOffset = I32(b, o + 188) / 100f;
                            road.StartNode = start;
                            road.EndNode = end;
                            road.Length = F32(b, o + 208);
                            road.BodyLength = 212;
                            Roads.Add(road);
                            return road;
                        }

                    case EtsMapItem.City:
                        {
                            if (o + 24 > limit) return null;
                            float width = F32(b, o + 8), height = F32(b, o + 12);
                            ulong node = U64(b, o + 16);
                            if (node == 0 || float.IsNaN(width) || float.IsNaN(height) || width <= 0f || height <= 0f || width > 20000f || height > 20000f) return null;

                            var city = Copy<EtsMapCity>(head);
                            city.CityToken = U64(b, o);
                            city.Width = width;
                            city.Height = height;
                            city.NodeUid = node;
                            city.BodyLength = 24;
                            Cities.Add(city);
                            return city;
                        }

                    case EtsMapItem.Company:
                        {
                            if (o + 36 > limit) return null;

                            // The first three fields swapped round at map version 903 (TruckLib's
                            // CompanySerializer history). Up to 902 - 898 is game 1.48 - the order is
                            // company token, city token, prefab uid; from 903 on - 907 is game 1.61 -
                            // it is city token, prefab uid, company token. Read the old way on 907,
                            // 0 of 10 companies round the start resolved a company unit, which is why
                            // the job market came up empty. The node at +24, the count at +32 and the
                            // body length are the same on both.
                            bool swapped = MapVersion >= 903;
                            ulong cityToken = swapped ? U64(b, o) : U64(b, o + 8);
                            ulong prefabUid = swapped ? U64(b, o + 8) : U64(b, o + 16);
                            ulong companyToken = swapped ? U64(b, o + 16) : U64(b, o);
                            ulong node = U64(b, o + 24);
                            if (prefabUid == 0 || node == 0) return null;
                            int points = (int)U32(b, o + 32);
                            if (points < 0 || points > 256) return null;
                            int size = 36 + points * 12;
                            if (o + size > limit) return null;

                            var company = Copy<EtsMapCompany>(head);
                            company.CompanyToken = companyToken;
                            company.PrefabUid = prefabUid;
                            company.CityToken = cityToken;
                            company.NodeUid = node;
                            company.SpawnPoints = new ulong[points];
                            for (int i = 0; i < points; i++) company.SpawnPoints[i] = U64(b, o + 36 + i * 8);
                            company.BodyLength = size;
                            Companies.Add(company);
                            return company;
                        }

                    case EtsMapItem.Terrain:
                        {
                            // 898 put the seed straight after the length and the three railings after
                            // that, so the side blocks began at +78. 907 inserts an f32 previous_length
                            // between the length and the seed, moving the seed to +48, the railings to
                            // +52..+82 and the sides to +82. Nothing after that moved. Read this way the
                            // body ends on the next item header for 97,216 of 97,348 terrain items
                            // across all 282 sectors; the 898 offsets land on it 0 times.
                            int sideAt = MapVersion >= 907 ? 82 : 78;

                            if (o + sideAt + 162 > limit) return null;
                            ulong start = U64(b, o), end = U64(b, o + 8);
                            float length = F32(b, o + 40);
                            if (start == 0 || end == 0 || float.IsNaN(length) || length <= 0f || length > 10000f) return null;

                            var terrain = Copy<EtsMapTerrainItem>(head);
                            terrain.StartNode = start;
                            terrain.EndNode = end;
                            for (int i = 0; i < 3; i++) { terrain.StartOffset[i] = F32(b, o + 16 + i * 4); terrain.EndOffset[i] = F32(b, o + 28 + i * 4); }
                            terrain.Length = length;
                            terrain.PreviousLength = MapVersion >= 907 ? F32(b, o + 44) : 0f;
                            terrain.Seed = U32(b, o + (MapVersion >= 907 ? 48 : 44));
                            terrain.StepSize = (int)((head.Flags >> 4) & 3);
                            terrain.Right.Noise = (int)(head.Flags & 3);
                            terrain.Right.Transition = (int)((head.Flags >> 2) & 3);
                            terrain.Left.Noise = (int)((head.Flags >> 24) & 3);
                            terrain.Left.Transition = (int)((head.Flags >> 26) & 3);

                            // Per side: u16 size / 10, profile, coefficient, a second token and float,
                            // three vegetation records, detail vegetation from and to: 78 bytes.
                            int p = o + sideAt;
                            foreach (EtsTerrainSide side in new[] { terrain.Right, terrain.Left })
                            {
                                side.Size = BitConverter.ToUInt16(b, p) / 10f;
                                side.ProfileToken = U64(b, p + 2);
                                side.Coefficient = F32(b, p + 10);
                                side.Vegetation = EtsVegetationRecord.Read(b, p + 26, 3);
                                side.DetailBandFrom = BitConverter.ToUInt16(b, p + 74) / 10f; side.DetailBandTo = BitConverter.ToUInt16(b, p + 76) / 10f;
                                p += 78;
                            }

                            uint spheres = U32(b, p);
                            if (spheres > 4096) return null;
                            p += 4 + (int)spheres * 20;
                            terrain.Right.Quads = EtsTerrainQuadData.Read(b, ref p, limit);
                            terrain.Left.Quads = EtsTerrainQuadData.Read(b, ref p, limit);
                            terrain.RightEdge = U64(b, p);
                            terrain.RightEdgeLook = U64(b, p + 8);
                            terrain.LeftEdge = U64(b, p + 16);
                            terrain.LeftEdgeLook = U64(b, p + 24);
                            p += 32;
                            if (p > limit) return null;
                            terrain.BodyLength = p - o;
                            Terrains.Add(terrain);
                            return terrain;
                        }

                    case EtsMapItem.BezierPatch:
                        {
                            // TruckLib's BezierPatchSerializer: sixteen control points (x index outer, z
                            // inner), u16 tesselation x and z, the node, a seed, three vegetation entries
                            // (token, u16 density, u8 scale), the vegetation spheres (20 bytes each) and
                            // then the quad grid, inline in the body.
                            if (o + 245 > limit) return null;
                            var patch = Copy<EtsMapBezierPatch>(head);
                            for (int i = 0; i < 48; i++) patch.ControlPoints[i] = F32(b, o + i * 4);
                            patch.TesselationX = BitConverter.ToUInt16(b, o + 192);
                            patch.TesselationZ = BitConverter.ToUInt16(b, o + 194);
                            patch.NodeUid = U64(b, o + 196);
                            patch.Seed = U32(b, o + 204);
                            int p = o + 208;
                            for (int i = 0; i < 3; i++) { patch.VegetationTokens[i] = U64(b, p); p += 11; }
                            uint spheres = U32(b, p);
                            if (patch.NodeUid == 0 || spheres > 4096 || patch.TesselationX > 256 || patch.TesselationZ > 256) return null;
                            p += 4 + (int)spheres * 20;
                            if (p > limit) return null;
                            patch.Quads = EtsTerrainQuadData.Read(b, ref p, limit);
                            if (p > limit) return null;
                            patch.BodyLength = p - o;
                            BezierPatches.Add(patch);
                            return patch;
                        }

                    case EtsMapItem.Prefab:
                        {
                            if (o + 24 > limit) return null;
                            int parts = (int)U32(b, o + 16);
                            if (parts < 0 || parts > 64) return null;
                            int p = o + 20 + parts * 8;
                            if (p + 4 > limit) return null;
                            int nodes = (int)U32(b, p);
                            if (nodes <= 0 || nodes > 64) return null;

                            // The SLAVE ITEM list, which this reader used to assume was always
                            // empty. TruckLib's PrefabSerializer reads, after the node list:
                            // ReadItemRefList (a u32 count and that many uids - the service items
                            // a company or garage prefab links to), the ferry link uid, then the
                            // origin. A prefab with even one slave therefore had its origin read
                            // out of the middle of a uid: the DomDepo at the bay came out with
                            // origin 232 and the Scandinavian garage with 22710, where the value
                            // indexes the descriptor's six nodes, and every terrain profile and
                            // coefficient after it was shifted by eight bytes per slave as well.
                            int slaveList = p + 4 + nodes * 8;
                            if (slaveList + 4 > limit) return null;
                            int slaves = (int)U32(b, slaveList);
                            if (slaves < 0 || slaves > 256) return null;
                            int size = 46 + 8 * parts + 20 * nodes + 8 * slaves;
                            if (o + size > limit) return null;

                            var prefab = Copy<EtsMapPrefab>(head);
                            prefab.DlcGuard = (byte)((head.Flags >> 8) & 0xFF);
                            prefab.PrefabToken = U64(b, o);
                            prefab.VariantToken = U64(b, o + 8);
                            prefab.AdditionalParts = new ulong[parts];
                            for (int i = 0; i < parts; i++) prefab.AdditionalParts[i] = U64(b, o + 20 + i * 8);
                            prefab.NodeUids = new ulong[nodes];
                            for (int i = 0; i < nodes; i++)
                            {
                                prefab.NodeUids[i] = U64(b, p + 4 + i * 8);
                                if (prefab.NodeUids[i] == 0) return null;
                            }
                            prefab.SlaveItems = new ulong[slaves];
                            for (int i = 0; i < slaves; i++) prefab.SlaveItems[i] = U64(b, slaveList + 4 + i * 8);
                            int q = slaveList + 4 + slaves * 8;   // the ferry link uid, then the origin
                            prefab.Origin = BitConverter.ToUInt16(b, q + 8);
                            prefab.TerrainProfiles = new ulong[nodes];
                            prefab.TerrainCoefficients = new float[nodes];
                            for (int i = 0; i < nodes; i++)
                            {
                                prefab.TerrainProfiles[i] = U64(b, q + 10 + i * 12);
                                prefab.TerrainCoefficients[i] = F32(b, q + 18 + i * 12);
                            }
                            // The body's last eight bytes: 46 + 8 parts + 20 nodes + 8 slaves = 38 fixed bytes
                            // before the per-node terrain, then this token.
                            prefab.SemaphoreProfile = U64(b, o + size - 8);
                            prefab.BodyLength = size;
                            Prefabs.Add(prefab);
                            return prefab;
                        }

                    case EtsMapItem.Model:
                        {
                            if (o + 28 > limit) return null;
                            int parts = (int)U32(b, o + 24);
                            if (parts < 0 || parts > 64) return null;
                            int size = 64 + parts * 8;
                            if (o + size > limit) return null;
                            int n = o + 28 + parts * 8;
                            ulong nodeUid = U64(b, n);
                            if (nodeUid == 0) return null;

                            // A NEGATIVE scale is a mirrored model, not a broken body. The map uses
                            // it freely - a shop, a house, a road arrow flipped to face the other
                            // way - and refusing it threw away 50 headers in the nine sectors round
                            // the bay, 42 of them genuine top-level models with their node in the
                            // sector table. One of them, /model2/building/shop/shop_big_03_balt.pmd
                            // at scale (-1, 1, 1), is the 32 x 50 m building whose footprint was
                            // the single largest hole in the ground. Only the magnitude is checked.
                            for (int i = 0; i < 3; i++)
                            {
                                float s = Math.Abs(F32(b, n + 8 + i * 4));
                                if (float.IsNaN(s) || s <= 0.0001f || s > 1000f) return null;
                            }

                            var model = Copy<EtsMapModel>(head);
                            model.ModelToken = U64(b, o);
                            model.LookToken = U64(b, o + 8);
                            model.VariantToken = U64(b, o + 16);
                            model.AdditionalParts = new ulong[parts];
                            for (int i = 0; i < parts; i++) model.AdditionalParts[i] = U64(b, o + 28 + i * 8);
                            model.NodeUid = nodeUid;
                            model.Scale = new[] { F32(b, n + 8), F32(b, n + 12), F32(b, n + 16) };
                            model.TerrainMaterial = U64(b, n + 20);
                            model.TerrainColor = U32(b, n + 28);
                            model.BodyLength = size;
                            Models.Add(model);
                            return model;
                        }

                    case EtsMapItem.Building:
                        {
                            if (o + 48 > limit) return null;
                            ulong start = U64(b, o + 16), end = U64(b, o + 24);
                            float span = F32(b, o + 32);
                            if (start == 0 || end == 0 || float.IsNaN(span) || span <= 0f || span > 10000f) return null;
                            int count = (int)U32(b, o + 44);
                            if (count < 0 || count > 4096) return null;
                            int size = 48 + count * 4;
                            if (o + size > limit) return null;

                            var building = Copy<EtsMapBuilding>(head);
                            building.DlcGuard = (byte)((head.Flags >> 8) & 0xFF);
                            building.SchemeToken = U64(b, o);
                            building.LookToken = U64(b, o + 8);
                            building.StartNode = start;
                            building.EndNode = end;
                            building.Length = F32(b, o + 32);
                            building.Seed = U32(b, o + 36);
                            building.Stretch = F32(b, o + 40);
                            building.Heights = new float[count];
                            for (int i = 0; i < count; i++) building.Heights[i] = F32(b, o + 48 + i * 4);
                            building.BodyLength = size;
                            Buildings.Add(building);
                            return building;
                        }

                    case EtsMapItem.CutPlane:
                        {
                            if (o + 4 > limit) return null;
                            int count = (int)U32(b, o);
                            if (count <= 0 || count > 256 || o + 4 + count * 8 > limit) return null;
                            for (int i = 0; i < count; i++) if (U64(b, o + 4 + i * 8) == 0) return null;
                            head.BodyLength = 4 + count * 8;
                            return head;
                        }

                    case EtsMapItem.Curve:
                        {
                            if (MapVersion >= 907) return DecodeCurve907(head, b, o, limit);

                            if (o + 112 > limit) return null;
                            ulong node = U64(b, o + 8), forward = U64(b, o + 16);
                            float length = F32(b, o + 40);
                            if (node == 0 || forward == 0 || node == forward) return null;
                            if (float.IsNaN(length) || length < 0f || length > 2000f) return null;
                            int count = (int)U32(b, o + 108);
                            if (count < 0 || count > 4096) return null;
                            int size = 112 + count * 4;
                            if (o + size > limit) return null;

                            var curve = Copy<EtsMapCurve>(head);
                            curve.ModelToken = U64(b, o);
                            curve.NodeUid = node;
                            curve.ForwardNodeUid = forward;
                            curve.Locator0 = U64(b, o + 24);
                            curve.Locator1 = U64(b, o + 32);
                            curve.Length = length;
                            curve.Seed = U32(b, o + 44);
                            curve.Stretch = F32(b, o + 48);
                            curve.Scale = F32(b, o + 52);
                            curve.FixedStep = F32(b, o + 56);
                            curve.TerrainMaterial = U64(b, o + 60);
                            curve.TerrainColor = U32(b, o + 68);
                            curve.TerrainRotation = F32(b, o + 72);
                            curve.FirstPart = U64(b, o + 76);
                            curve.LastPart = U64(b, o + 84);
                            curve.VariationToken = U64(b, o + 92);
                            curve.LookToken = U64(b, o + 100);
                            curve.Heights = new float[count];
                            for (int i = 0; i < count; i++) curve.Heights[i] = F32(b, o + 112 + i * 4);
                            curve.BodyLength = size;
                            Curves.Add(curve);
                            return curve;
                        }

                    case EtsMapItem.Hookup:
                        {
                            if (o + 16 > limit) return null;
                            ulong length = U64(b, o);
                            if (length == 0 || length > 128 || o + 8 + (int)length + 8 > limit) return null;

                            for (int i = 0; i < (int)length; i++)
                            {
                                if (b[o + 8 + i] < 32 || b[o + 8 + i] > 126) return null;
                            }

                            ulong node = U64(b, o + 8 + (int)length);
                            if (node == 0) return null;

                            var hookup = Copy<EtsMapHookup>(head);
                            hookup.PoolName = System.Text.Encoding.ASCII.GetString(b, o + 8, (int)length);
                            hookup.NodeUid = node;
                            hookup.BodyLength = 8 + (int)length + 8;
                            Hookups.Add(hookup);
                            return hookup;
                        }

                    case EtsMapItem.Sign:
                        {
                            if (o + 41 > limit) return null;
                            ulong node = U64(b, o + 8);
                            if (node == 0) return null;
                            int boards = b[o + 32];
                            int p = o + 33 + boards * 24;
                            if (boards > 32 || p + 8 > limit) return null;
                            ulong templateLength = U64(b, p);
                            if (templateLength > 128 || p + 8 + (int)templateLength > limit) return null;
                            int q = p + 8 + (int)templateLength;

                            // An untemplated sign ends here. A templated one carries two arrays (see
                            // EtsMapSign): the per-area records, then the overrides, each value's width
                            // given by its type - 1 byte, 4, 4, 4, a u64-prefixed string, or 8.
                            EtsMapSignArea[] areas = null;
                            EtsMapSignOverride[] overrideList = null;

                            if (templateLength > 0)
                            {
                                if (q + 4 > limit) return null;
                                int areaCount = (int)U32(b, q);
                                if (areaCount < 0 || areaCount > 64) return null;
                                q += 4;
                                areas = new EtsMapSignArea[areaCount];

                                for (int i = 0; i < areaCount; i++)
                                {
                                    if (q + 9 > limit) return null;
                                    var area = new EtsMapSignArea { Area = U64(b, q), Flags = b[q + 8] };
                                    q += 9;
                                    if ((area.Flags & ~3) != 0) return null;
                                    if ((area.Flags & 1) != 0) { if (q + 2 > limit) return null; area.Value = BitConverter.ToUInt16(b, q); q += 2; }
                                    if ((area.Flags & 2) != 0) { if (q + 8 > limit) return null; area.Board = U64(b, q); q += 8; }
                                    areas[i] = area;
                                }

                                if (q + 4 > limit) return null;
                                int overrides = (int)U32(b, q);
                                if (overrides < 0 || overrides > 512) return null;
                                q += 4;
                                overrideList = new EtsMapSignOverride[overrides];

                                for (int i = 0; i < overrides; i++)
                                {
                                    if (q + 16 > limit) return null;
                                    int attributes = (int)U32(b, q + 12);
                                    if (attributes < 0 || attributes > 64) return null;
                                    var entry = new EtsMapSignOverride { Id = U32(b, q), Area = U64(b, q + 4), Attributes = new EtsMapSignAttribute[attributes] };
                                    q += 16;

                                    for (int a = 0; a < attributes; a++)
                                    {
                                        if (q + 6 > limit) return null;
                                        var attribute = new EtsMapSignAttribute { Type = BitConverter.ToUInt16(b, q), Index = U32(b, q + 2) };
                                        q += 6;

                                        switch (attribute.Type)
                                        {
                                            case 1: if (q + 1 > limit) return null; attribute.Number = b[q]; q += 1; break;
                                            case 2: case 3: if (q + 4 > limit) return null; attribute.Number = U32(b, q); q += 4; break;
                                            case 4: if (q + 4 > limit) return null; attribute.Float = F32(b, q); q += 4; break;
                                            case 5:
                                                {
                                                    if (q + 8 > limit) return null;
                                                    ulong text = U64(b, q);
                                                    if (text > 4096 || q + 8 + (int)text > limit) return null;
                                                    attribute.Text = System.Text.Encoding.UTF8.GetString(b, q + 8, (int)text);
                                                    q += 8 + (int)text;
                                                    break;
                                                }
                                            case 6: if (q + 8 > limit) return null; attribute.Token = U64(b, q); q += 8; break;
                                            default: return null;
                                        }

                                        entry.Attributes[a] = attribute;
                                    }

                                    overrideList[i] = entry;
                                }
                            }

                            if (q > limit) return null;

                            var sign = Copy<EtsMapSign>(head);
                            sign.ModelToken = U64(b, o);
                            sign.NodeUid = node;
                            sign.LookToken = U64(b, o + 16);
                            sign.VariantToken = U64(b, o + 24);
                            if (templateLength > 0) sign.Template = System.Text.Encoding.ASCII.GetString(b, p + 8, (int)templateLength);
                            if (areas != null) sign.Areas = areas;
                            if (overrideList != null) sign.Overrides = overrideList;

                            if (boards > 0)
                            {
                                sign.Boards = new ulong[boards * 3];
                                for (int i = 0; i < boards * 3; i++) sign.Boards[i] = U64(b, o + 33 + i * 8);
                            }
                            sign.BodyLength = q - o;
                            Signs.Add(sign);
                            return sign;
                        }

                    case EtsMapItem.Compound:
                        {
                            if (o + 12 > limit) return null;
                            ulong parentNode = U64(b, o);
                            int children = (int)U32(b, o + 8);
                            if (parentNode == 0 || children <= 0 || children > 512) return null;

                            var compound = Copy<EtsMapCompound>(head);
                            compound.NodeUid = parentNode;
                            int p = o + 12;

                            for (int i = 0; i < children; i++)
                            {
                                if (p + 57 > limit) return null;
                                EtsMapItem childHead = ReadHead(b, p);
                                if (childHead.Type <= 0 || childHead.Type > 60 || childHead.Uid == 0) return null;

                                // A child whose layout is unknown ends the compound: its length
                                // cannot be measured from inside, so the whole item falls back to
                                // the walk's own scan rather than guessing where the block ends.
                                EtsMapItem child = DecodeBody(childHead, b, p + 57, limit);
                                if (child == null) return null;

                                child.BodyOffset = p + 57;
                                compound.Children.Add(child);
                                p += 57 + child.BodyLength;
                            }

                            if (p + 4 > limit) return null;
                            int nodes = (int)U32(b, p);
                            if (nodes < 0 || nodes > 4096 || p + 4 + nodes * 56 > limit) return null;

                            // The node block, in the sector's own 56-byte format. Reading it is the
                            // whole point: without it none of the children can be placed.
                            for (int i = 0; i < nodes; i++)
                            {
                                int q = p + 4 + i * 56;
                                var node = new EtsMapNode { Uid = U64(b, q), BackwardItem = U64(b, q + 36), ForwardItem = U64(b, q + 44), Flags = U32(b, q + 52) };
                                node.Position[0] = I32(b, q + 8) / 256f;
                                node.Position[1] = I32(b, q + 12) / 256f;
                                node.Position[2] = I32(b, q + 16) / 256f;
                                node.Rotation[3] = F32(b, q + 20);
                                node.Rotation[0] = F32(b, q + 24);
                                node.Rotation[1] = F32(b, q + 28);
                                node.Rotation[2] = F32(b, q + 32);
                                compound.OwnNodeUids.Add(node.Uid);

                                if (!NodesByUid.ContainsKey(node.Uid))
                                {
                                    Nodes.Add(node);
                                    NodesByUid[node.Uid] = node;
                                }
                            }

                            compound.BodyLength = p + 4 + nodes * 56 - o;
                            Compounds.Add(compound);
                            return compound;
                        }
                }
            }
            catch (Exception)
            {
                // Fall through to measuring.
            }

            return null;
        }

        /// <summary>
        /// Curve (44) on map version 907, TruckLib's current CurveSerializer: the shared fields
        /// moved to the front (node, forward node, two locators, length, a u32 use mask) and the
        /// model fields became a list of subcurves behind that mask - 40 + the sum of
        /// (100 + 4 * height offsets) per set bit.
        ///
        /// Measured on the nine sectors round the 1.61 start, over all 9,418 curves the walk then
        /// exposed: the length this computes lands exactly on the next item header on 8,880 and is
        /// NEVER too long; the other 538 are the last child of a compound and land exactly on that
        /// compound's own node table, whose 4 + 56n the compound decoder then consumes - 538 of 538
        /// checked, none unexplained. Every model token names a unit in /def/world/curve_model.sii,
        /// and the subcurve's own fields read true: first part 'start1'/'s1', look 'default'/'new'/
        /// 'no_reflect', the colour word taking only twelve values, 0x00FFFFFF on 9,326.
        ///
        /// NOT measured: the u32 at +36 is 1 on every curve in the demo, so these bytes cannot tell
        /// a bit mask from a subcurve count. TruckLib says mask (up to four subcurves, taken by
        /// popcount) and this follows TruckLib; the first curve with two subcurves will settle it.
        /// </summary>
        private EtsMapCurve DecodeCurve907(EtsMapItem head, byte[] b, int o, int limit)
        {
            if (o + 40 > limit) return null;
            ulong node = U64(b, o), forward = U64(b, o + 8);
            if (node == 0 || forward == 0 || node == forward) return null;
            float length = F32(b, o + 32);
            if (float.IsNaN(length) || length < 0f || length > 2000f) return null;

            uint mask = U32(b, o + 36);
            if (mask == 0 || (mask & ~0xFu) != 0) return null;

            var curve = Copy<EtsMapCurve>(head);
            curve.NodeUid = node;
            curve.ForwardNodeUid = forward;
            curve.Locator0 = U64(b, o + 16);
            curve.Locator1 = U64(b, o + 24);
            curve.Length = length;

            int p = o + 40;
            bool first = true;

            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                if (p + 100 > limit) return null;
                int n = (int)U32(b, p + 76);
                if (n < 0 || n > 8192 || p + 100 + n * 4 > limit) return null;

                if (first)
                {
                    first = false;
                    curve.ModelToken = U64(b, p);
                    curve.SubcurveFlags = U32(b, p + 8);
                    curve.Seed = U32(b, p + 12);
                    curve.Stretch = F32(b, p + 16);
                    curve.Scale = F32(b, p + 20);
                    curve.FixedStep = F32(b, p + 24);
                    curve.TerrainMaterial = U64(b, p + 28);
                    curve.TerrainColor = U32(b, p + 36);
                    curve.TerrainRotation = F32(b, p + 40);
                    curve.FirstPart = U64(b, p + 44);
                    curve.LastPart = U64(b, p + 52);
                    curve.VariationToken = U64(b, p + 60);
                    curve.LookToken = U64(b, p + 68);
                    curve.Heights = new float[n];
                    for (int h = 0; h < n; h++) curve.Heights[h] = F32(b, p + 80 + h * 4);
                }

                p += 100 + n * 4;
            }

            curve.BodyLength = p - o;
            Curves.Add(curve);
            return curve;
        }

        private static T Copy<T>(EtsMapItem head) where T : EtsMapItem, new()
        {
            var item = new T { Type = head.Type, Uid = head.Uid, Flags = head.Flags, ViewDistance = head.ViewDistance };
            Array.Copy(head.KdopMin, item.KdopMin, 5);
            Array.Copy(head.KdopMax, item.KdopMax, 5);
            return item;
        }

        // ---------------------------------------------------------------------- headers

        private bool IsHeader(byte[] b, int o, float sx, float sz, int limit)
        {
            if (o + 57 > limit) return false;
            uint type = U32(b, o);
            if (type == 0 || type > 60) return false;
            ulong uid = U64(b, o + 4);
            if (uid == 0 || (uid & 0xFFFFFFFFUL) == 0 || (uid >> 32) == 0 || NodesByUid.ContainsKey(uid)) return false;

            // Item uids mostly ascend through a file and mostly share the top of their uid, but
            // MEASURED, not assumed: enforcing either as a hard rule costs far more than it saves.
            // Requiring ascending uids plus a common prefix dropped the map from 97% of declared
            // items found to 29%, and stripped one depot's surroundings from 416 real items to 5,
            // because the walk then refused the genuine header it had landed on and skipped ahead.
            // The kdop test below stays the only gate.
            return KdopPlausible(b, o + 12, sx, sz);
        }

        private int ScanHeader(byte[] b, int from, float sx, float sz, int limit)
        {
            for (int o = from; o + 57 <= limit; o++)
            {
                if (IsHeader(b, o, sx, sz, limit))
                {
                    return o;
                }
            }

            return -1;
        }

        /// <summary>
        /// The bounding volume of a real item: five min/max pairs, axes 3 and 4 the diagonals
        /// (x+z)/2 and (x-z)/2 of the x/z box, the whole thing near this sector.
        /// </summary>
        private static bool KdopPlausible(byte[] b, int o, float sx, float sz)
        {
            var mn = new float[5];
            var mx = new float[5];
            bool any = false;

            for (int i = 0; i < 5; i++)
            {
                mn[i] = F32(b, o + i * 4);
                mx[i] = F32(b, o + 20 + i * 4);

                if (float.IsNaN(mn[i]) || float.IsNaN(mx[i]) || float.IsInfinity(mn[i]) || float.IsInfinity(mx[i]) || mn[i] > mx[i] + 0.01f)
                {
                    return false;
                }

                if (mn[i] != 0f || mx[i] != 0f)
                {
                    any = true;
                }
            }

            if (!any) return false;
            if (mn[0] < sx - 2500f || mx[0] > sx + 6500f || mn[2] < sz - 2500f || mx[2] > sz + 6500f) return false;
            if (mn[1] < -2000f || mx[1] > 5000f) return false;
            if ((mx[0] - mn[0]) + (mx[2] - mn[2]) < 0.01f) return false;

            const float tol = 1f;
            float d1lo = (mn[0] + mn[2]) / 2, d1hi = (mx[0] + mx[2]) / 2, d2lo = (mn[0] - mx[2]) / 2, d2hi = (mx[0] - mn[2]) / 2;

            if (mn[3] < d1lo - tol || mx[3] > d1hi + tol || mn[4] < d2lo - tol || mx[4] > d2hi + tol) return false;

            // At least ONE diagonal must carry the item's extent. Requiring it of axis 3 alone
            // refused every genuine item that runs north-west to south-east: such an item keeps
            // x + z nearly constant, so axis 3 is thin while axis 4 carries the whole span. The
            // walk then lost the header and skipped forward - 1232 of the 33953 items declared in
            // the nine sectors round the bay went unbuilt, and fifteen prefab data records ended
            // on an item that was never registered. Measured on eleven refused headers sitting
            // exactly at the end of a correctly parsed body (roads and prefabs with sane boxes,
            // e.g. a prefab 249 m across whose axis 3 spans 63 m and axis 4 241 m), the larger of
            // the two diagonals passes every one.
            float diagonal = Math.Max(mx[3] - mn[3], mx[4] - mn[4]);
            float box = Math.Max(d1hi - d1lo, d2hi - d2lo);

            if (diagonal < box * 0.3f - tol) return false;

            return true;
        }

        // ---------------------------------------------------------------------- nodes

        /// <summary>Bytes in one node record: uid, fixed-point position, quaternion, backward and forward item, flags.</summary>
        private const int NodeSize = 56;

        /// <summary>The largest node count believed: 200,000 records is 11 MB, about the largest .aux file of the demo.</summary>
        private const uint MaxNodeCount = 200000;

        /// <summary>
        /// The node table: a u32 count followed by that many 56-byte records, each a fixed-point
        /// position and a unit quaternion (LooksLikeNode). It is not always the last thing in the
        /// file - larger sectors carry more after it - so it is found wherever it is: every offset
        /// is tried as a count, and the largest count that is followed by at least that many
        /// node-like records wins.
        ///
        /// EVERY offset, because a record read 4 bytes before its place still looks like a node
        /// whenever the node's rotation has no z part (a yaw-only node): its "x" is the top half of
        /// the uid, a small int, and its "quaternion" is the z position's bits (a denormal, ~0) plus
        /// w, x, y, which are of unit length. The walk used to jump to the end of each run it
        /// measured, so such a run starting just before the real table hid it (probe 2090):
        /// - sec-0006+0004.base (Dijon): a count of 179 four bytes before the real one (920) was
        ///   "confirmed" by 179 shifted records, all with x = 10549.9 in a sector spanning
        ///   -24000..-20000, and the real table was jumped over;
        /// - sec+0000+0003.base (Munich): a shifted run of 76 under a count of 131 was refused but
        ///   still jumped over, and a stray 5-record table 8 KB on won, so 1,527 nodes were lost.
        /// Probe 2091 over all 564 .base and .aux files of the 1.61 demo: 17 files read a wrong
        /// table this way (each 1 to 179 nodes, where the real one has 270 to 12,635), among them
        /// sec-0002+0002.aux, which stopped its item walk at 455 KB of 4.3 MB. With every offset
        /// tried, all 564 tables have at least 95 % of their nodes inside their own sector grown by
        /// 1 km (a shifted table has none), and the other 547 files read exactly as before.
        ///
        /// Trying every offset naively re-measured the same records from every count field inside a
        /// big table (probe 2093: 2.27 M node tests for 3,069 nodes in sec-0005-0001.base), so:
        /// - two byte tests go first: a count of 1..200000 has a zero top byte and a second byte of
        ///   at most 3, and the first record's x (within 100 km, 0x0186A000) has a top byte of 0, 1,
        ///   0xFE or 0xFF - nearly every offset stops there;
        /// - the run measured on each of the 56 byte grids is remembered, so a count field inside a
        ///   run already measured on its grid reuses that run's end.
        /// Probes 2094-2095: the same table as the plain every-offset walk on all 564 files, and
        /// 4.6-4.8 s for all of them against 7.1-7.3 s for the old walk under Mono (16.5 ms per
        /// sector on average, both files; the slowest file 107-123 ms, a 12.7 MB .aux, was 163-173).
        ///
        /// A run also goes on over a node whose rotation is four zeros (ZeroRotationNode). The
        /// 95 % test above cannot see a table that was never found, and one was not: checker probes
        /// 2102-2105 looked for node-like runs the world leaves unread and found exactly one, in
        /// sec-0002-0005.aux (north of the Bremen HQ, inside its 1,700 m read ring): its real table,
        /// 4,099 records at 3,801,007, holds at slot 1,945 node 54ce158a518f66cd at (-5477.4, 6.0,
        /// -17259.6) with w, x, y, z = -0.0, 0, 0, 0. That one record ended the run, the count was
        /// refused, and a stray count of 32 at 289,833 won - so the item walk stopped 289 KB into
        /// the 4.0 MB file (984 of 3,300 items) and 4,099 nodes were never read. Probe 2104: with
        /// such records allowed, that file alone changes of the 564 (32 -> 4,099, all of them
        /// inside the sector + 1 km) and it is the only zero-rotation node in any chosen table.
        /// </summary>
        private static int FindNodeTable(byte[] b, out int nodeCount)
        {
            nodeCount = 0;
            int best = -1;
            int bestCount = 0;

            // Per byte grid (offset mod 56): the last run of node-like records measured on it, [start, end).
            var runStart = new int[NodeSize];
            var runEnd = new int[NodeSize];

            for (int o = 24; o + NodeSize <= b.Length; o++)
            {
                if (b[o - 1] != 0 || b[o - 2] > 3)
                {
                    continue;
                }

                uint count = U32(b, o - 4);

                // A count no larger than the best already found cannot win.
                if (count == 0 || count > MaxNodeCount || count <= (uint)bestCount)
                {
                    continue;
                }

                byte top = b[o + 11];

                if (top > 1 && top < 0xFE)
                {
                    continue;
                }

                int grid = o % NodeSize;
                int end;

                if (o >= runStart[grid] && o < runEnd[grid])
                {
                    // Inside a run measured from an earlier count on this grid: every record from o
                    // to its end looks like a node, and the one at its end does not (or the file ends).
                    end = runEnd[grid];
                }
                else
                {
                    end = o;

                    while (end + NodeSize <= b.Length && (LooksLikeNode(b, end) || ZeroRotationNode(b, end)))
                    {
                        end += NodeSize;
                    }

                    runStart[grid] = o;
                    runEnd[grid] = end;
                }

                if ((end - o) / NodeSize >= count)
                {
                    best = o - 4;
                    bestCount = (int)count;
                }
            }

            nodeCount = bestCount;
            return best;
        }

        private static bool LooksLikeNode(byte[] b, int o)
        {
            for (int i = 0; i < 3; i++)
            {
                // In long, because a stray 0x80000000 in the bytes being tested would make
                // Math.Abs on an int throw rather than answer.
                long v = I32(b, o + 8 + i * 4);

                // The demo's nodes reach x = 29,449 m; the full map goes further. A hundred
                // kilometres is well past any of it and still far inside the int range.
                if (v > 100000L * 256 || v < -100000L * 256) return false;
            }

            float w = F32(b, o + 20), x = F32(b, o + 24), y = F32(b, o + 28), z = F32(b, o + 32);

            if (float.IsNaN(w) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;

            double l = Math.Sqrt(w * w + x * x + y * y + z * z);
            return Math.Abs(l - 1.0) < 0.01;
        }

        /// <summary>
        /// A node record whose rotation is four zero floats (-0.0 counts: the one such node of the demo stores w as
        /// 0x80000000), with a non-zero uid and a position LooksLikeNode would take. Only FindNodeTable's run
        /// measurement accepts it, inside a table a count has announced; never on its own. See FindNodeTable.
        /// </summary>
        private static bool ZeroRotationNode(byte[] b, int o)
        {
            if (U64(b, o) == 0)
            {
                return false;
            }

            for (int i = 0; i < 3; i++)
            {
                long v = I32(b, o + 8 + i * 4);

                if (v > 100000L * 256 || v < -100000L * 256) return false;
            }

            return F32(b, o + 20) == 0f && F32(b, o + 24) == 0f && F32(b, o + 28) == 0f && F32(b, o + 32) == 0f;
        }
    }
}
