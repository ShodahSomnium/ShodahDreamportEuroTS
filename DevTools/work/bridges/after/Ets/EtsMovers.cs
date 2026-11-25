using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A mover (map item type 9): animated models that stand at a node or walk a path of nodes - the map's
    /// pedestrians, grazing cows and sheep, horses, dogs, chickens, the eagles circling over the fields, gulls, and
    /// also flags, wind turbines, ships, trains, balloons and harvesters. Walkers (type 28) were deprecated in 1.36
    /// and the 1.61 map has none: every pedestrian is a mover.
    ///
    /// Body (TruckLib MoverSerializer, map 907), after the common 57-byte item head: u32 tag count + u64 tags,
    /// u64 model token (a unit "mover.&lt;token&gt;" of /def/world/mover*.sii), u64 look, u64 variant, f32 speed,
    /// f32 end delay, f32 width, u32 count, u32 + f32 cached segment lengths, u32 + u64 node uids. Measured on the
    /// whole 1.61 demo map (living probe 2): 9,145 movers in 282 sectors, every one in the .aux file, every body
    /// ending exactly where the sector reader's scan put the next item header (0 mismatches), every first node in
    /// the sector's own node table.
    ///
    /// Head flags (TruckLib Mover): bit 0 curved path, 1 active by day, 2 by night, 3 bounce at the end, 4 follow
    /// the path's direction, 5 water reflection, 6 ignore cut planes, 9 no sound, 10 nice weather, 11 bad weather,
    /// 12 prefer a non-moving animation, 13 uniform placement, 14 keep orientation on bounce.
    ///
    /// Pure C#: read on the worker thread from the sector's own bytes (the sector reader keeps only offsets).
    /// </summary>
    public sealed class EtsMapMover
    {
        public ulong Uid;
        public uint Flags;
        public byte ViewDistance;
        public ulong[] Tags = new ulong[0];
        public ulong ModelToken, LookToken, VariantToken;
        public float Speed, EndDelay, Width;
        public int Count;
        public float[] Lengths = new float[0];
        public ulong[] NodeUids = new ulong[0];

        /// <summary>Node positions (x, y, z, game frame) and rotations (x, y, z, w), resolved from the sector; null where a node was not found.</summary>
        public float[][] NodePositions = new float[0][];
        public float[][] NodeRotations = new float[0][];

        public string Model => EtsToken.Decode(ModelToken);
        public string Look => EtsToken.Decode(LookToken);
        public string Variant => EtsToken.Decode(VariantToken);

        public bool CurvedPath => (Flags & 1u) != 0;
        public bool ByDay => (Flags & 2u) != 0;
        public bool ByNight => (Flags & 4u) != 0;
        public bool Bounce => (Flags & 8u) != 0;
        public bool FollowDir => (Flags & 16u) != 0;
        public bool NiceWeather => (Flags & 1024u) != 0;
        public bool BadWeather => (Flags & 2048u) != 0;
        public bool PreferStatic => (Flags & 4096u) != 0;
        public bool Uniform => (Flags & 8192u) != 0;
        public bool KeepOrientationOnBounce => (Flags & 16384u) != 0;

        /// <summary>
        /// Whether the game shows it on a clear day, which is the only weather and time this world has (EtsClimate
        /// applies the nice-weather profile of the demo's 10:00 start): by day, or with no time bits at all; not
        /// bad-weather-only. 9,010 of the 9,145 movers pass.
        /// </summary>
        public bool ShownOnClearDay => (ByDay || !ByNight) && !(BadWeather && !NiceWeather);

        /// <summary>Nodes that resolved, in order.</summary>
        public int ResolvedNodes
        {
            get
            {
                int n = 0;
                foreach (float[] p in NodePositions) if (p != null) n++;
                return n;
            }
        }

        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static ulong U64(byte[] b, int o) => BitConverter.ToUInt64(b, o);
        private static float F32(byte[] b, int o) => BitConverter.ToSingle(b, o);

        /// <summary>Decodes a mover body at o. False on anything implausible; length is the body's size.</summary>
        public static bool TryDecode(byte[] b, int o, int limit, out EtsMapMover mover, out int length)
        {
            mover = null;
            length = 0;

            try
            {
                if (b == null || o < 0 || o + 4 > limit || limit > b.Length) return false;
                int p = o;
                uint tags = U32(b, p); p += 4;
                if (tags > 64 || p + tags * 8 + 44 > limit) return false;
                var m = new EtsMapMover { Tags = new ulong[tags] };
                for (int i = 0; i < tags; i++) { m.Tags[i] = U64(b, p); p += 8; }
                m.ModelToken = U64(b, p); p += 8;
                m.LookToken = U64(b, p); p += 8;
                m.VariantToken = U64(b, p); p += 8;
                m.Speed = F32(b, p); p += 4;
                m.EndDelay = F32(b, p); p += 4;
                m.Width = F32(b, p); p += 4;
                uint count = U32(b, p); p += 4;
                if (p + 4 > limit) return false;
                uint lengths = U32(b, p); p += 4;
                if (lengths > 4096 || p + lengths * 4 + 4 > limit) return false;
                m.Lengths = new float[lengths];
                for (int i = 0; i < lengths; i++) { m.Lengths[i] = F32(b, p); p += 4; }
                uint nodes = U32(b, p); p += 4;
                if (nodes == 0 || nodes > 4096 || p + nodes * 8 > limit) return false;
                m.NodeUids = new ulong[nodes];
                for (int i = 0; i < nodes; i++) { m.NodeUids[i] = U64(b, p); p += 8; }

                if (m.ModelToken == 0 || count > 1000 || float.IsNaN(m.Speed) || float.IsNaN(m.Width) || float.IsNaN(m.EndDelay) ||
                    m.Speed < 0f || m.Speed > 500f || m.Width < 0f || m.Width > 1000f)
                {
                    return false;
                }

                m.Count = (int)Math.Max(1u, count);
                length = p - o;
                mover = m;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Every mover of a sector, from its .aux and .base bytes (movers live in .aux; .base is tried too). Each
        /// type-9 item the sector reader found is decoded at its body offset once the 57-byte head just before it
        /// is confirmed to be that item (type and uid), and its nodes are looked up in the sector's node table.
        /// Movers inside compounds are not reached: a compound holding one is not decoded by the sector reader.
        /// </summary>
        public static List<EtsMapMover> ReadSector(EtsMapSector sector, byte[] aux, byte[] baseBytes, out int failed)
        {
            var result = new List<EtsMapMover>();
            failed = 0;

            if (sector == null)
            {
                return result;
            }

            foreach (EtsMapItem item in sector.Items)
            {
                if (item.Type != EtsMapItem.Mover)
                {
                    continue;
                }

                EtsMapMover mover = null;

                foreach (byte[] b in new[] { aux, baseBytes })
                {
                    int o = item.BodyOffset;

                    if (b == null || o < 57 || o >= b.Length || U32(b, o - 57) != EtsMapItem.Mover || U64(b, o - 53) != item.Uid)
                    {
                        continue;
                    }

                    if (TryDecode(b, o, b.Length, out mover, out int length))
                    {
                        break;
                    }
                }

                if (mover == null)
                {
                    failed++;
                    continue;
                }

                mover.Uid = item.Uid;
                mover.Flags = item.Flags;
                mover.ViewDistance = item.ViewDistance;
                mover.NodePositions = new float[mover.NodeUids.Length][];
                mover.NodeRotations = new float[mover.NodeUids.Length][];

                for (int i = 0; i < mover.NodeUids.Length; i++)
                {
                    if (sector.NodesByUid.TryGetValue(mover.NodeUids[i], out EtsMapNode node))
                    {
                        mover.NodePositions[i] = (float[])node.Position.Clone();
                        mover.NodeRotations[i] = (float[])node.Rotation.Clone();
                    }
                }

                result.Add(mover);
            }

            return result;
        }
    }

    /// <summary>
    /// A bone animation (.pma version 5) as the movers use it. EtsAnimation reads the same version for the cab,
    /// but most mover animations carry a flag word it does not know: +4 is u16 frame count and u16 FLAGS (walk_01:
    /// 0x0078 frames, flags 6; read as one u32, 393,336 frames). Measured on the demo (living probes 6-7):
    ///
    ///     0 u32 version 5 | 4 u16 frames, u16 flags | 8 u32 bones | 12 u64 skeleton hash (the pmg's +24) |
    ///     20 float3 bounding sphere centre, 32 radius | 36 float3 box min, 48 box max | 60 f32 length (s) |
    ///     64 offset of the frame durations (f32 each) | 68 offset of the bone indices (u8 each) |
    ///     72 offset of the keys (56 bytes: stretch quat, rotation quat (w first), translation, scale; frame-major) |
    ///     76 offset of the per-frame MOVEMENT (float3, flag 2) | 80 offset of the per-frame turn (quat w first, flag 4)
    ///
    /// The movement is the model's root motion, a delta per frame along its local -z (forward): man walk_01 4.44 m in
    /// 3.97 s (1.12 m/s), woman 1.27 m/s, the new-generation walk 1.10, the German shepherd 1.36, the cyclist 3.31,
    /// a chicken 0.20; idle and eating animations have no flag and do not move. The bones themselves stay in place,
    /// so the mover item moves the model and the clip only swings the limbs. Keys carry scale too: the eagles'
    /// joints are scaled 1.2-1.3.
    /// </summary>
    public sealed class EtsMoverAnim
    {
        public string Path;
        public int FrameCount, BoneCount, Flags;
        public float Length;
        public ulong SkeletonHash;
        public float[] FrameTimes;
        public int[] BoneIndices;

        /// <summary>Per (frame * BoneCount + bone): rotation x y z w (4), translation (3), scale (3), game frame.</summary>
        public float[] Rotations, Translations, Scales;

        /// <summary>Metres the animation carries the model forward over one loop, and per second: 0 for one that stands.</summary>
        public float Travel;
        public float NaturalSpeed => Length > 1e-3f ? Travel / Length : 0f;

        /// <summary>Whether any key scales a bone (so the clip needs scale curves).</summary>
        public bool HasScale;

        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static float F32(byte[] b, int o) => BitConverter.ToSingle(b, o);

        /// <summary>The skeleton hash alone, from the head (for choosing an animation that fits a model without reading it all).</summary>
        public static bool TryReadHash(byte[] b, out ulong hash)
        {
            hash = 0;
            if (b == null || b.Length < 84 || U32(b, 0) != 5) return false;
            hash = U32(b, 12) | ((ulong)U32(b, 16) << 32);
            return true;
        }

        public static EtsMoverAnim Parse(byte[] b, string path)
        {
            if (b == null || b.Length < 84)
            {
                throw new ArgumentException(path + ": pma too short.");
            }

            if (U32(b, 0) != 5)
            {
                throw new ArgumentException(path + ": pma version " + U32(b, 0) + " (expected 5).");
            }

            var a = new EtsMoverAnim
            {
                Path = path,
                FrameCount = b[4] | (b[5] << 8),
                Flags = b[6] | (b[7] << 8),
                BoneCount = (int)U32(b, 8),
                SkeletonHash = U32(b, 12) | ((ulong)U32(b, 16) << 32),
                Length = F32(b, 60)
            };

            int lengths = (int)U32(b, 64), bones = (int)U32(b, 68), keys = (int)U32(b, 72), moves = (int)U32(b, 76);

            if (a.FrameCount <= 0 || a.BoneCount <= 0 || a.BoneCount > 255 ||
                lengths < 84 || lengths + a.FrameCount * 4 > b.Length || bones < 0 || bones + a.BoneCount > b.Length ||
                keys < 0 || keys + (long)a.FrameCount * a.BoneCount * 56 > b.Length)
            {
                throw new ArgumentException(path + ": pma tables do not fit (" + a.FrameCount + " frames, " + a.BoneCount + " bones, flags " + a.Flags + ").");
            }

            a.FrameTimes = new float[a.FrameCount];
            float time = 0f;

            for (int f = 0; f < a.FrameCount; f++)
            {
                a.FrameTimes[f] = time;
                time += F32(b, lengths + f * 4);
            }

            if (!(a.Length > 0f) || float.IsNaN(a.Length))
            {
                a.Length = a.FrameTimes[a.FrameCount - 1];
            }

            a.BoneIndices = new int[a.BoneCount];
            for (int k = 0; k < a.BoneCount; k++) a.BoneIndices[k] = b[bones + k];

            int slots = a.FrameCount * a.BoneCount;
            a.Rotations = new float[slots * 4];
            a.Translations = new float[slots * 3];
            a.Scales = new float[slots * 3];

            for (int s = 0; s < slots; s++)
            {
                int o = keys + s * 56;
                a.Rotations[s * 4 + 3] = F32(b, o + 16);
                a.Rotations[s * 4] = F32(b, o + 20);
                a.Rotations[s * 4 + 1] = F32(b, o + 24);
                a.Rotations[s * 4 + 2] = F32(b, o + 28);
                a.Translations[s * 3] = F32(b, o + 32);
                a.Translations[s * 3 + 1] = F32(b, o + 36);
                a.Translations[s * 3 + 2] = F32(b, o + 40);

                for (int i = 0; i < 3; i++)
                {
                    float v = F32(b, o + 44 + i * 4);
                    if (float.IsNaN(v) || v <= 0f) v = 1f;
                    a.Scales[s * 3 + i] = v;
                    if (Math.Abs(v - 1f) > 1e-3f) a.HasScale = true;
                }
            }

            // Root motion (flag 2): a float3 per frame, the step since the previous frame. Its length summed is the
            // distance one loop walks; the direction is the model's forward (-z) in every file measured.
            if ((a.Flags & 2) != 0 && moves > 0 && moves + a.FrameCount * 12 <= b.Length)
            {
                double x = 0, y = 0, z = 0;

                for (int f = 0; f < a.FrameCount; f++)
                {
                    x += F32(b, moves + f * 12);
                    y += F32(b, moves + f * 12 + 4);
                    z += F32(b, moves + f * 12 + 8);
                }

                a.Travel = (float)Math.Sqrt(x * x + z * z);
                if (float.IsNaN(a.Travel)) a.Travel = 0f;
            }

            return a;
        }
    }

    /// <summary>
    /// The skinning data of a pmg (version 0x15) that EtsModelData does not keep: per piece, the offsets at +88 and
    /// +92 point at four u8 bone indices and four u8 weights per vertex, inside the piece's interleaved vertex
    /// (stride at +60). Measured on the demo's movers (living probe 5): header +16 is 4 (weights per vertex) on
    /// every skinned model and 0 on the unskinned gull cards; every index is below the skeleton's bone count and
    /// the four weights of every vertex sum to 255 (human 21 and 68 bones, cow 32, sheep 25, horse 39, dog 41,
    /// chicken 5, eagles 5 - one joint per bird).
    /// </summary>
    public sealed class EtsModelSkin
    {
        /// <summary>Per piece: 4 bone indices and 4 weights (0-255) per vertex; null for a piece without skin.</summary>
        public byte[][] Indices = new byte[0][];
        public byte[][] Weights = new byte[0][];

        public bool Any
        {
            get
            {
                foreach (byte[] i in Indices) if (i != null) return true;
                return false;
            }
        }

        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);

        public static EtsModelSkin Read(byte[] pmg, int boneCount)
        {
            var skin = new EtsModelSkin();

            if (pmg == null || pmg.Length < 112 || pmg[0] != 0x15)
            {
                return skin;
            }

            int pieces = (int)U32(pmg, 4);
            int piecesOffset = (int)U32(pmg, 84);

            if (pieces <= 0 || pieces > 4096 || piecesOffset < 0 || piecesOffset + pieces * 100 > pmg.Length)
            {
                return skin;
            }

            skin.Indices = new byte[pieces][];
            skin.Weights = new byte[pieces][];

            for (int p = 0; p < pieces; p++)
            {
                int o = piecesOffset + p * 100;
                int verts = (int)U32(pmg, o + 4), stride = I32(pmg, o + 60), bi = I32(pmg, o + 88), bw = I32(pmg, o + 92);

                if (boneCount <= 0 || verts <= 0 || stride <= 0 || bi < 0 || bw < 0 ||
                    bi + (long)(verts - 1) * stride + 4 > pmg.Length || bw + (long)(verts - 1) * stride + 4 > pmg.Length)
                {
                    continue;
                }

                var idx = new byte[verts * 4];
                var wts = new byte[verts * 4];
                bool ok = true;

                for (int v = 0; v < verts && ok; v++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        byte i = pmg[bi + v * stride + k], w = pmg[bw + v * stride + k];
                        if (w > 0 && i >= boneCount) { ok = false; break; }
                        idx[v * 4 + k] = w > 0 ? i : (byte)0;
                        wts[v * 4 + k] = w;
                    }
                }

                if (ok)
                {
                    skin.Indices[p] = idx;
                    skin.Weights[p] = wts;
                }
            }

            return skin;
        }
    }

    /// <summary>What a mover shows, one choice of model: the model and its lods, plus the looks and variants to pick from.</summary>
    public sealed class EtsMoverDesc
    {
        public string Name;
        public string Model;

        /// <summary>Lod models and the distances from which each is used; an empty path hides the model from there.</summary>
        public string[] Lods = new string[0];
        public float[] LodDistances = new float[0];
        public string[] Variants = new string[0];
        public string[] Looks = new string[0];
    }

    /// <summary>What a mover model token stands for: one model with one animation (mover_model) or a tag-picked set of models and animations (mover_model_group).</summary>
    public sealed class EtsMoverKind
    {
        public string Token;
        public string Name;
        public bool Group;
        public bool Pedestrian;
        public readonly List<EtsMoverDesc> Descs = new List<EtsMoverDesc>();
        public readonly List<string> Anims = new List<string>();
        public string[] DescTags = new string[0];
        public string[] AnimTags = new string[0];

        /// <summary>pedestrian, animal, bird or other: what the log counts it as and which distance limit it has.</summary>
        public string Category = "other";
    }

    /// <summary>
    /// The mover definitions: /def/world/mover*.sii (mover_model and mover_model_group, 917 units in the 1.61 demo),
    /// mover_desc*.sii (628 models with group_tags) and mover_anim*.sii (313 animations with group_tags). A group
    /// "collects all mover desc containing all tags from desc_tags and all mover anim containing all tags from
    /// anim_tags", then "randomly chooses one of the collected mover desc and then tries to choose one of the
    /// collected mover anim which use the same skeleton" (modding wiki, Mover model group). Pure; built once.
    /// </summary>
    public sealed class EtsMoverCatalog
    {
        private readonly Dictionary<string, EtsSiiUnit> _movers = new Dictionary<string, EtsSiiUnit>(StringComparer.Ordinal);
        private readonly List<EtsSiiUnit> _descs = new List<EtsSiiUnit>();
        private readonly List<EtsSiiUnit> _anims = new List<EtsSiiUnit>();
        private readonly Dictionary<string, EtsMoverKind> _kinds = new Dictionary<string, EtsMoverKind>(StringComparer.Ordinal);
        private readonly EtsFileSystem _files;

        public int MoverUnits => _movers.Count;
        public int DescUnits => _descs.Count;
        public int AnimUnits => _anims.Count;

        public EtsMoverCatalog(EtsFileSystem files)
        {
            _files = files;
            EtsSiiFile movers = files.ReadDefinitionFamily("/def/world", "mover");

            foreach (EtsSiiUnit u in movers.Units)
            {
                if (u.Name != null && u.Name.StartsWith("mover.", StringComparison.Ordinal)) _movers[u.Name.Substring(6)] = u;
            }

            _descs.AddRange(files.ReadDefinitionFamily("/def/world", "mover_desc").Units);
            _anims.AddRange(files.ReadDefinitionFamily("/def/world", "mover_anim").Units);
        }

        private static string[] Strings(EtsSiiUnit u, string key)
        {
            List<string> list = u.GetArray(key);
            if (list == null || list.Count == 0) return new string[0];
            var result = new string[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = EtsSiiValue.Unquote(list[i]);
            return result;
        }

        private static float[] Floats(EtsSiiUnit u, string key)
        {
            string[] s = Strings(u, key);
            var f = new float[s.Length];
            for (int i = 0; i < s.Length; i++) f[i] = EtsSiiValue.ParseFloat(s[i], 0f);
            return f;
        }

        private static bool HasAll(string[] have, string[] want)
        {
            foreach (string w in want)
            {
                if (Array.IndexOf(have, w) < 0) return false;
            }

            return true;
        }

        /// <summary>The kind a mover's model token names, or null when the definitions do not have it.</summary>
        public EtsMoverKind Kind(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (_kinds.TryGetValue(token, out EtsMoverKind cached)) return cached;
            EtsMoverKind kind = null;

            if (_movers.TryGetValue(token, out EtsSiiUnit u))
            {
                kind = new EtsMoverKind { Token = token, Name = EtsSiiValue.Unquote(u.GetString("mover_name", token)), Pedestrian = u.GetBool("pedestrian") };

                if (u.Type == "mover_model_group")
                {
                    kind.Group = true;
                    kind.DescTags = Strings(u, "desc_tags");
                    kind.AnimTags = Strings(u, "anim_tags");

                    if (kind.DescTags.Length > 0)
                    {
                        foreach (EtsSiiUnit d in _descs)
                        {
                            if (!HasAll(Strings(d, "group_tags"), kind.DescTags)) continue;
                            string model = EtsSiiValue.Unquote(d.GetString("model_desc", ""));
                            if (model.Length == 0) continue;
                            kind.Descs.Add(new EtsMoverDesc
                            {
                                Name = d.Name, Model = model, Lods = Strings(d, "lods"), LodDistances = Floats(d, "lod_distances"),
                                Variants = Strings(d, "variants"), Looks = Strings(d, "looks")
                            });
                        }
                    }

                    if (kind.AnimTags.Length > 0)
                    {
                        foreach (EtsSiiUnit a in _anims)
                        {
                            if (!HasAll(Strings(a, "group_tags"), kind.AnimTags)) continue;
                            string anim = EtsSiiValue.Unquote(a.GetString("model_anim", ""));
                            if (anim.Length > 0 && !kind.Anims.Contains(anim)) kind.Anims.Add(anim);
                        }
                    }
                }
                else
                {
                    string model = EtsSiiValue.Unquote(u.GetString("model_desc", ""));

                    if (model.Length > 0)
                    {
                        kind.Descs.Add(new EtsMoverDesc { Name = u.Name, Model = model, Lods = Strings(u, "lods"), LodDistances = Floats(u, "lod_dist") });
                    }

                    string anim = EtsSiiValue.Unquote(u.GetString("model_anim", ""));
                    if (anim.Length > 0) kind.Anims.Add(anim);
                }

                kind.Category = Categorise(kind);
            }

            _kinds[token] = kind;
            return kind;
        }

        /// <summary>
        /// Pedestrian, animal, bird or other, from what the definitions say: a group marked pedestrian or a model
        /// under the character folders is a pedestrian; eagles, gulls, ducks, herons and storks are birds; the rest
        /// of /model/mover/animal and /asset/character/animal is animals.
        /// </summary>
        public static string Categorise(EtsMoverKind kind)
        {
            string all = kind.Token + " " + string.Join(" ", kind.DescTags) + " " + string.Join(" ", kind.AnimTags);
            foreach (EtsMoverDesc d in kind.Descs) all += " " + d.Model;
            all = all.ToLowerInvariant();

            if (all.Contains("eagle") || all.Contains("gull") || all.Contains("duck") || all.Contains("heron") || all.Contains("stork") || all.Contains("bird") || all.Contains("pigeon"))
            {
                return "bird";
            }

            if (all.Contains("/animal/") || all.Contains("/character/animal") || all.Contains("horse") || all.Contains(" cow") || all.Contains("sheep") || all.Contains("chicken") ||
                all.Contains("rabbit") || all.Contains("deer") || all.Contains("shepherd") || all.Contains(" dog") || all.Contains(" cat"))
            {
                return "animal";
            }

            if (kind.Pedestrian || all.Contains("/characters/") || all.Contains("/crowd/") || all.Contains("/character/human") || all.Contains("/serviceman/") || all.Contains(" human"))
            {
                return "pedestrian";
            }

            return "other";
        }
    }

    /// <summary>
    /// The choice made for one mover instance: which model (and lods), look, variant and animation - made on the
    /// worker thread, where the files are read, so the frame only builds. Seeded by the item uid and the instance
    /// index, so every client picks the same people for the same path.
    /// </summary>
    public sealed class EtsMoverPick
    {
        public string Model;
        public string[] Lods = new string[0];
        public float[] LodDistances = new float[0];
        public string Look;
        public string Variant;
        public string Anim;
        public string Failure;

        /// <summary>
        /// Picks for a kind. 'moving' asks for an animation that walks (root motion) and 'standing' for one that does
        /// not, when the kind has both (a cow_all path of grazing cows must not play the walk). Model and animation
        /// must share a skeleton: the hashes are read from the files' heads (cached by path in 'hashes').
        /// </summary>
        public static EtsMoverPick Choose(EtsFileSystem files, EtsMoverKind kind, string itemLook, string itemVariant, bool moving, int seed,
                                          Dictionary<string, ulong> hashes, Dictionary<string, float> speeds)
        {
            var pick = new EtsMoverPick();

            if (kind == null || kind.Descs.Count == 0)
            {
                pick.Failure = "no model defined";
                return pick;
            }

            var rng = new Random(seed);
            int start = rng.Next(kind.Descs.Count);

            for (int attempt = 0; attempt < kind.Descs.Count; attempt++)
            {
                EtsMoverDesc desc = kind.Descs[(start + attempt) % kind.Descs.Count];

                if (!files.Exists(desc.Model) || !files.Exists(ModelGeometry(desc.Model)))
                {
                    pick.Failure = "model " + desc.Model + " is not in the archives";
                    continue;
                }

                ulong modelHash = Hash(files, ModelGeometry(desc.Model), true, hashes);
                string anim = null;

                if (kind.Anims.Count > 0)
                {
                    var fits = new List<string>();
                    var preferred = new List<string>();

                    foreach (string a in kind.Anims)
                    {
                        if (!files.Exists(a)) continue;
                        ulong h = Hash(files, a, false, hashes);
                        if (h == 0 || (modelHash != 0 && h != modelHash)) continue;
                        fits.Add(a);
                        float speed = Speed(files, a, speeds);
                        if ((speed > 0.05f) == moving) preferred.Add(a);
                    }

                    List<string> from = preferred.Count > 0 ? preferred : fits;

                    if (from.Count == 0)
                    {
                        pick.Failure = "no animation of " + kind.Token + " fits the skeleton of " + desc.Model;
                        continue;
                    }

                    anim = from[rng.Next(from.Count)];
                }

                pick.Model = desc.Model;
                pick.Lods = desc.Lods;
                pick.LodDistances = desc.LodDistances;
                pick.Anim = anim;
                pick.Look = !string.IsNullOrEmpty(itemLook) && itemLook != "default" ? itemLook : desc.Looks.Length > 0 ? desc.Looks[rng.Next(desc.Looks.Length)] : itemLook;
                pick.Variant = !string.IsNullOrEmpty(itemVariant) && itemVariant != "default" ? itemVariant : desc.Variants.Length > 0 ? desc.Variants[rng.Next(desc.Variants.Length)] : itemVariant;
                pick.Failure = null;
                return pick;
            }

            return pick;
        }

        public static string ModelGeometry(string pmd)
        {
            return pmd.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase) ? pmd.Substring(0, pmd.Length - 4) + ".pmg" : pmd + ".pmg";
        }

        private static ulong Hash(EtsFileSystem files, string path, bool geometry, Dictionary<string, ulong> hashes)
        {
            if (hashes.TryGetValue(path, out ulong h)) return h;
            byte[] b = files.Read(path);
            h = 0;

            if (geometry)
            {
                if (b != null && b.Length >= 32) h = BitConverter.ToUInt64(b, 24);
            }
            else
            {
                EtsMoverAnim.TryReadHash(b, out h);
            }

            hashes[path] = h;
            return h;
        }

        private static float Speed(EtsFileSystem files, string path, Dictionary<string, float> speeds)
        {
            if (speeds.TryGetValue(path, out float s)) return s;

            try
            {
                s = EtsMoverAnim.Parse(files.Read(path), path).NaturalSpeed;
            }
            catch (Exception)
            {
                s = 0f;
            }

            speeds[path] = s;
            return s;
        }
    }
}
