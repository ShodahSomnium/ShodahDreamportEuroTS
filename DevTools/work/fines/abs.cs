using System; using System.IO;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
 int pe=BitConverter.ToInt32(b,0x3c); ulong ib=BitConverter.ToUInt64(b,pe+24+24); Console.WriteLine("imagebase "+ib.ToString("x"));
 foreach(var s in a){ ulong v=ib+Convert.ToUInt64(s,16); for(int i=0;i+8<=b.Length;i+=1) if(BitConverter.ToUInt64(b,i)==v) Console.WriteLine(s+" abs ref at file off "+i.ToString("x")); } } }
