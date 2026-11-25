using System;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A texture object: the small binary file a material names instead of naming a picture
    /// directly. It says which DDS holds the pixels and how to sample them - which is all a
    /// renderer needs from it.
    ///
    /// Layout, read off material/road/arrows.tobj (73 bytes) rather than from any document:
    ///
    ///     0   u32 0x70B10A01   magic
    ///     24  u8  type         2 for an ordinary 2D texture, 5 for a cube map
    ///     26  u8  mag filter, 27 min filter, 28 mip filter   3 means "default"
    ///     30  u8  address u, 31 address v
    ///     40  u32 path length
    ///     48  path             not terminated; the file ends with it
    ///
    /// The address modes matter: a road texture tiles along its length, while a decal such as
    /// arrows must clamp or its edge repeats across the whole quad.
    /// </summary>
    public sealed class EtsTobj
    {
        public const uint Magic = 0x70B10A01;

        /// <summary>Address modes, as the engine numbers them.</summary>
        public const int AddressRepeat = 0;
        public const int AddressClamp = 1;
        public const int AddressClampToEdge = 2;
        public const int AddressClampToBorder = 3;
        public const int AddressMirror = 4;
        public const int AddressMirrorClamp = 5;

        /// <summary>Absolute archive path of the DDS.</summary>
        public string TexturePath;

        public int Type;
        public int AddressU;
        public int AddressV;
        public int MagFilter;
        public int MinFilter;
        public int MipFilter;

        public bool IsCubeMap => Type == 5;

        public bool RepeatU => AddressU == AddressRepeat || AddressU == AddressMirror;

        public bool RepeatV => AddressV == AddressRepeat || AddressV == AddressMirror;

        /// <param name="bytes">The file.</param>
        /// <param name="ownPath">The tobj's own archive path, so a relative texture path can be
        /// resolved against its folder.</param>
        public static EtsTobj Parse(byte[] bytes, string ownPath)
        {
            if (bytes == null || bytes.Length < 49)
            {
                throw new ArgumentException("Not a tobj: too short (" + (bytes == null ? 0 : bytes.Length) + " bytes).");
            }

            var tobj = new EtsTobj
            {
                Type = bytes[24],
                MagFilter = bytes[26],
                MinFilter = bytes[27],
                MipFilter = bytes[28],
                AddressU = bytes[30],
                AddressV = bytes[31]
            };

            int length = BitConverter.ToInt32(bytes, 40);
            string path;

            if (length > 0 && 48 + length <= bytes.Length)
            {
                path = Encoding.ASCII.GetString(bytes, 48, length);
            }
            else
            {
                // Length field disagrees with the file. The path is still the tail of the file,
                // so take everything printable from byte 48 on.
                int end = 48;

                while (end < bytes.Length && bytes[end] >= 32 && bytes[end] < 127)
                {
                    end++;
                }

                path = Encoding.ASCII.GetString(bytes, 48, end - 48);
            }

            tobj.TexturePath = Resolve(path, ownPath);
            return tobj;
        }

        /// <summary>An absolute archive path for a reference that may be relative to another file.</summary>
        public static string Resolve(string reference, string relativeTo)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return reference;
            }

            if (reference.StartsWith("/", StringComparison.Ordinal))
            {
                return reference;
            }

            string folder = string.Empty;

            if (!string.IsNullOrEmpty(relativeTo))
            {
                int slash = relativeTo.LastIndexOf('/');
                folder = slash < 0 ? string.Empty : relativeTo.Substring(0, slash);
            }

            if (!folder.StartsWith("/", StringComparison.Ordinal))
            {
                folder = "/" + folder;
            }

            return folder.TrimEnd('/') + "/" + reference;
        }
    }
}
