using System; using System.IO; using System.Collections.Generic;
static class P {
 static byte[] b; static List<(string n,uint va,uint vs,uint raw,uint rs)> secs=new List<(string,uint,uint,uint,uint)>();
 static long Rva(long off){ foreach(var s in secs) if(off>=s.raw && off<s.raw+s.rs) return off-s.raw+s.va; return -1; }
 static long Off(long rva){ foreach(var s in secs) if(rva>=s.va && rva<s.va+s.rs) return rva-s.va+s.raw; return -1; }
 static void Main(string[] a){
  b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
  int pe=BitConverter.ToInt32(b,0x3c); int ns=BitConverter.ToUInt16(b,pe+6); int opt=BitConverter.ToUInt16(b,pe+20); int sh=pe+24+opt;
  for(int i=0;i<ns;i++){ int o=sh+i*40; string n=System.Text.Encoding.ASCII.GetString(b,o,8).TrimEnd('\0'); secs.Add((n,BitConverter.ToUInt32(b,o+12),BitConverter.ToUInt32(b,o+8),BitConverter.ToUInt32(b,o+20),BitConverter.ToUInt32(b,o+16))); }
  if(a[0]=="secs"){ foreach(var s in secs) Console.WriteLine(s); return; }
  if(a[0]=="xref"){ long target=Rva(Convert.ToInt64(a[1],16)); Console.WriteLine("target rva "+target.ToString("x"));
    var t=secs.Find(s=>s.n==".text");
    for(long off=t.raw; off<t.raw+t.rs-4; off++){ int d=BitConverter.ToInt32(b,(int)off); long rva=off-t.raw+t.va; // disp at rva, next ip = rva+4 (+imm)
      for(int extra=0;extra<=4;extra+=4){ if(rva+4+extra+d==target){ Console.WriteLine("ref at off "+off.ToString("x")+" rva "+rva.ToString("x")+" extra="+extra); } } } }
  if(a[0]=="dump"){ long off=Convert.ToInt64(a[1],16); int n=int.Parse(a[2]); for(int i=0;i<n;i+=16){ Console.Write((off+i).ToString("x")+": "); for(int j=0;j<16;j++) Console.Write(b[off+i+j].ToString("x2")+" "); Console.WriteLine(); } }
  if(a[0]=="refs"){ Q.Refs(b, o=>Rva(o), r=>Off(r), Convert.ToInt64(a[1],16), Convert.ToInt64(a[2],16)); }
  if(a[0]=="at"){ foreach(var s0 in a[1].Split(',')){ long off=Convert.ToInt64(s0,16); int d=BitConverter.ToInt32(b,(int)off); long t=Rva(off)+4+d; long to=Off(t); int n=0; while(to>=0&&n<100&&b[to+n]!=0) n++; Console.WriteLine(s0+" -> rva "+t.ToString("x")+" off "+to.ToString("x")+" str \""+(to<0?"":System.Text.Encoding.ASCII.GetString(b,(int)to,n))+"\" f32 "+(to<0?0:BitConverter.ToSingle(b,(int)to))); } }
  if(a[0]=="f32"){ long rva=Convert.ToInt64(a[1],16); long off=Off(rva); Console.WriteLine(BitConverter.ToSingle(b,(int)off)+" / "+BitConverter.ToDouble(b,(int)off)); }
 }}
static class Q {
 public static void Refs(byte[] b, Func<long,long> rvaOf, Func<long,long> offOf, long from, long to){
  for(long off=from; off<to; off++){ int d=BitConverter.ToInt32(b,(int)off); long rva=rvaOf(off); long t=rva+4+d; long toff=offOf(t); if(toff<0||t<0x1db9000||t>0x3a00000) continue;
   // string?
   int len=0; while(toff+len<b.Length && b[toff+len]>=0x20 && b[toff+len]<0x7f && len<120) len++;
   bool prevZero = b[toff-1]==0;
   string op = (off>=3? b[off-3].ToString("x2")+" "+b[off-2].ToString("x2")+" "+b[off-1].ToString("x2"):"");
   if(len>=3 && b[toff+len]==0 && prevZero) Console.WriteLine(off.ToString("x")+" ["+op+"] str "+System.Text.Encoding.ASCII.GetString(b,(int)toff,len));
   else if(b[off-1]==0x05||b[off-1]==0x0d||b[off-1]==0x15||b[off-1]==0x1d||b[off-1]==0x25||b[off-1]==0x2d||b[off-1]==0x35||b[off-1]==0x3d){ if(off>=4 && (b[off-3]==0x0f) && (b[off-4]==0xf3||b[off-4]==0xf2||b[off-4]==0x66||b[off-4]==0x0f||true)) Console.WriteLine(off.ToString("x")+" ["+op+"] f32 "+BitConverter.ToSingle(b,(int)toff)+" f64 "+BitConverter.ToDouble(b,(int)toff)); }
  }
 }
}
