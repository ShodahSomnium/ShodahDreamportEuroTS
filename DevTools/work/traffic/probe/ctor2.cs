using System; using System.IO; using System.Collections.Generic;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
 int end=1536+31166976; var hist=new Dictionary<string,int>();
 foreach(var s in a){ int disp=Convert.ToInt32(s,16); var h=new Dictionary<uint,int>();
 for(int i=1536;i<end-10;i++){
  if(b[i]==0xC7 && (b[i+1]&0xC0)==0x80 && ((b[i+1]>>3)&7)==0 && (b[i+1]&7)!=4 && BitConverter.ToInt32(b,i+2)==disp){ uint v=BitConverter.ToUInt32(b,i+6); h[v]=h.TryGetValue(v,out var n)?n+1:1; if(v==0x3f800000||v==0) Console.WriteLine(s+" at "+i.ToString("x")+" = "+BitConverter.ToSingle(b,i+6)); }
 }
 foreach(var kv in h) Console.WriteLine(s+" value 0x"+kv.Key.ToString("x")+" ("+BitConverter.ToSingle(BitConverter.GetBytes(kv.Key),0)+") x"+kv.Value);
 } } }
