using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>A sound_item_data unit (/def/world/sound_item_data.sii, sound_item_ambient.sii): what a map sound item plays.</summary>
    public sealed class EtsMapSoundDef
    {
        public string Unit;
        /// <summary>sound_path: the event ("/sound/world/ambients/forest.bank#ambient/forest/temp_meadow_1").</summary>
        public string Path;
        /// <summary>sound_sfx and sfx_count: an area's extra event (the forest's birds_2_eu_temp scatterers), placed that many times.</summary>
        public string Sfx;
        public int SfxCount;
        /// <summary>max_dist (m): a point sound's reach; an area's fade-out distance past its edge.</summary>
        public float MaxDist = 50f;
    }

    /// <summary>One map sound item (type 21, in the sector's .snd file).</summary>
    public sealed class EtsMapSoundItem
    {
        public ulong Uid;
        /// <summary>Kdop flags bit 0: an ambient area (a width x height rectangle round its node); else a point sound at its node.</summary>
        public bool Area;
        /// <summary>Kdop flags bit 1: a reverb area (no sound of its own).</summary>
        public bool Reverb;
        public string Token;
        public float X, Y, Z;
        public float[] Rotation; // x, y, z, w (game frame)
        public float Width, Height;
        public EtsMapSoundDef Def;

        /// <summary>Distance (m, in plan) from a game-frame point to the area's rectangle (0 inside), or to the point.</summary>
        public float DistanceTo(float gx, float gz)
        {
            float dx = gx - X, dz = gz - Z;
            if (!Area) return (float)Math.Sqrt(dx * dx + dz * dz);
            // Into the node's frame (the inverse of its rotation): width along local x, height along local z.
            float[] q = Rotation;
            float[] local = EtsMapNode.Rotate(new[] { -q[0], -q[1], -q[2], q[3] }, dx, 0f, dz);
            float ox = Math.Max(0f, Math.Abs(local[0]) - Width * 0.5f), oz = Math.Max(0f, Math.Abs(local[2]) - Height * 0.5f);
            return (float)Math.Sqrt(ox * ox + oz * oz);
        }

        /// <summary>A game-frame point in the area: u, v in 0..1 across its width and height.</summary>
        public void PointIn(float u, float v, out float gx, out float gz)
        {
            float lx = (u - 0.5f) * Width, lz = (v - 0.5f) * Height;
            float[] w = EtsMapNode.Rotate(Rotation, lx, 0f, lz);
            gx = X + w[0];
            gz = Z + w[2];
        }
    }

    /// <summary>
    /// The game's own ambient sounds placed on the map. Measured on the 1.61 demo (DevTools/work/sound/map, 2026-10-14):
    /// every sector has a ".snd" file beside .base/.aux/.data, the same layout as the others (header, items, node table)
    /// and EtsMapSector reads it as it is; its items are type 21 with a 32-byte body - u64 sound_item_data token +0, u64
    /// reverb token +8, float width +16, float height +20, u64 node +24. Kdop flags: 1 = an ambient area (4,082 of the
    /// 7,215 in the demo: field_grown 1,557, foresttdec 535, foresttcon 266, field_bare 264 ...), 0 = a point sound
    /// (2,332: church_mdl1 239, dogs_2 168, city_indus 161, river_mid_1 145, crickets_3 122, brds_seagl1 118 ...),
    /// 2 = a reverb area (801, no token). An area's rectangle is width along its node's x and height along its z,
    /// centred on the node (the item's own bounding box says so on every one checked). The token names
    /// sound_item_data "ambient.&lt;token&gt;" (areas, sound_item_ambient.sii) or "sound.&lt;token&gt;" (point sounds,
    /// sound_item_data.sii). Pure C#: read on a worker thread.
    /// </summary>
    public static class EtsMapSounds
    {
        public const int ItemType = 21;

        /// <summary>Every sound_item_data unit of /def/world/sound_item_data*.sii and sound_item_ambient*.sii, by unit name.</summary>
        public static Dictionary<string, EtsMapSoundDef> ReadDefs(EtsFileSystem files)
        {
            var defs = new Dictionary<string, EtsMapSoundDef>(StringComparer.Ordinal);
            if (files == null || !files.TryList("/def/world", out List<string> list, out _)) return defs;
            list.Sort(StringComparer.Ordinal);
            foreach (string file in list)
            {
                if (!file.EndsWith(".sii", StringComparison.Ordinal)) continue;
                if (!file.StartsWith("sound_item_data", StringComparison.Ordinal) && !file.StartsWith("sound_item_ambient", StringComparison.Ordinal)) continue;
                string path = "/def/world/" + file;
                string text = files.ReadText(path);
                if (text == null) continue;
                EtsSiiFile sii = EtsSii.Parse(text, path, files.ReadText);
                if (sii == null) continue;
                foreach (EtsSiiUnit u in sii.Units)
                {
                    if (u.Type != "sound_item_data") continue;
                    string sound = EtsSiiValue.Unquote(u.GetString("sound_path", "")).Trim();
                    if (sound.Length == 0) continue;
                    defs[u.Name] = new EtsMapSoundDef
                    {
                        Unit = u.Name,
                        Path = sound,
                        Sfx = EtsSiiValue.Unquote(u.GetString("sound_sfx", "")).Trim(),
                        SfxCount = (int)u.GetFloat("sfx_count", 0f),
                        MaxDist = u.GetFloat("max_dist", 50f)
                    };
                }
            }
            return defs;
        }

        /// <summary>A sector's sound items (none when it has no .snd), each with its definition when the token names one.</summary>
        public static List<EtsMapSoundItem> ReadSector(EtsFileSystem files, int sectorX, int sectorZ, Dictionary<string, EtsMapSoundDef> defs)
        {
            var items = new List<EtsMapSoundItem>();
            byte[] b = files != null ? files.Read(EtsMapSector.FileName(sectorX, sectorZ, "snd")) : null;
            if (b == null) return items;
            EtsMapSector sector = EtsMapSector.Parse(sectorX, sectorZ, b, null, null);
            foreach (EtsMapItem it in sector.Items)
            {
                if (it.Type != ItemType || it.BodyLength < 32 || it.BodyOffset + 32 > b.Length) continue;
                int o = it.BodyOffset;
                ulong token = BitConverter.ToUInt64(b, o);
                ulong nodeUid = BitConverter.ToUInt64(b, o + 24);
                if (!sector.NodesByUid.TryGetValue(nodeUid, out EtsMapNode node)) continue;
                var item = new EtsMapSoundItem
                {
                    Uid = it.Uid,
                    Area = (it.Flags & 1) != 0,
                    Reverb = (it.Flags & 2) != 0,
                    Token = token != 0 ? EtsToken.Decode(token) : "",
                    Width = BitConverter.ToSingle(b, o + 16),
                    Height = BitConverter.ToSingle(b, o + 20),
                    X = node.Position[0],
                    Y = node.Position[1],
                    Z = node.Position[2],
                    Rotation = node.Rotation
                };
                if (item.Reverb || item.Token.Length == 0) continue;
                if (defs != null)
                {
                    string first = item.Area ? "ambient." : "sound.", second = item.Area ? "sound." : "ambient.";
                    if (!defs.TryGetValue(first + item.Token, out item.Def)) defs.TryGetValue(second + item.Token, out item.Def);
                }
                if (item.Def == null) continue;
                items.Add(item);
            }
            return items;
        }
    }
}
