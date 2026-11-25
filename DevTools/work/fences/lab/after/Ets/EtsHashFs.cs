using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One of the engine's .scs archives - base.scs, def.scs and the rest - opened for reading.
    ///
    /// The format is HashFS, and the name says how it works: there is no directory of names,
    /// only a table of CityHash64 values. A file is found by hashing the path you already know
    /// and looking the hash up; a folder is a file whose content is a list of the names inside
    /// it, one per line, subfolders marked with a leading asterisk. Everything a Somnium world
    /// needs from the game - definitions, models, textures, the map - comes through here.
    ///
    /// Layout, confirmed byte by byte against the demo's archives rather than taken on trust:
    ///
    ///     0   "SCS#"          magic
    ///     4   u16 version     1
    ///     6   u16 salt        0 on every archive seen so far
    ///     8   "CITY"          hash method
    ///     12  u32 entries
    ///     16  u32 table offset, 0x1000 on every archive seen so far
    ///
    /// and each entry, 32 bytes: u64 hash, u64 offset, u32 flags, u32 crc, u32 size,
    /// u32 compressed size. Flags: 1 directory, 2 zlib-compressed, 4 verify.
    ///
    /// base.scs is thirteen gigabytes. Nothing here reads it whole: the table is read once
    /// (six and a half megabytes for its two hundred thousand entries), and each file is a seek
    /// and a read of its own bytes. The stream is kept open for the life of the object, because
    /// a game that opens and closes a thirteen-gigabyte file per texture is a game that stutters.
    /// </summary>
    public sealed class EtsHashFs : IDisposable
    {
        public const uint Magic = 0x23534353; // "SCS#"

        public const uint FlagDirectory = 1;
        public const uint FlagCompressed = 2;
        public const uint FlagVerify = 4;
        public const uint FlagEncrypted = 8;

        /// <summary>One row of the entry table.</summary>
        public struct Entry
        {
            public ulong Hash;
            public long Offset;
            public uint Flags;
            public uint Crc;
            public uint Size;
            public uint CompressedSize;

            /// <summary>
            /// HashFS v2 only: the bytes are a packed texture surface rather than a file as stored -
            /// every .tobj of game 1.61 is one, and no .dds is stored at all. Read these with
            /// ReadPackedImage; the geometry below describes the surface.
            /// </summary>
            public bool IsPackedImage;
            public int ImageWidth;
            public int ImageHeight;
            public int MipCount;
            public int DxgiFormat;
            public int FaceCount;
            public int PitchAlign;
            public int ImageAlign;
            public bool IsCubeMap;
            public uint SampleFlags;

            public bool IsDirectory => (Flags & FlagDirectory) != 0;
            public bool IsCompressed => (Flags & FlagCompressed) != 0;
            public bool IsEncrypted => (Flags & FlagEncrypted) != 0;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<ulong, Entry> _entries = new Dictionary<ulong, Entry>();
        private FileStream _stream;

        /// <summary>The archive file this reads from.</summary>
        public string FilePath { get; private set; }

        public ushort Version { get; private set; }

        public ushort Salt { get; private set; }

        public int EntryCount => _entries.Count;

        /// <summary>Every entry, in table order. For browsing and diagnosis, not for lookup.</summary>
        public IEnumerable<Entry> Entries => _entries.Values;

        private EtsHashFs()
        {
        }

        /// <summary>
        /// Opens an archive and reads its table.
        /// </summary>
        /// <exception cref="InvalidDataException">Not a HashFS v1 archive.</exception>
        public static EtsHashFs Open(string file)
        {
            var archive = new EtsHashFs { FilePath = file };
            archive._stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

            try
            {
                var header = new byte[64];
                archive.ReadExactly(0, header, 0, header.Length);

                if (BitConverter.ToUInt32(header, 0) != Magic)
                {
                    throw new InvalidDataException(file + " is not a HashFS archive (bad magic).");
                }

                archive.Version = BitConverter.ToUInt16(header, 4);
                archive.Salt = BitConverter.ToUInt16(header, 6);
                string method = Encoding.ASCII.GetString(header, 8, 4);
                uint count = BitConverter.ToUInt32(header, 12);
                uint tableOffset = BitConverter.ToUInt32(header, 16);

                if (method != "CITY")
                {
                    throw new InvalidDataException(file + " uses hash method \"" + method + "\", not CITY.");
                }

                if (archive.Version == 2)
                {
                    archive.ReadTablesV2(header);
                    return archive;
                }

                if (archive.Version != 1)
                {
                    throw new InvalidDataException(file + " is HashFS version " + archive.Version +
                                                   "; only versions 1 and 2 are understood.");
                }

                var table = new byte[count * 32L];
                archive.ReadExactly(tableOffset, table, 0, table.Length);

                for (int i = 0; i < count; i++)
                {
                    int o = i * 32;
                    var entry = new Entry
                    {
                        Hash = BitConverter.ToUInt64(table, o),
                        Offset = (long)BitConverter.ToUInt64(table, o + 8),
                        Flags = BitConverter.ToUInt32(table, o + 16),
                        Crc = BitConverter.ToUInt32(table, o + 20),
                        Size = BitConverter.ToUInt32(table, o + 24),
                        CompressedSize = BitConverter.ToUInt32(table, o + 28)
                    };

                    archive._entries[entry.Hash] = entry;
                }

                return archive;
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }


        /// <summary>
        /// Version 2, which the game switched to at 1.50 and which the demo now ships. The whole
        /// catalogue moved out of one flat table: there is a compressed ENTRY table of 16-byte
        /// records that say only where an entry's metadata lives, and a compressed METADATA table
        /// holding variable-length blocks that carry the offset, the sizes and the kind. Both tables
        /// are zlib.
        ///
        /// Header, after the magic and version this class already read:
        ///   +12 u32 entries          +16 u32 entry table length (as stored, compressed)
        ///   +20 u32 metadata entries +24 u32 metadata table length (as stored)
        ///   +28 u64 entry table start   +36 u64 metadata table start
        ///   +44 u32 security descriptor offset   +48 u8 platform
        /// Checked against the demo's own archives: version.scs states 2 entries with its metadata
        /// table at 12,720 and 37 bytes long, and the file is 12,757 bytes - it ends exactly there;
        /// base.scs states its metadata at 10,378,353,472 and 1,806,742 long against a file of
        /// 10,380,160,214, which also ends exactly there.
        /// </summary>
        private void ReadTablesV2(byte[] header)
        {
            uint entries = BitConverter.ToUInt32(header, 12);
            uint entryTableLength = BitConverter.ToUInt32(header, 16);
            uint metadataTableLength = BitConverter.ToUInt32(header, 24);
            long entryTableStart = (long)BitConverter.ToUInt64(header, 28);
            long metadataTableStart = (long)BitConverter.ToUInt64(header, 36);

            var packedEntries = new byte[entryTableLength];
            ReadExactly(entryTableStart, packedEntries, 0, packedEntries.Length);
            byte[] entryTable = Inflate(packedEntries, (int)entries * EntryRecordV2);

            var packedMetadata = new byte[metadataTableLength];
            ReadExactly(metadataTableStart, packedMetadata, 0, packedMetadata.Length);
            byte[] metadata = Inflate(packedMetadata, 0);

            for (long i = 0; i < entries; i++)
            {
                long o = i * EntryRecordV2;
                ulong hash = BitConverter.ToUInt64(entryTable, (int)o);
                uint metadataIndex = BitConverter.ToUInt32(entryTable, (int)o + 8);
                int metadataCount = BitConverter.ToUInt16(entryTable, (int)o + 12);

                // The index counts four-byte blocks into the metadata table. Each of the entry's
                // chunks is described by three bytes of index and one byte of kind; the payload
                // follows the last descriptor.
                long block = (long)metadataIndex * MetadataBlockV2;

                if (metadataCount <= 0)
                {
                    continue;
                }

                int kind = metadata[block + 3];
                int payload = (int)(block + metadataCount * 4L);

                // An image entry carries twelve bytes of texture metadata in front of the main
                // metadata, so it needs twelve more bytes of table than a plain file does.
                if (payload + MainMetadataV2 + (kind == ChunkImage ? PackedImageV2 : 0) > metadata.Length)
                {
                    continue;
                }

                // Kind 128 is a plain file, 129 a folder, 1 a packed image.
                if (kind != ChunkPlain && kind != ChunkDirectory && kind != ChunkImage)
                {
                    OtherChunks++;
                    continue;
                }

                bool packedImage = kind == ChunkImage;
                int imageWidth = 0, imageHeight = 0, mipCount = 0, dxgiFormat = 0, faceCount = 0;
                int pitchAlign = 1, imageAlign = 1;
                bool isCubeMap = false;
                uint sampleFlags = 0;

                if (packedImage)
                {
                    // u16 width - 1, u16 height - 1, u32 image flags, u32 sample flags (TruckLib.HashFs
                    // PackedTobjDdsMetadata). Measured on the 1.61 demo: the block length this implies
                    // lands exactly on the next entry's block index 66,769 times out of 66,769, and
                    // the surface length it describes equals the payload's own declared length 66,674
                    // times out of 66,674.
                    PackedImages++;
                    imageWidth = BitConverter.ToUInt16(metadata, payload) + 1;
                    imageHeight = BitConverter.ToUInt16(metadata, payload + 2) + 1;
                    uint imageFlags = BitConverter.ToUInt32(metadata, payload + 4);
                    sampleFlags = BitConverter.ToUInt32(metadata, payload + 8);
                    mipCount = (int)(imageFlags & 0xF) + 1;
                    dxgiFormat = (int)((imageFlags >> 4) & 0xFF);
                    isCubeMap = ((imageFlags >> 12) & 3) != 0;
                    faceCount = (int)((imageFlags >> 14) & 0x3F) + 1;
                    pitchAlign = 1 << (int)((imageFlags >> 20) & 0xF);
                    imageAlign = 1 << (int)((imageFlags >> 24) & 0xF);
                    payload += PackedImageV2;
                }

                // Sizes are 28 bits with four bits of flags in the top nibble of each fourth byte;
                // the offset is in 16-byte blocks.
                uint compressedSize = (uint)(metadata[payload] | (metadata[payload + 1] << 8) | (metadata[payload + 2] << 16))
                                      | ((uint)(metadata[payload + 3] & 0x0F) << 24);
                int flags1 = metadata[payload + 3] >> 4;
                uint size = (uint)(metadata[payload + 4] | (metadata[payload + 5] << 8) | (metadata[payload + 6] << 16))
                            | ((uint)(metadata[payload + 7] & 0x0F) << 24);
                long offset = (long)BitConverter.ToUInt32(metadata, payload + 12) * OffsetBlockV2;

                bool compressed = (flags1 & 1) != 0;

                _entries[hash] = new Entry
                {
                    Hash = hash,
                    Offset = offset,
                    Size = size,
                    CompressedSize = compressed ? compressedSize : size,
                    Flags = (kind == ChunkDirectory ? FlagDirectory : 0u) | (compressed ? FlagCompressed : 0u),
                    IsPackedImage = packedImage,
                    ImageWidth = imageWidth,
                    ImageHeight = imageHeight,
                    MipCount = mipCount,
                    DxgiFormat = dxgiFormat,
                    FaceCount = faceCount,
                    PitchAlign = pitchAlign,
                    ImageAlign = imageAlign,
                    IsCubeMap = isCubeMap,
                    SampleFlags = sampleFlags
                };
            }
        }

        /// <summary>Entries whose bytes are a packed image rather than a file as stored. They are in
        /// the entry table and read through ReadPackedImage.</summary>
        public int PackedImages { get; private set; }

        /// <summary>Entries of a kind this reader does not know.</summary>
        public int OtherChunks { get; private set; }

        private const int EntryRecordV2 = 16;
        private const int MetadataBlockV2 = 4;
        private const int MainMetadataV2 = 16;
        private const long OffsetBlockV2 = 16;
        private const int ChunkImage = 1;
        private const int ChunkPlain = 128;
        private const int ChunkDirectory = 129;
        private const int PackedImageV2 = 12;

        /// <summary>
        /// A zlib stream, inflated. Pass the expected length when it is known so a short read is
        /// caught; pass zero to take whatever comes out, which is what the two tables need.
        /// </summary>
        private static byte[] Inflate(byte[] packed, int expected)
        {
            // zlib framing: two header bytes in front of a raw deflate stream, Adler-32 behind it.
            int skip = packed.Length >= 2 && (packed[0] & 0x0F) == 8 && ((packed[0] << 8) | packed[1]) % 31 == 0 ? 2 : 0;

            using (var source = new MemoryStream(packed, skip, packed.Length - skip))
            using (var inflate = new DeflateStream(source, CompressionMode.Decompress))
            {
                if (expected > 0)
                {
                    var exact = new byte[expected];
                    int total = 0;

                    while (total < expected)
                    {
                        int got = inflate.Read(exact, total, expected - total);

                        if (got <= 0)
                        {
                            break;
                        }

                        total += got;
                    }

                    if (total != expected)
                    {
                        throw new InvalidDataException("Short inflate: expected " + expected + " bytes, got " + total + ".");
                    }

                    return exact;
                }

                using (var grown = new MemoryStream())
                {
                    var buffer = new byte[1 << 16];
                    int got;

                    while ((got = inflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        grown.Write(buffer, 0, got);
                    }

                    return grown.ToArray();
                }
            }
        }
        /// <summary>
        /// The archive's form of a path: forward slashes, no leading or trailing slash. The root
        /// folder is the empty string.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string p = path.Replace('\\', '/');

            while (p.StartsWith("/", StringComparison.Ordinal))
            {
                p = p.Substring(1);
            }

            while (p.EndsWith("/", StringComparison.Ordinal))
            {
                p = p.Substring(0, p.Length - 1);
            }

            return p;
        }

        /// <summary>The hash the table would hold for a path, salt included.</summary>
        public ulong HashOf(string path)
        {
            string normalized = Normalize(path);

            // A non-zero salt is prepended to the path as text before hashing. No archive with
            // a salt has been seen; if one turns up, this is the line to check first.
            if (Salt != 0)
            {
                normalized = Salt.ToString() + normalized;
            }

            return EtsCityHash.Hash(normalized);
        }

        public bool TryGetEntry(string path, out Entry entry)
        {
            return _entries.TryGetValue(HashOf(path), out entry);
        }

        public bool TryGetEntry(ulong hash, out Entry entry)
        {
            return _entries.TryGetValue(hash, out entry);
        }

        public bool Exists(string path)
        {
            return _entries.ContainsKey(HashOf(path));
        }

        /// <summary>The bytes of a file, decompressed. Null when the archive has no such path.</summary>
        public byte[] Read(string path)
        {
            return TryGetEntry(path, out Entry entry) ? Read(entry) : null;
        }

        /// <summary>
        /// The surface bytes of a packed image with the GDeflate undone, still in the archive's
        /// aligned mip layout (EtsDds.FromPackedSurface takes it from there). Null for other entries.
        /// </summary>
        public byte[] ReadPackedImage(Entry entry)
        {
            if (!entry.IsPackedImage)
            {
                return null;
            }

            var packed = new byte[entry.CompressedSize];
            ReadExactly(entry.Offset, packed, 0, packed.Length);
            return entry.IsCompressed ? EtsGDeflate.Decompress(packed, (int)entry.Size) : packed;
        }

        /// <summary>The bytes of an entry, decompressed.</summary>
        public byte[] Read(Entry entry)
        {
            if (entry.IsPackedImage)
            {
                // GDeflate rather than zlib, and a raw surface rather than a stored file: callers
                // that want the pixels go through ReadPackedImage. Everyone else gets null, which is
                // what they got before image entries were admitted to the table.
                return null;
            }

            if (!entry.IsCompressed)
            {
                var plain = new byte[entry.Size];
                ReadExactly(entry.Offset, plain, 0, plain.Length);
                return plain;
            }

            var packed = new byte[entry.CompressedSize];
            ReadExactly(entry.Offset, packed, 0, packed.Length);

            // zlib framing: a two-byte header in front of a raw deflate stream and an Adler-32
            // behind it. DeflateStream wants the raw stream only, so step over the header; the
            // trailer is simply never read.
            int skip = packed.Length >= 2 && (packed[0] & 0x0F) == 8 && ((packed[0] << 8) | packed[1]) % 31 == 0 ? 2 : 0;

            var result = new byte[entry.Size];

            using (var source = new MemoryStream(packed, skip, packed.Length - skip))
            using (var inflate = new DeflateStream(source, CompressionMode.Decompress))
            {
                int total = 0;

                while (total < result.Length)
                {
                    int got = inflate.Read(result, total, result.Length - total);

                    if (got <= 0)
                    {
                        break;
                    }

                    total += got;
                }

                if (total != result.Length)
                {
                    throw new InvalidDataException("Short inflate: expected " + result.Length + " bytes, got " + total +
                                                   " (entry 0x" + entry.Hash.ToString("X16") + ").");
                }
            }

            return result;
        }

        /// <summary>
        /// The names inside a folder. Subfolders come back without their asterisk.
        /// </summary>
        /// <returns>False when the path is not a folder in this archive.</returns>
        public bool TryReadDirectory(string path, out List<string> files, out List<string> folders)
        {
            files = null;
            folders = null;

            if (!TryGetEntry(path, out Entry entry) || !entry.IsDirectory)
            {
                return false;
            }

            byte[] bytes = Read(entry);

            files = new List<string>();
            folders = new List<string>();

            // Version 2 writes a folder as a count, then one length byte per name, then the names
            // run together - and marks a subfolder with a leading slash instead of version 1's
            // asterisk.
            if (Version == 2)
            {
                if (bytes.Length < 4)
                {
                    return true;
                }

                int count = (int)BitConverter.ToUInt32(bytes, 0);
                int at = 4 + count;

                if (count < 0 || at > bytes.Length)
                {
                    return true;
                }

                for (int i = 0; i < count; i++)
                {
                    int length = bytes[4 + i];

                    if (at + length > bytes.Length)
                    {
                        break;
                    }

                    string name = Encoding.UTF8.GetString(bytes, at, length);
                    at += length;

                    if (name.Length > 0 && name[0] == '/')
                    {
                        folders.Add(name.Substring(1));
                    }
                    else if (name.Length > 0)
                    {
                        files.Add(name);
                    }
                }

                return true;
            }

            string text = Encoding.UTF8.GetString(bytes);

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '*')
                {
                    folders.Add(line.Substring(1));
                }
                else
                {
                    files.Add(line);
                }
            }

            return true;
        }

        private void ReadExactly(long offset, byte[] buffer, int index, int count)
        {
            lock (_gate)
            {
                _stream.Position = offset;
                int total = 0;

                while (total < count)
                {
                    int got = _stream.Read(buffer, index + total, count - total);

                    if (got <= 0)
                    {
                        throw new EndOfStreamException("Archive " + FilePath + " ended at " + (offset + total) +
                                                       " while reading " + count + " bytes.");
                    }

                    total += got;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
