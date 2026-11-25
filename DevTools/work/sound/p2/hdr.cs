using System; using System.Linq; using System.Text; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class HDR { public static void Run(EtsFileSystem fs, string[] a){ var d=fs.Read(a[0]); var b=EtsFmodBank.Parse(d,out var e); int o=b.FsbOffset; int count=BitConverter.ToInt32(d,o+8); int p=o+60;
  for(int i=0;i<count;i++){ ulong raw=BitConverter.ToUInt64(d,p); p+=8; bool next=(raw&1)!=0; var sb=new StringBuilder(i+" "+b.Samples[i].Name+" rawoff "+(((raw>>6)&0xFFFFFFF)*16)+" chunks:");
    while(next){ uint c=BitConverter.ToUInt32(d,p); p+=4; next=(c&1)!=0; int size=(int)((c>>1)&0xFFFFFF); int type=(int)((c>>25)&0x7F); sb.Append(" t"+type+"/"+size); if(type==11) sb.Append("(crc "+BitConverter.ToUInt32(d,p)+(size>4?" +"+BitConverter.ToUInt32(d,p+4)+","+BitConverter.ToUInt32(d,p+8)+","+BitConverter.ToUInt32(d,p+12):"")+")"); p+=size; }
    if(i<4||b.Samples[i].Frames>1000000) Console.WriteLine(sb); } } }
