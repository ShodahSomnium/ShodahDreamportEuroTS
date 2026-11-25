using System;
using System.Collections.Generic;
using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis.Contracts;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Reads one of the game's FMOD Studio sound banks (/sound/**/*.bank, read from the player's own install) and
    /// decodes its samples. A bank is a RIFF file ("RIFF" size "FEV ") whose "SND " chunk holds an FSB5 sample bank.
    /// ETS2 1.61 stores every sample as FSB5 codec 15: Vorbis audio packets with the Vorbis identification, comment
    /// and setup headers left out. The identification header is rebuilt from the sample's channels and rate (FMOD
    /// always encodes with 256/2048 blocks), the comment header is empty, and the setup header is looked up by the
    /// CRC-32 the sample's VORBISDATA chunk stores (EtsVorbisSetups). The packets then decode with a normal Vorbis
    /// decoder (vendored NVorbis). Pure C#, no Unity calls: decode on a worker thread.
    /// </summary>
    public sealed class EtsFmodBank
    {
        public sealed class Sample
        {
            public int Index;
            public string Name;
            public int Rate;
            public int Channels;
            public int Frames;          // samples per channel, from the FSB5 sample header
            public int LoopStart = -1;  // in frames, from the LOOP chunk (-1 = none)
            public int LoopEnd = -1;
            public uint SetupCrc;       // CRC-32 of the Vorbis setup header (VORBISDATA chunk)
            public int DataOffset;      // into Fsb
            public int DataLength;
            public override string ToString() => Name + " " + Channels + "ch " + Rate + "Hz " + Frames + " frames" + (LoopStart >= 0 ? " loop " + LoopStart + "-" + LoopEnd : "") + " crc " + SetupCrc;
        }

        /// <summary>Codec number from the FSB5 header (15 = Vorbis).</summary>
        public int Mode;
        public int Version;
        public readonly List<Sample> Samples = new List<Sample>();
        /// <summary>The bank file bytes; FSB5 data offsets index into this.</summary>
        public byte[] Bytes;
        public int FsbOffset = -1;
        /// <summary>Top-level RIFF chunk ids with sizes, for the log ("FMT 8, LIST 1234, SND 999").</summary>
        public string ChunkSummary = "";

        /// <summary>Looks up a Vorbis setup header by CRC-32. Defaults to the table embedded in EtsVorbisSetups.</summary>
        public static Func<uint, byte[]> SetupLookup = EtsVorbisSetups.Find;

        public Sample Find(string name)
        {
            for (int i = 0; i < Samples.Count; i++)
                if (string.Equals(Samples[i].Name, name, StringComparison.OrdinalIgnoreCase)) return Samples[i];
            return null;
        }

        public static EtsFmodBank Parse(byte[] bank, out string error)
        {
            error = null;
            if (bank == null || bank.Length < 64) { error = "bank missing or empty"; return null; }
            var b = new EtsFmodBank { Bytes = bank };
            int fsb = -1;
            if (Tag(bank, 0) == "RIFF")
            {
                // Walk the RIFF tree for the SND chunk; record what is there.
                var sb = new StringBuilder();
                int end = Math.Min(bank.Length, 8 + (int)U32(bank, 4));
                int p = 12;
                while (p + 8 <= end)
                {
                    string id = Tag(bank, p); int size = (int)U32(bank, p + 4);
                    if (size < 0 || p + 8 + size > bank.Length) break;
                    if (sb.Length < 200) sb.Append(id.Trim()).Append(' ').Append(size).Append(", ");
                    if (id == "SND ") fsb = IndexOf(bank, p + 8, Math.Min(p + 8 + size, bank.Length), "FSB5");
                    p += 8 + size; p = (p + 1) & ~1;
                }
                b.ChunkSummary = sb.ToString().TrimEnd(',', ' ');
            }
            if (fsb < 0) fsb = IndexOf(bank, 0, bank.Length, "FSB5");
            if (fsb < 0) { error = "no FSB5 sample bank inside (encrypted bank?)"; return null; }
            b.FsbOffset = fsb;
            try { b.ReadFsb5(fsb); }
            catch (Exception e) { error = "FSB5 parse failed: " + e.Message; return null; }
            return b;
        }

        void ReadFsb5(int o)
        {
            byte[] d = Bytes;
            Version = (int)U32(d, o + 4);
            int count = (int)U32(d, o + 8);
            int headersSize = (int)U32(d, o + 12);
            int namesSize = (int)U32(d, o + 16);
            int dataSize = (int)U32(d, o + 20);
            Mode = (int)U32(d, o + 24);
            int headerLen = Version == 0 ? 64 : 60;
            int p = o + headerLen;
            int headersEnd = p + headersSize;
            if (count < 0 || count > 100000 || headersEnd > d.Length) throw new Exception("bad header (" + count + " samples)");
            for (int i = 0; i < count; i++)
            {
                ulong raw = U64(d, p); p += 8;
                bool next = (raw & 1) != 0;
                int freqCode = (int)((raw >> 1) & 0xF);
                int channels = (int)((raw >> 5) & 1) + 1;
                int dataOff = (int)((raw >> 6) & 0xFFFFFFF) * 16;
                int frames = (int)((raw >> 34) & 0x3FFFFFFF);
                var s = new Sample { Index = i, Name = i.ToString("0000"), Channels = channels, Frames = frames, DataOffset = dataOff, Rate = FreqFromCode(freqCode) };
                while (next)
                {
                    uint c = U32(d, p); p += 4;
                    next = (c & 1) != 0;
                    int size = (int)((c >> 1) & 0xFFFFFF);
                    int type = (int)((c >> 25) & 0x7F);
                    if (type == 1) s.Channels = d[p];
                    else if (type == 2) s.Rate = (int)U32(d, p);
                    else if (type == 3 && size >= 8) { s.LoopStart = (int)U32(d, p); s.LoopEnd = (int)U32(d, p + 4); }
                    else if (type == 11 && size >= 4) s.SetupCrc = U32(d, p);
                    p += size;
                }
                Samples.Add(s);
            }
            p = headersEnd;
            if (namesSize > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    int no = (int)U32(d, p + 4 * i);
                    int q = p + no, e = q;
                    while (e < p + namesSize && e < d.Length && d[e] != 0) e++;
                    Samples[i].Name = Encoding.UTF8.GetString(d, q, e - q);
                }
            }
            int dataBase = headersEnd + namesSize;
            for (int i = 0; i < count; i++)
            {
                var s = Samples[i];
                int start = s.DataOffset;
                int stop = i + 1 < count ? Samples[i + 1].DataOffset : dataSize;
                s.DataOffset = dataBase + start;
                s.DataLength = Math.Max(0, Math.Min(stop, dataSize) - start);
                if (s.DataOffset + s.DataLength > d.Length) s.DataLength = Math.Max(0, d.Length - s.DataOffset);
            }
        }

        static int FreqFromCode(int c)
        {
            switch (c)
            {
                case 1: return 8000; case 2: return 11000; case 3: return 11025; case 4: return 16000; case 5: return 22050;
                case 6: return 24000; case 7: return 32000; case 8: return 44100; case 9: return 48000; default: return 44100;
            }
        }

        /// <summary>
        /// Decodes one sample to interleaved floats (-1..1), Frames * Channels long. Returns null with a reason when
        /// the codec is not Vorbis, the setup header is unknown or the packets do not decode.
        /// </summary>
        public float[] Decode(Sample s, out string error)
        {
            error = null;
            if (s == null) { error = "no sample"; return null; }
            if (Mode != 15) { error = "codec " + Mode + " is not Vorbis"; return null; }
            byte[] setup = SetupLookup != null ? SetupLookup(s.SetupCrc) : null;
            if (setup == null) { error = "no Vorbis setup header for crc " + s.SetupCrc; return null; }
            try
            {
                var provider = new FsbPacketProvider(Bytes, s.DataOffset, s.DataLength, IdHeader(s.Channels, s.Rate), CommentHeader, setup);
                var dec = new StreamDecoder(provider);
                dec.ClipSamples = true;
                int want = s.Frames > 0 ? s.Frames * s.Channels : 0;
                var outBuf = new float[want > 0 ? want : 65536];
                int got = 0;
                var chunk = new float[4096 * s.Channels];
                while (true)
                {
                    int n = dec.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    if (got + n > outBuf.Length)
                    {
                        if (want > 0) { n = outBuf.Length - got; if (n <= 0) break; }
                        else Array.Resize(ref outBuf, Math.Max(outBuf.Length * 2, got + n));
                    }
                    Array.Copy(chunk, 0, outBuf, got, n);
                    got += n;
                    if (want > 0 && got >= want) break;
                }
                if (got < outBuf.Length && want == 0) Array.Resize(ref outBuf, got);
                if (got == 0) { error = "decoded nothing"; return null; }
                return outBuf;
            }
            catch (Exception e) { error = "Vorbis decode failed: " + e.Message; return null; }
        }

        static readonly byte[] CommentHeader = { 3, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0, 0, 0, 0, 0, 0, 0, 0, 1 };

        static byte[] IdHeader(int channels, int rate)
        {
            var h = new byte[30];
            h[0] = 1; h[1] = (byte)'v'; h[2] = (byte)'o'; h[3] = (byte)'r'; h[4] = (byte)'b'; h[5] = (byte)'i'; h[6] = (byte)'s';
            h[11] = (byte)channels;
            h[12] = (byte)rate; h[13] = (byte)(rate >> 8); h[14] = (byte)(rate >> 16); h[15] = (byte)(rate >> 24);
            h[28] = 8 | (11 << 4); // blocksizes 256 / 2048
            h[29] = 1;
            return h;
        }

        /// <summary>Feeds the three rebuilt headers, then the FSB5 packets (uint16 length + bytes each).</summary>
        sealed class FsbPacketProvider : IPacketProvider
        {
            readonly byte[] _data; readonly int _end; int _pos;
            readonly byte[][] _headers; int _headerIndex;
            ArrayPacket _peeked;

            public FsbPacketProvider(byte[] data, int start, int length, byte[] id, byte[] comment, byte[] setup)
            { _data = data; _pos = start; _end = start + length; _headers = new[] { id, comment, setup }; }

            public bool CanSeek => false;
            public int StreamSerial => 0;
            public long GetGranuleCount() => 0;
            public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount) => throw new NotSupportedException();

            public IPacket PeekNextPacket()
            {
                if (_peeked == null) _peeked = Next();
                return _peeked;
            }

            public IPacket GetNextPacket()
            {
                var p = _peeked ?? Next();
                _peeked = null;
                return p;
            }

            ArrayPacket Next()
            {
                if (_headerIndex < 3) { var h = _headers[_headerIndex++]; return new ArrayPacket(h, 0, h.Length, false); }
                if (_pos + 2 > _end) return null;
                int size = _data[_pos] | (_data[_pos + 1] << 8);
                if (size == 0 || _pos + 2 + size > _end) return null;
                int at = _pos + 2;
                _pos = at + size;
                bool last = _pos + 2 > _end || (_data[_pos] | (_data[_pos + 1] << 8)) == 0;
                return new ArrayPacket(_data, at, size, last);
            }
        }

        sealed class ArrayPacket : DataPacket
        {
            readonly byte[] _d; readonly int _start, _len; int _i;
            public ArrayPacket(byte[] d, int start, int len, bool eos) { _d = d; _start = start; _len = len; IsEndOfStream = eos; }
            protected override int TotalBits => _len * 8;
            protected override int ReadNextByte() => _i < _len ? _d[_start + _i++] : -1;
            public override void Reset() { _i = 0; base.Reset(); }
        }

        // ---- little helpers ----
        static string Tag(byte[] d, int o) => o + 4 <= d.Length ? Encoding.ASCII.GetString(d, o, 4) : "";
        static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        static ulong U64(byte[] d, int o) => U32(d, o) | ((ulong)U32(d, o + 4) << 32);
        static int IndexOf(byte[] d, int from, int to, string tag)
        {
            byte a = (byte)tag[0], b = (byte)tag[1], c = (byte)tag[2], e = (byte)tag[3];
            for (int i = from; i + 4 <= to; i++)
                if (d[i] == a && d[i + 1] == b && d[i + 2] == c && d[i + 3] == e) return i;
            return -1;
        }
    }
}
