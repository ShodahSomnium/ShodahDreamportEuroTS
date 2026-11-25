using System; using System.IO;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
 int pe=BitConverter.ToInt32(b,0x3c); long ib=BitConverter.ToInt64(b,pe+24+24); Console.WriteLine("imagebase "+ib.ToString("x"));
 foreach(var s in a){ long rva=Convert.ToInt64(s,16); long va=ib+rva; for(int i=0;i<b.Length-8;i+=1){ if(BitConverter.ToInt64(b,i)==va){ Console.Write(s+" at off "+i.ToString("x")+": "); for(int j=-32;j<48;j+=8) Console.Write(BitConverter.ToInt64(b,i+j).ToString("x")+" "); Console.WriteLine(); } } } } }
