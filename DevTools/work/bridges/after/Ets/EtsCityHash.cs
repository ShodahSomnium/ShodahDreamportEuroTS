using System;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// CityHash64, which is how a HashFS archive names its files: an entry is found by hashing
    /// its path, not by a directory of names. Without this there is no way into base.scs.
    ///
    /// Two editions of the short branches are here because Google changed them between
    /// CityHash 1.0 and 1.1 - the 17-to-32 and 33-to-64 branches differ, while the empty string
    /// agrees - and the engine's archives were built with one of them. Which one is not written
    /// down anywhere, so it was settled by measurement: every name listed in def.scs's folders
    /// was hashed both ways and looked up. The 1.0 branches resolve all 32,592 paths of 64 bytes
    /// or fewer; the 1.1 branches resolve one (the root). Over 64 bytes the loop written here
    /// resolves all 4,949 such paths, and the loop this author remembered as "1.0" resolves
    /// none - so that memory was wrong, and the code follows the archive rather than the memory.
    /// <see cref="Default"/> is the answer, and the harness that established it is the place to
    /// look if a future build stops resolving.
    ///
    /// Written from the reference implementation rather than taken from a library, because a
    /// world script is compiled on its own against a fixed set of assemblies and cannot carry a
    /// NuGet package with it.
    /// </summary>
    public static class EtsCityHash
    {
        public enum Edition
        {
            /// <summary>CityHash 1.0 short branches: the k3 constant and RotateByAtLeast1. The game's.</summary>
            V10,

            /// <summary>CityHash 1.1 short branches, with the per-length multiplier. Kept for diagnosis.</summary>
            V11
        }

        /// <summary>The edition the game's archives were hashed with. Established by test.</summary>
        public static Edition Default = Edition.V10;

        private const ulong K0 = 0xc3a5c85c97cb3127UL;
        private const ulong K1 = 0xb492b66fbe98f273UL;
        private const ulong K2 = 0x9ae16a3b2f90404fUL;
        private const ulong K3 = 0xc949d7c7509e6557UL;
        private const ulong KMul = 0x9ddfea08eb382d69UL;

        /// <summary>Hashes a path the way the archive does: UTF-8 bytes, no leading slash.</summary>
        public static ulong Hash(string path)
        {
            return Hash(path, Default);
        }

        public static ulong Hash(string path, Edition edition)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(path ?? string.Empty);
            return Hash(bytes, 0, bytes.Length, edition);
        }

        public static ulong Hash(byte[] s, int offset, int length, Edition edition)
        {
            if (length <= 16)
            {
                return edition == Edition.V11 ? HashLen0To16V11(s, offset, length) : HashLen0To16V10(s, offset, length);
            }

            if (length <= 32)
            {
                return edition == Edition.V11 ? HashLen17To32V11(s, offset, length) : HashLen17To32V10(s, offset, length);
            }

            if (length <= 64)
            {
                return edition == Edition.V11 ? HashLen33To64V11(s, offset, length) : HashLen33To64V10(s, offset, length);
            }

            // The same loop for both editions - see the class summary for the measurement that
            // put it here.
            return HashLong(s, offset, length);
        }

        // ------------------------------------------------------------------ primitives

        private static ulong Fetch64(byte[] s, int p)
        {
            return (ulong)s[p] | ((ulong)s[p + 1] << 8) | ((ulong)s[p + 2] << 16) | ((ulong)s[p + 3] << 24) |
                   ((ulong)s[p + 4] << 32) | ((ulong)s[p + 5] << 40) | ((ulong)s[p + 6] << 48) | ((ulong)s[p + 7] << 56);
        }

        private static uint Fetch32(byte[] s, int p)
        {
            return (uint)s[p] | ((uint)s[p + 1] << 8) | ((uint)s[p + 2] << 16) | ((uint)s[p + 3] << 24);
        }

        private static ulong Rotate(ulong val, int shift)
        {
            return shift == 0 ? val : (val >> shift) | (val << (64 - shift));
        }

        private static ulong RotateByAtLeast1(ulong val, int shift)
        {
            return (val >> shift) | (val << (64 - shift));
        }

        private static ulong ShiftMix(ulong val)
        {
            return val ^ (val >> 47);
        }

        private static ulong Bswap64(ulong v)
        {
            return ((v & 0x00000000000000FFUL) << 56) | ((v & 0x000000000000FF00UL) << 40) |
                   ((v & 0x0000000000FF0000UL) << 24) | ((v & 0x00000000FF000000UL) << 8) |
                   ((v & 0x000000FF00000000UL) >> 8) | ((v & 0x0000FF0000000000UL) >> 24) |
                   ((v & 0x00FF000000000000UL) >> 40) | ((v & 0xFF00000000000000UL) >> 56);
        }

        private static ulong HashLen16(ulong u, ulong v)
        {
            return HashLen16(u, v, KMul);
        }

        private static ulong HashLen16(ulong u, ulong v, ulong mul)
        {
            ulong a = (u ^ v) * mul;
            a ^= a >> 47;
            ulong b = (v ^ a) * mul;
            b ^= b >> 47;
            b *= mul;
            return b;
        }

        private static void WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b,
            out ulong first, out ulong second)
        {
            a += w;
            b = Rotate(b + a + z, 21);
            ulong c = a;
            a += x;
            a += y;
            b += Rotate(a, 44);
            first = a + z;
            second = b + c;
        }

        private static void WeakHashLen32WithSeeds(byte[] s, int p, ulong a, ulong b, out ulong first, out ulong second)
        {
            WeakHashLen32WithSeeds(Fetch64(s, p), Fetch64(s, p + 8), Fetch64(s, p + 16), Fetch64(s, p + 24), a, b,
                out first, out second);
        }

        // ------------------------------------------------------------------ CityHash 1.1

        private static ulong HashLen0To16V11(byte[] s, int p, int len)
        {
            if (len >= 8)
            {
                ulong mul = K2 + (ulong)len * 2;
                ulong a = Fetch64(s, p) + K2;
                ulong b = Fetch64(s, p + len - 8);
                ulong c = Rotate(b, 37) * mul + a;
                ulong d = (Rotate(a, 25) + b) * mul;
                return HashLen16(c, d, mul);
            }

            if (len >= 4)
            {
                ulong mul = K2 + (ulong)len * 2;
                ulong a = Fetch32(s, p);
                return HashLen16((ulong)len + (a << 3), Fetch32(s, p + len - 4), mul);
            }

            if (len > 0)
            {
                byte a = s[p];
                byte b = s[p + (len >> 1)];
                byte c = s[p + len - 1];
                uint y = a + ((uint)b << 8);
                uint z = (uint)len + ((uint)c << 2);
                return ShiftMix(y * K2 ^ z * K0) * K2;
            }

            return K2;
        }

        private static ulong HashLen17To32V11(byte[] s, int p, int len)
        {
            ulong mul = K2 + (ulong)len * 2;
            ulong a = Fetch64(s, p) * K1;
            ulong b = Fetch64(s, p + 8);
            ulong c = Fetch64(s, p + len - 8) * mul;
            ulong d = Fetch64(s, p + len - 16) * K2;
            return HashLen16(Rotate(a + b, 43) + Rotate(c, 30) + d, a + Rotate(b + K2, 18) + c, mul);
        }

        private static ulong HashLen33To64V11(byte[] s, int p, int len)
        {
            ulong mul = K2 + (ulong)len * 2;
            ulong a = Fetch64(s, p) * K2;
            ulong b = Fetch64(s, p + 8);
            ulong c = Fetch64(s, p + len - 24);
            ulong d = Fetch64(s, p + len - 32);
            ulong e = Fetch64(s, p + 16) * K2;
            ulong f = Fetch64(s, p + 24) * 9;
            ulong g = Fetch64(s, p + len - 8);
            ulong h = Fetch64(s, p + len - 16) * mul;
            ulong u = Rotate(a + g, 43) + (Rotate(b, 30) + c) * 9;
            ulong v = ((a + g) ^ d) + f + 1;
            ulong w = Bswap64((u + v) * mul) + h;
            ulong x = Rotate(e + f, 42) + c;
            ulong y = (Bswap64((v + w) * mul) + g) * mul;
            ulong z = e + f + c;
            a = Bswap64((x + z) * mul + y) + b;
            b = ShiftMix((z + a) * mul + d + h) * mul;
            return b + x;
        }

        /// <summary>
        /// Inputs over 64 bytes. This is the loop CityHash 1.1 ships; it is also the loop the
        /// game's archives were hashed with, whatever their short branches say - 4,949 of 4,949
        /// long paths in def.scs resolve through it.
        /// </summary>
        private static ulong HashLong(byte[] s, int p, int len)
        {
            ulong x = Fetch64(s, p + len - 40);
            ulong y = Fetch64(s, p + len - 16) + Fetch64(s, p + len - 56);
            ulong z = HashLen16(Fetch64(s, p + len - 48) + (ulong)len, Fetch64(s, p + len - 24));

            WeakHashLen32WithSeeds(s, p + len - 64, (ulong)len, z, out ulong vFirst, out ulong vSecond);
            WeakHashLen32WithSeeds(s, p + len - 32, y + K1, x, out ulong wFirst, out ulong wSecond);

            x = x * K1 + Fetch64(s, p);
            len = (len - 1) & ~63;

            do
            {
                x = Rotate(x + y + vFirst + Fetch64(s, p + 8), 37) * K1;
                y = Rotate(y + vSecond + Fetch64(s, p + 48), 42) * K1;
                x ^= wSecond;
                y += vFirst + Fetch64(s, p + 40);
                z = Rotate(z + wFirst, 33) * K1;
                WeakHashLen32WithSeeds(s, p, vSecond * K1, x + wFirst, out vFirst, out vSecond);
                WeakHashLen32WithSeeds(s, p + 32, z + wSecond, y + Fetch64(s, p + 16), out wFirst, out wSecond);
                ulong t = z;
                z = x;
                x = t;
                p += 64;
                len -= 64;
            } while (len != 0);

            return HashLen16(HashLen16(vFirst, wFirst) + ShiftMix(y) * K1 + z, HashLen16(vSecond, wSecond) + x);
        }

        // ------------------------------------------------------------------ CityHash 1.0

        private static ulong HashLen0To16V10(byte[] s, int p, int len)
        {
            if (len > 8)
            {
                ulong a = Fetch64(s, p);
                ulong b = Fetch64(s, p + len - 8);
                return HashLen16(a, RotateByAtLeast1(b + (ulong)len, len)) ^ b;
            }

            if (len >= 4)
            {
                ulong a = Fetch32(s, p);
                return HashLen16((ulong)len + (a << 3), Fetch32(s, p + len - 4));
            }

            if (len > 0)
            {
                byte a = s[p];
                byte b = s[p + (len >> 1)];
                byte c = s[p + len - 1];
                uint y = a + ((uint)b << 8);
                uint z = (uint)len + ((uint)c << 2);
                return ShiftMix(y * K2 ^ z * K3) * K2;
            }

            return K2;
        }

        private static ulong HashLen17To32V10(byte[] s, int p, int len)
        {
            ulong a = Fetch64(s, p) * K1;
            ulong b = Fetch64(s, p + 8);
            ulong c = Fetch64(s, p + len - 8) * K2;
            ulong d = Fetch64(s, p + len - 16) * K0;
            return HashLen16(Rotate(a - b, 43) + Rotate(c, 30) + d, a + Rotate(b ^ K3, 20) - c + (ulong)len);
        }

        private static ulong HashLen33To64V10(byte[] s, int p, int len)
        {
            ulong z = Fetch64(s, p + 24);
            ulong a = Fetch64(s, p) + ((ulong)len + Fetch64(s, p + len - 16)) * K0;
            ulong b = Rotate(a + z, 52);
            ulong c = Rotate(a, 37);
            a += Fetch64(s, p + 8);
            c += Rotate(a, 7);
            a += Fetch64(s, p + 16);
            ulong vf = a + z;
            ulong vs = b + Rotate(a, 31) + c;
            a = Fetch64(s, p + 16) + Fetch64(s, p + len - 32);
            z = Fetch64(s, p + len - 8);
            b = Rotate(a + z, 52);
            c = Rotate(a, 37);
            a += Fetch64(s, p + len - 24);
            c += Rotate(a, 7);
            a += Fetch64(s, p + len - 16);
            ulong wf = a + z;
            ulong ws = b + Rotate(a, 31) + c;
            ulong r = ShiftMix((vf + ws) * K2 + (wf + vs) * K0);
            return ShiftMix(r * K0 + vs) * K2;
        }

    }
}
