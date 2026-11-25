// gen <vorbis_headers.bin> <out.cs> crc...  : writes EtsVorbisSetups.cs with only the setup headers ETS2's banks use.
using System; using System.IO; using System.Collections.Generic; using System.Text;
static class Gen { static void Main(string[] a){
  var t=new Dictionary<uint,byte[]>(); using(var r=new BinaryReader(File.OpenRead(a[0]))){ int n=r.ReadInt32(); for(int i=0;i<n;i++){ uint k=r.ReadUInt32(); int l=r.ReadInt32(); t[k]=r.ReadBytes(l);} }
  var sb=new StringBuilder();
  sb.Append(@"using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The Vorbis setup headers FMOD's encoder writes, which FSB5 leaves out of every sample and names only by CRC-32
    /// (the VORBISDATA chunk). Only the ").Append(a.Length-2).Append(@" headers ETS2 1.61's 265 sample banks use are here (all 4954 samples,
    /// measured 2026-10-14 by DevTools/work/sound/probe 'scan'); they are generic libvorbis encoder codebooks, not game
    /// data. Taken from python-fsb5's vorbis_headers.py (https://github.com/HearthSim/python-fsb5, MIT License,
    /// Copyright (c) 2016 Simon Pinfold - notice in Vorbis/LICENSE.md); each one's CRC-32 is checked against its key.
    /// </summary>
    public static class EtsVorbisSetups
    {
        private static Dictionary<uint, byte[]> s_table;
        private static readonly object s_lock = new object();

        public static byte[] Find(uint crc)
        {
            lock (s_lock)
            {
                if (s_table == null)
                {
                    s_table = new Dictionary<uint, byte[]>();
                    for (int i = 0; i < Keys.Length; i++)
                    {
                        byte[] b = Convert.FromBase64String(Data[i]);
                        if (Crc32(b) == Keys[i]) s_table[Keys[i]] = b;
                    }
                }
                return s_table.TryGetValue(crc, out byte[] found) ? found : null;
            }
        }

        public static uint Crc32(byte[] b)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < b.Length; i++)
            {
                c ^= b[i];
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
            }
            return ~c;
        }

        private static readonly uint[] Keys = { ");
  for(int i=2;i<a.Length;i++){ sb.Append(a[i]).Append("u"); if(i<a.Length-1) sb.Append(", "); }
  sb.Append(" };\n\n        private static readonly string[] Data =\n        {\n");
  for(int i=2;i<a.Length;i++){ uint k=uint.Parse(a[i]); var b64=Convert.ToBase64String(t[k]); sb.Append("            // crc ").Append(k).Append(", ").Append(t[k].Length).Append(" bytes\n            \"");
    for(int j=0;j<b64.Length;j+=100){ if(j>0) sb.Append("\" +\n            \""); sb.Append(b64.Substring(j,Math.Min(100,b64.Length-j))); } sb.Append("\",\n"); }
  sb.Append("        };\n    }\n}\n");
  File.WriteAllText(a[1], sb.ToString().Replace("\r\n","\n")); Console.WriteLine("wrote "+a[1]);
}}
