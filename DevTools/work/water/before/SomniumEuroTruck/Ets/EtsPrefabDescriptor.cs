using System;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A prefab descriptor (.ppd): where a prefab's road ends are, so the model can be stood on
    /// the map's nodes. Everything else in the file - navigation curves, signs, semaphores,
    /// spawn points, terrain points, map points - is counted and skipped for now.
    ///
    /// Versions 22 to 25 (the 1.61.1.0 demo ships 25 throughout, 1.48 shipped 23), read off
    /// /prefab/bridge/a4_koln_bridge.ppd: counts at 4..44, offsets at 48..92, node records of 104
    /// bytes at the node offset - terrain point range (two u32), variant range (two u32), position
    /// (float3), direction (float3), eight input lanes and eight output lanes (i32 each).
    /// </summary>
    public sealed class EtsPrefabDescriptor
    {
        public sealed class Node
        {
            public float[] Position = new float[3];
            public float[] Direction = new float[3];

            /// <summary>This node's run of terrain points, all variants together.</summary>
            public int TerrainPointStart;
            public int TerrainPointCount;

            /// <summary>
            /// This node's entries in the variant table: one (start, length) pair per model
            /// variant, relative to TerrainPointStart. Zero entries means the run is not split.
            /// </summary>
            public int VariantStart;
            public int VariantCount;
        }

        /// <summary>
        /// The terrain points of one node for one model variant: an index into TerrainPoints /
        /// TerrainNormals and how many. The layout is the one TruckLib.Models reads: a node's run
        /// of points is shared by all the model's variants, and the variant table says which slice
        /// each variant uses (a prefab with two variants keeps two 14-point borders per node, and a
        /// 14-point border is a 13-column terrain, which is exactly what the map's corner says).
        /// </summary>
        public void NodePointRange(int nodeIndex, int variantIndex, out int start, out int count)
        {
            Node node = Nodes[nodeIndex];
            start = node.TerrainPointStart;
            count = node.TerrainPointCount;

            if (node.VariantCount > 0)
            {
                int v = Math.Max(0, Math.Min(variantIndex, node.VariantCount - 1));
                int entry = node.VariantStart + v;

                if (entry >= 0 && entry * 2 + 1 < TerrainVariants.Length)
                {
                    start = node.TerrainPointStart + TerrainVariants[entry * 2];
                    count = TerrainVariants[entry * 2 + 1];
                }
            }

            int total = TerrainPoints.Length / 3;

            if (start < 0 || start >= total)
            {
                count = 0;
            }
            else if (start + count > total)
            {
                count = total - start;
            }
        }

        /// <summary>
        /// A place the game stands a vehicle: the position and facing, in the prefab's own space,
        /// and what it is for.
        ///
        /// This is how Euro Truck Simulator puts a truck down. The record is float3 position at +0,
        /// float4 rotation at +12 stored W FIRST, u32 type at +28, and on descriptor version 24 and
        /// up a u32 flags field at +32 - so 32 bytes through version 23 and 36 from 24 - laid out
        /// from the offset at byte 64 of the descriptor. Verified rather than assumed: over all
        /// 5,546 descriptors of the 1.61.1.0 demo, all version 25, the stride the file's own
        /// count/offset table yields is 36 on every one of the 349 that carry spawn points, the
        /// four floats at +12 are a unit quaternion on 1,925 of 1,925, the dword at +28 takes 17
        /// values which are all real spawn point types, and the dword at +32 is zero on 1,764.
        /// </summary>
        public sealed class SpawnPoint
        {
            public float[] Position = new float[3];
            public float[] Rotation = new float[4];
            public int Type;
        }

        /// <summary>Where a truck parks at a company. This is the one a delivery starts from.</summary>
        public const int SpawnCompany = 13;

        /// <summary>A parking bay, and a garage: both places a truck is meant to stand.</summary>
        public const int SpawnParking = 10;
        public const int SpawnGarage = 14;

        /// <summary>Where a trailer stands waiting to be coupled.</summary>
        public const int SpawnTrailer = 1;

        /// <summary>Where a delivery is dropped: the easy, medium and hard unloading bays.</summary>
        public const int SpawnUnloadEasy = 2;
        public const int SpawnUnloadMedium = 19;
        public const int SpawnUnloadHard = 20;

        /// <summary>The rest of TruckLib.Models' SpawnPointType, checked against its Enums.cs: a rigid
        /// truck's unloading place, a company unload, a weigh station category point, an owned
        /// trailer's place, and a long trailer's loading place. All appear round the 1.61 start.</summary>
        public const int SpawnUnloadRigid = 21;
        public const int SpawnWeighStationCat = 22;
        public const int SpawnCompanyUnload = 23;
        public const int SpawnTrailerSpawn = 24;
        public const int SpawnLongTrailer = 25;

        /// <summary>
        /// A sign the prefab carries (52 bytes at version 25, the stride the file's own offset table gives on
        /// all 522 of the demo's descriptors that have signs, harness Program1103): token name at +0 (a Blender
        /// locator name), float3 position at +8, rotation at +20 stored W FIRST, token model at +36 (a
        /// "sign.&lt;token&gt;" unit of /def/world/sign.sii, the same units a sign map item names), token part at
        /// +44 - the prefab model part the sign belongs to ("vis" on nearly all), so it shows with that part.
        /// Round the two starts (probe Det5) they are street lamps, yard lamps and give-way signs: 190 in the
        /// 38 prefabs within 1.2 km of the Szczecin start, 172 in 22 round Cambridge.
        /// </summary>
        public sealed class Sign
        {
            public ulong Name;
            public float[] Position = new float[3];

            /// <summary>x, y, z, w (reordered from the file's w first).</summary>
            public float[] Rotation = { 0f, 0f, 0f, 1f };

            public ulong Model;
            public ulong Part;
        }

        /// <summary>
        /// A traffic light or barrier locator (84 bytes at version 25 - TruckLib.Models Ppd/Semaphore.cs
        /// Deserialize19, and the file's own offset table on all 707 descriptors that have one): float3
        /// position at +0, rotation at +12 stored W FIRST, u32 type at +28 (0 use the profile, 1 model only,
        /// 2 traffic light, 3 minor, 4 major, 5/6/8/10 barriers, 7 blockable, 9 virtual), u32 id at +32 (what a
        /// nav curve's semaphore id names), float4 intervals at +36 (green, yellow, red, yellow seconds) and
        /// f32 cycle delay at +52 - both used when the type is not 0 - token profile at +56
        /// (tr_sem_prof.&lt;token&gt;), then u32 and u32[4] not understood (0 on every one round the starts).
        /// Round the Szczecin start 27 semaphores on 9 prefabs, all type 0, profiles cr_1x1, cr_2x1, cr_1x2,
        /// cr_2x2; round Cambridge 25 on 3 prefabs (probe Det1).
        /// </summary>
        public sealed class Semaphore
        {
            public float[] Position = new float[3];

            /// <summary>x, y, z, w (reordered from the file's w first).</summary>
            public float[] Rotation = { 0f, 0f, 0f, 1f };

            public int Type;
            public int Id;
            public float[] Intervals = new float[4];
            public float CycleDelay;
            public ulong Profile;
        }

        public int Version;
        public Node[] Nodes = new Node[0];
        public SpawnPoint[] SpawnPoints = new SpawnPoint[0];
        public Sign[] Signs = new Sign[0];
        public Semaphore[] Semaphores = new Semaphore[0];

        /// <summary>The first spawn point of a given type, or null.</summary>
        public SpawnPoint FindSpawn(int type)
        {
            for (int i = 0; i < SpawnPoints.Length; i++)
            {
                if (SpawnPoints[i].Type == type)
                {
                    return SpawnPoints[i];
                }
            }

            return null;
        }
        public int NavCurveCount;
        public int SignCount;
        public int SemaphoreCount;
        public int SpawnPointCount;
        public int TerrainPointCount;
        public int MapPointCount;
        public int TriggerPointCount;

        /// <summary>
        /// Terrain points: three floats per point, the border the prefab's terrain grows from. The
        /// file keeps the positions as one array and the NORMALS as a second array of the same
        /// length after it (TruckLib.Models reads it so) - an earlier reading took them as six
        /// floats per point and interleaved the two, which scrambled every border.
        /// </summary>
        public float[] TerrainPoints = new float[0];

        /// <summary>The outward direction at each terrain point, three floats per point.</summary>
        public float[] TerrainNormals = new float[0];

        /// <summary>The variant table: (start, length) pairs, relative to a node's own run of points.</summary>
        public int[] TerrainVariants = new int[0];

        public static EtsPrefabDescriptor Parse(byte[] b)
        {
            if (b == null || b.Length < 96)
            {
                throw new ArgumentException("Prefab descriptor too short.");
            }

            var ppd = new EtsPrefabDescriptor
            {
                Version = BitConverter.ToInt32(b, 0),
                NavCurveCount = BitConverter.ToInt32(b, 8),
                SignCount = BitConverter.ToInt32(b, 12),
                SemaphoreCount = BitConverter.ToInt32(b, 16),
                SpawnPointCount = BitConverter.ToInt32(b, 20),
                TerrainPointCount = BitConverter.ToInt32(b, 24),
                MapPointCount = BitConverter.ToInt32(b, 32),
                TriggerPointCount = BitConverter.ToInt32(b, 36)
            };

            int nodeCount = BitConverter.ToInt32(b, 4);
            int nodeOffset = BitConverter.ToInt32(b, 48);

            if (nodeCount < 0 || nodeCount > 64 || nodeOffset < 0 || nodeOffset + nodeCount * 104 > b.Length)
            {
                throw new ArgumentException("Prefab descriptor node table out of range (version " + ppd.Version + ").");
            }


            // Counts at 4..44 in the order node, nav curve, sign, semaphore, spawn point, terrain
            // point, terrain point variant, map point, trigger point, intersection, nav node; the
            // twelve offsets at 48.. follow the same order with the terrain points taking two -
            // positions at 68, normals at 72 - and the variant table at 76.
            int terrainPoints = BitConverter.ToInt32(b, 24);
            int positionsOffset = BitConverter.ToInt32(b, 68);
            int normalsOffset = BitConverter.ToInt32(b, 72);

            if (terrainPoints > 0 && terrainPoints < 100000 && positionsOffset > 0 && positionsOffset + terrainPoints * 12 <= b.Length)
            {
                ppd.TerrainPoints = new float[terrainPoints * 3];

                for (int i = 0; i < terrainPoints * 3; i++)
                {
                    ppd.TerrainPoints[i] = BitConverter.ToSingle(b, positionsOffset + i * 4);
                }

                if (normalsOffset > 0 && normalsOffset + terrainPoints * 12 <= b.Length)
                {
                    ppd.TerrainNormals = new float[terrainPoints * 3];

                    for (int i = 0; i < terrainPoints * 3; i++)
                    {
                        ppd.TerrainNormals[i] = BitConverter.ToSingle(b, normalsOffset + i * 4);
                    }
                }
            }

            int variantEntries = BitConverter.ToInt32(b, 28);
            int variantsOffset = BitConverter.ToInt32(b, 76);

            if (variantEntries > 0 && variantEntries < 100000 && variantsOffset > 0 && variantsOffset + variantEntries * 8 <= b.Length)
            {
                ppd.TerrainVariants = new int[variantEntries * 2];

                for (int i = 0; i < variantEntries * 2; i++)
                {
                    ppd.TerrainVariants[i] = BitConverter.ToInt32(b, variantsOffset + i * 4);
                }
            }
            int spawnCount = BitConverter.ToInt32(b, 20);
            int spawnOffset = BitConverter.ToInt32(b, 64);

            // The record grew a u32 flags field at the END when the descriptor format went 23 -> 25
            // (TruckLib.Models Ppd/SpawnPoint.cs: position + rotation + type is 32 bytes through
            // version 23; version 24 and up adds Flags, 36). Taken from the files' own tables, not
            // guessed: over all 5,546 descriptors the 1.61.1.0 demo ships - every one version 25 -
            // (next section's offset - spawn offset) / count is 36 on all 349 that carry spawn
            // points and never 32, and at 36 every section of all 5,546 tiles its file exactly.
            // Read at 32 on a 9-point descriptor, only point 0 survived; the rest came back with
            // nonsense positions, types and quaternions of length up to 20.
            int spawnStride = ppd.Version >= 24 ? 36 : 32;

            if (spawnCount > 0 && spawnCount < 4096 && spawnOffset > 0 && spawnOffset + spawnCount * spawnStride <= b.Length)
            {
                ppd.SpawnPoints = new SpawnPoint[spawnCount];

                for (int i = 0; i < spawnCount; i++)
                {
                    int o = spawnOffset + i * spawnStride;
                    var point = new SpawnPoint { Type = BitConverter.ToInt32(b, o + 28) };

                    for (int k = 0; k < 3; k++)
                    {
                        point.Position[k] = BitConverter.ToSingle(b, o + k * 4);
                    }

                    // Stored w first, as every SCS quaternion is; kept here as (x, y, z, w) because
                    // that is how EtsModelBuilder.ToUnityRotation reads it. Not version-gated:
                    // TruckLib has one w-first reader serving both the 32 and 36 byte records, so
                    // this was a standing bug rather than a version 25 change. Ground truth round the
                    // 1.61 start: 357 of 369 spawn points put through their prefab's transform land
                    // on a real map node to 0.05 m, and w first reproduces that node's own heading on
                    // 357 of 357 where the old order managed 192 - most of the misses 180 degrees out.
                    // Version 23 files could not be re-measured, the 1.61 update having left none.
                    point.Rotation[3] = BitConverter.ToSingle(b, o + 12);
                    point.Rotation[0] = BitConverter.ToSingle(b, o + 16);
                    point.Rotation[1] = BitConverter.ToSingle(b, o + 20);
                    point.Rotation[2] = BitConverter.ToSingle(b, o + 24);

                    ppd.SpawnPoints[i] = point;
                }
            }

            ppd.Nodes = new Node[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                int o = nodeOffset + i * 104;
                var node = new Node
                {
                    TerrainPointStart = BitConverter.ToInt32(b, o),
                    TerrainPointCount = BitConverter.ToInt32(b, o + 4),
                    VariantStart = BitConverter.ToInt32(b, o + 8),
                    VariantCount = BitConverter.ToInt32(b, o + 12)
                };

                for (int k = 0; k < 3; k++)
                {
                    node.Position[k] = BitConverter.ToSingle(b, o + 16 + k * 4);
                    node.Direction[k] = BitConverter.ToSingle(b, o + 28 + k * 4);
                }

                ppd.Nodes[i] = node;
            }

            // Signs and semaphores. A failure here must never cost the prefab its nodes and spawn points, which
            // everything else stands on, so they are read last and on their own.
            try
            {
                ReadSignsAndSemaphores(ppd, b);
            }
            catch (Exception)
            {
                ppd.Signs = new Sign[0];
                ppd.Semaphores = new Semaphore[0];
            }

            return ppd;
        }

        /// <summary>
        /// The record size of one section, from the file's own table: the next section's offset less this one's, over
        /// the count. Sections are laid out in the table's order (sign at 56, semaphore at 60, spawn point at 64), so
        /// the next non-empty offset after this one bounds it. 0 when it cannot be told.
        /// </summary>
        private static int Stride(byte[] b, int count, int offsetAt)
        {
            int offset = BitConverter.ToInt32(b, offsetAt);

            if (count <= 0 || offset <= 0)
            {
                return 0;
            }

            for (int at = offsetAt + 4; at <= 92; at += 4)
            {
                int next = BitConverter.ToInt32(b, at);

                if (next > offset)
                {
                    return (next - offset) % count == 0 ? (next - offset) / count : 0;
                }
            }

            return (b.Length - offset) % count == 0 ? (b.Length - offset) / count : 0;
        }

        private static void ReadSignsAndSemaphores(EtsPrefabDescriptor ppd, byte[] b)
        {
            int signCount = BitConverter.ToInt32(b, 12);
            int signOffset = BitConverter.ToInt32(b, 56);

            // 52 bytes on every version the demo ships; a file whose table says otherwise is left unread.
            if (signCount > 0 && signCount < 4096 && Stride(b, signCount, 56) == 52 && signOffset + signCount * 52 <= b.Length)
            {
                ppd.Signs = new Sign[signCount];

                for (int i = 0; i < signCount; i++)
                {
                    int o = signOffset + i * 52;
                    var sign = new Sign
                    {
                        Name = BitConverter.ToUInt64(b, o),
                        Model = BitConverter.ToUInt64(b, o + 36),
                        Part = BitConverter.ToUInt64(b, o + 44)
                    };

                    for (int k = 0; k < 3; k++) sign.Position[k] = BitConverter.ToSingle(b, o + 8 + k * 4);
                    sign.Rotation[3] = BitConverter.ToSingle(b, o + 20);
                    for (int k = 0; k < 3; k++) sign.Rotation[k] = BitConverter.ToSingle(b, o + 24 + k * 4);
                    ppd.Signs[i] = sign;
                }
            }

            int semaphoreCount = BitConverter.ToInt32(b, 16);
            int semaphoreOffset = BitConverter.ToInt32(b, 60);
            int stride = Stride(b, semaphoreCount, 60);

            // 84 bytes at version 25 (four u32 more than the 68-byte record of 21-24, which is otherwise the same).
            if (semaphoreCount > 0 && semaphoreCount < 4096 && (stride == 84 || stride == 68) && semaphoreOffset + semaphoreCount * stride <= b.Length)
            {
                ppd.Semaphores = new Semaphore[semaphoreCount];

                for (int i = 0; i < semaphoreCount; i++)
                {
                    int o = semaphoreOffset + i * stride;
                    var semaphore = new Semaphore
                    {
                        Type = BitConverter.ToInt32(b, o + 28),
                        Id = BitConverter.ToInt32(b, o + 32),
                        CycleDelay = BitConverter.ToSingle(b, o + 52),
                        Profile = BitConverter.ToUInt64(b, o + 56)
                    };

                    for (int k = 0; k < 3; k++) semaphore.Position[k] = BitConverter.ToSingle(b, o + k * 4);
                    semaphore.Rotation[3] = BitConverter.ToSingle(b, o + 12);
                    for (int k = 0; k < 3; k++) semaphore.Rotation[k] = BitConverter.ToSingle(b, o + 16 + k * 4);
                    for (int k = 0; k < 4; k++) semaphore.Intervals[k] = BitConverter.ToSingle(b, o + 36 + k * 4);
                    ppd.Semaphores[i] = semaphore;
                }
            }
        }
    }
}
