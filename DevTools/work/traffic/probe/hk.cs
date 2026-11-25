using System; using System.Text; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static EtsFileSystem fs;
 static void Main(string[] a){ fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  foreach(var defp in a){ var t=fs.ReadText(defp); var f=EtsSii.Parse(t,defp,fs.ReadText); foreach(var u in f.Units){ var desc=u.GetString("model"); if(desc==null) continue; Console.WriteLine(defp+" "+desc);
   byte[] b = fs.Read(desc.Substring(0, desc.Length - 4) + ".pmg");
   int count = BitConverter.ToInt32(b, 20), table = BitConverter.ToInt32(b, 80), pool = BitConverter.ToInt32(b, 88);
   for (int i = 0; i < count; i++){ int o = table + i * 44; uint h = BitConverter.ToUInt32(b, o + 40); string hk=""; if(h!=0xFFFFFFFFu){ int s=(int)(pool+h), e=s; while(e<b.Length&&b[e]!=0)e++; hk=Encoding.ASCII.GetString(b,s,e-s);} 
     string n=EtsToken.Decode(BitConverter.ToUInt64(b,o)); if(hk.Length>0 && !hk.StartsWith("flare") || n.StartsWith("hook")) Console.WriteLine("  "+n+" hookup="+hk+" pos "+BitConverter.ToSingle(b,o+8).ToString("0.00")+","+BitConverter.ToSingle(b,o+12).ToString("0.00")+","+BitConverter.ToSingle(b,o+16).ToString("0.00")+" rot w"+BitConverter.ToSingle(b,o+24).ToString("0.00")+" "+BitConverter.ToSingle(b,o+28).ToString("0.00")+","+BitConverter.ToSingle(b,o+32).ToString("0.00")+","+BitConverter.ToSingle(b,o+36).ToString("0.00")); } } } } }
