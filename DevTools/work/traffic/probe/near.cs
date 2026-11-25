using System; using System.IO;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
 foreach(var s in a[0].Split(',')){ int c=Convert.ToInt32(s,16); Console.WriteLine("-- "+s);
  for(int i=c-600;i<c+600;i++){ if(b[i]==0xC7 && (b[i+1]&0xC0)==0x80 && ((b[i+1]>>3)&7)==0 && (b[i+1]&7)!=4){ int d=BitConverter.ToInt32(b,i+2); if(d>=0x240&&d<=0x270) Console.WriteLine("  "+i.ToString("x")+" [+"+d.ToString("x")+"] = 0x"+BitConverter.ToUInt32(b,i+6).ToString("x")+" ("+BitConverter.ToSingle(b,i+6)+")"); }
   // movss [r+disp32], xmm : F3 0F 11 /r mod=10
   if(b[i]==0xF3&&b[i+1]==0x0F&&b[i+2]==0x11&&(b[i+3]&0xC0)==0x80){ int d=BitConverter.ToInt32(b,i+4); if(d>=0x240&&d<=0x270) Console.WriteLine("  "+i.ToString("x")+" movss [+"+d.ToString("x")+"], xmm"+((b[i+3]>>3)&7)); } } } } }
