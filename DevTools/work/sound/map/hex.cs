using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  var bb=fs.Read("/map/europe/sec+0000+0000.snd"); var s=EtsMapSector.Parse(0,0,bb,null,null);
  Console.WriteLine("nodes "+s.Nodes.Count+" items "+s.Items.Count+" version "+BitConverter.ToUInt32(bb,0));
  int k=0; foreach(var it in s.Items){ if(k++>8) break; var o=it.BodyOffset; Console.WriteLine(it.Type+" uid "+it.Uid.ToString("x")+" "+string.Join(" ",Enumerable.Range(0,it.BodyLength/4).Select(i=>BitConverter.ToUInt32(bb,o+i*4).ToString("x8"))));
    foreach(var n in s.Nodes) if(n.ForwardItem==it.Uid||n.BackwardItem==it.Uid) Console.WriteLine("   node "+n.Uid.ToString("x")+" at "+string.Join(",",n.Position.Select(x=>x.ToString("0.0")))+" rot "+string.Join(",",n.Rotation.Select(x=>x.ToString("0.00")))); }
}}
