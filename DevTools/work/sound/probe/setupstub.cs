// Probe-only stand-in for the world's generated EtsVorbisSetups: reads DevTools/work/sound/vorbis_headers.bin.
using System; using System.IO; using System.Collections.Generic;
namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    public static class EtsVorbisSetups
    {
        static Dictionary<uint, byte[]> _t;
        public static string TablePath = "D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/sound/vorbis_headers.bin";
        internal static Dictionary<uint, byte[]> AllX { get { Load(); return _t; } }
        static void Load()
        {
            if (_t != null) return;
            _t = new Dictionary<uint, byte[]>();
            using (var r = new BinaryReader(File.OpenRead(TablePath)))
            { int n = r.ReadInt32(); for (int i = 0; i < n; i++) { uint k = r.ReadUInt32(); int l = r.ReadInt32(); _t[k] = r.ReadBytes(l); } }
        }
        public static byte[] Find(uint crc) { Load(); return _t.TryGetValue(crc, out var b) ? b : null; }
    }
}
