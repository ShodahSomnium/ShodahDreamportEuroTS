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
            public int Channels;        // played channels: the coded ones, surround mixed down to stereo
            public int Coded;           // channels in the Vorbis stream (1, 2, 6 for the 5.1 ambience beds, 8)
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
                int chCode = (int)((raw >> 5) & 3); // 0 mono, 1 stereo, 2 six (5.1), 3 eight
                int channels = chCode == 0 ? 1 : chCode == 1 ? 2 : chCode == 2 ? 6 : 8;
                int dataOff = (int)((raw >> 7) & 0x7FFFFFF) * 32;
                int frames = (int)((raw >> 34) & 0x3FFFFFFF);
                var s = new Sample { Index = i, Name = i.ToString("0000"), Coded = channels, Frames = frames, DataOffset = dataOff, Rate = FreqFromCode(freqCode) };
                while (next)
                {
                    uint c = U32(d, p); p += 4;
                    next = (c & 1) != 0;
                    int size = (int)((c >> 1) & 0xFFFFFF);
                    int type = (int)((c >> 25) & 0x7F);
                    if (type == 1) s.Coded = d[p];
                    else if (type == 2) s.Rate = (int)U32(d, p);
                    else if (type == 3 && size >= 8) { s.LoopStart = (int)U32(d, p); s.LoopEnd = (int)U32(d, p + 4); }
                    else if (type == 11 && size >= 4) s.SetupCrc = U32(d, p);
                    p += size;
                }
                s.Coded = Math.Max(1, s.Coded);
                s.Channels = Math.Min(2, s.Coded);
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

            // Some long samples' packets start 16 bytes BEFORE the offset their header gives (measured 2026-10-14:
            // default.bank / forest.bank default_country and forest_warm_con_day_1 - the previous sample's packets end,
            // zero padding runs to the 16-byte boundary, and the first packet sits there, 16 bytes early). Read at the
            // header's offset the first "packet" is mid-stream (a header-type first bit) and NVorbis decodes nothing.
            // So every sample's start is checked as a chain of audio packets and moved by 16 when only the neighbour is.
            if (Mode == 15)
            {
                for (int i = 0; i < count; i++)
                {
                    var s = Samples[i];
                    int end = s.DataOffset + s.DataLength;
                    if (s.DataLength <= 0 || PacketChain(d, s.DataOffset, end)) continue;
                    foreach (int delta in ShiftTries)
                    {
                        int at = s.DataOffset + delta;
                        if (at < dataBase || at >= end || !PacketChain(d, at, end)) continue;
                        s.DataOffset = at;
                        s.DataLength = end - at;
                        Shifted++;
                        break;
                    }
                }
            }
        }

        private static readonly int[] ShiftTries = { -16, 16, -32, 32 };

        /// <summary>Samples whose packets were found 16 or 32 bytes from their header's offset (diagnostic).</summary>
        public int Shifted;

        /// <summary>True when the bytes at <paramref name="p"/> read as Vorbis audio packets (u16 size, first bit 0) for up to six packets.</summary>
        static bool PacketChain(byte[] d, int p, int end)
        {
            for (int k = 0; k < 6; k++)
            {
                if (p + 2 > end) return k > 0;
                int size = d[p] | (d[p + 1] << 8);
                if (size == 0) return k > 0;
                if (size > 16384 || p + 2 + size > end || (d[p + 2] & 1) != 0) return false;
                p += 2 + size;
            }
            return true;
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
                var dec = new MixDecoder(new FsbPacketProvider(Bytes, s.DataOffset, s.DataLength, IdHeader(s.Coded, s.Rate), CommentHeader, setup), s.Coded, s.Channels);
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

        /// <summary>
        /// True when decoded audio is clipped white noise: in at least half of its first eight half-seconds the signal
        /// crosses zero on over 30 % of frames and hits full scale. The game's long ambience beds (default_country,
        /// city_basic_background_loop_1, the harbour, wind and coast beds) decode like that here - a loud static loop -
        /// so they are left out instead of played. Real beds stay well under (papermill 0.39 at peak 0.26, traffic 0.03).
        /// </summary>
        public static bool LooksLikeNoise(float[] pcm, int length, int channels, int rate)
        {
            channels = Math.Max(1, channels);
            int win = Math.Max(1024, rate / 2) * channels;
            int windows = 0, noisy = 0;
            for (int p = 0; p + win <= length && windows < 8; p += win, windows++)
            {
                int zc = 0; float peak = 0f;
                for (int i = p + channels; i < p + win; i += channels)
                {
                    if ((pcm[i] >= 0f) != (pcm[i - channels] >= 0f)) zc++;
                    float v = Math.Abs(pcm[i]);
                    if (v > peak) peak = v;
                }
                if (zc > 0.3f * (win / channels) && peak > 0.95f) noisy++;
            }
            return windows > 0 && noisy * 2 >= windows;
        }

        /// <summary>True when the sample's first 4 s decode as noise (LooksLikeNoise), checked before streaming it.</summary>
        public bool DecodesAsNoise(Sample s)
        {
            byte[] setup = SetupLookup != null ? SetupLookup(s.SetupCrc) : null;
            if (setup == null || s.DataLength <= 0) return false;
            try
            {
                var dec = new MixDecoder(new FsbPacketProvider(Bytes, s.DataOffset, s.DataLength, IdHeader(s.Coded, s.Rate), CommentHeader, setup), s.Coded, s.Channels);
                var buf = new float[Math.Max(1, s.Channels) * Math.Max(8192, s.Rate * 4)];
                int got = 0;
                while (got < buf.Length) { int n = dec.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                dec.Dispose();
                return LooksLikeNoise(buf, got, s.Channels, s.Rate);
            }
            catch (Exception) { return true; }
        }

        /// <summary>A copy of one sample's packets, for streaming it (LoopReader) without keeping the whole bank.</summary>
        public byte[] CopyPackets(Sample s)
        {
            if (s == null || s.DataLength <= 0) return null;
            var b = new byte[s.DataLength];
            Array.Copy(Bytes, s.DataOffset, b, 0, s.DataLength);
            return b;
        }

        /// <summary>
        /// Decodes a sample's packets (CopyPackets) a buffer at a time, round and round: at the end it starts again from
        /// the first packet, the last 256 frames before the end faded out and the first 256 after it faded in (Vorbis
        /// drops the first packet's output, so a hard join would click). One thread at a time (the stream worker).
        /// </summary>
        public sealed class LoopReader
        {
            readonly byte[] _data, _setup;
            readonly int _channels, _coded, _rate;
            MixDecoder _dec;
            bool _fadeIn;
            public int Passes { get; private set; }
            public bool Ok => _setup != null && _data != null && _data.Length > 2;

            public LoopReader(byte[] packets, int channels, int rate, uint setupCrc, int coded = 0)
            {
                _data = packets;
                _channels = Math.Max(1, channels);
                _coded = coded > 0 ? coded : _channels;
                _rate = rate;
                _setup = SetupLookup != null ? SetupLookup(setupCrc) : null;
            }

            /// <summary>Fills the whole buffer (interleaved floats); silence if the packets cannot be decoded.</summary>
            public void Read(float[] buf)
            {
                int got = 0;
                if (Ok)
                {
                    for (int guard = 0; got < buf.Length && guard < 64; guard++)
                    {
                        if (_dec == null)
                        {
                            try
                            {
                                _dec = new MixDecoder(new FsbPacketProvider(_data, 0, _data.Length, IdHeader(_coded, _rate), CommentHeader, _setup), _coded, _channels);
                            }
                            catch (Exception) { _dec = null; break; }
                            _fadeIn = Passes > 0;
                            Passes++;
                        }
                        int n;
                        try { n = _dec.Read(buf, got, buf.Length - got); }
                        catch (Exception) { n = 0; }
                        if (n <= 0)
                        {
                            Fade(buf, Math.Max(0, got - 256 * _channels), got, false);
                            try { _dec.Dispose(); } catch (Exception) { }
                            _dec = null;
                            if (Passes > 1000000) break;
                            continue;
                        }
                        if (_fadeIn) { Fade(buf, got, Math.Min(got + 256 * _channels, got + n), true); _fadeIn = false; }
                        got += n;
                    }
                }
                if (got < buf.Length) Array.Clear(buf, got, buf.Length - got);
            }

            void Fade(float[] buf, int from, int to, bool up)
            {
                int frames = (to - from) / _channels;
                for (int f = 0; f < frames; f++)
                {
                    float g = (f + 0.5f) / frames;
                    if (!up) g = 1f - g;
                    for (int c = 0; c < _channels; c++) buf[from + f * _channels + c] *= g;
                }
            }
        }

        /// <summary>
        /// A Vorbis decoder that hands out the played channels: surround streams (the game's 5.1 ambience beds,
        /// FL C FR RL RR LFE in Vorbis order) are mixed down to stereo, the LFE left out, kept at the same loudness for
        /// uncorrelated channels (each side's weights sum to 1 in power) and clipped to -1..1.
        /// </summary>
        sealed class MixDecoder : IDisposable
        {
            readonly StreamDecoder _dec;
            readonly int _in, _out;
            readonly float[] _wl, _wr;
            float[] _scratch;

            public MixDecoder(IPacketProvider provider, int coded, int played)
            {
                _dec = new StreamDecoder(provider);
                _dec.ClipSamples = true;
                _in = Math.Max(1, coded);
                _out = Math.Max(1, played);
                if (_in == _out) return;
                _wl = new float[_in]; _wr = new float[_in];
                // Vorbis channel order: 3 L C R; 4 FL FR RL RR; 5 FL C FR RL RR; 6 + LFE; 7 FL C FR SL SR RC LFE; 8 FL C FR SL SR RL RR LFE.
                string[] order = _in == 3 ? new[] { "L", "C", "R" } : _in == 4 ? new[] { "L", "R", "L", "R" } : _in == 5 ? new[] { "L", "C", "R", "L", "R" }
                    : _in == 6 ? new[] { "L", "C", "R", "L", "R", "-" } : _in == 7 ? new[] { "L", "C", "R", "L", "R", "C", "-" } : new[] { "L", "C", "R", "L", "R", "L", "R", "-" };
                float pl = 0f, pr = 0f;
                for (int c = 0; c < _in; c++)
                {
                    string k = c < order.Length ? order[c] : "-";
                    _wl[c] = k == "L" ? 1f : k == "C" ? 0.7071f : 0f;
                    _wr[c] = k == "R" ? 1f : k == "C" ? 0.7071f : 0f;
                    pl += _wl[c] * _wl[c]; pr += _wr[c] * _wr[c];
                }
                float gl = pl > 0f ? 1f / (float)Math.Sqrt(pl) : 0f, gr = pr > 0f ? 1f / (float)Math.Sqrt(pr) : 0f;
                for (int c = 0; c < _in; c++) { _wl[c] *= gl; _wr[c] *= gr; }
            }

            /// <summary>Reads up to <paramref name="count"/> played floats (whole frames) into buf at at; 0 at the end.</summary>
            public int Read(float[] buf, int at, int count)
            {
                if (_wl == null) return _dec.Read(buf, at, count);
                int frames = count / _out;
                if (frames <= 0) return 0;
                if (_scratch == null || _scratch.Length < frames * _in) _scratch = new float[frames * _in];
                int n = _dec.Read(_scratch, 0, frames * _in) / _in;
                for (int f = 0; f < n; f++)
                {
                    int s = f * _in;
                    float l = 0f, r = 0f;
                    for (int c = 0; c < _in; c++) { float v = _scratch[s + c]; l += v * _wl[c]; r += v * _wr[c]; }
                    l = l > 1f ? 1f : l < -1f ? -1f : l;
                    r = r > 1f ? 1f : r < -1f ? -1f : r;
                    if (_out == 1) buf[at + f] = 0.5f * (l + r);
                    else { buf[at + f * _out] = l; buf[at + f * _out + 1] = r; }
                }
                return n * _out;
            }

            public void Dispose() => _dec.Dispose();
        }

        static readonly byte[] CommentHeader ={ 3, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0, 0, 0, 0, 0, 0, 0, 0, 1 };

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
