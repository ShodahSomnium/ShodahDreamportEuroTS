using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 var map = new EtsMap(fs); Console.WriteLine(map.SectorCount+" sectors");
 foreach(var n in map.AvailableSectors){ }
 // read all
 int minx=999,maxx=-999,minz=999,maxz=-999;
 foreach(var n in map.AvailableSectors){ // secXXXX+YYYY
   int x=int.Parse(n.Substring(3,5)), z=int.Parse(n.Substring(8,5)); var s=map.GetSector(x,z); if(s==null) continue;
   foreach(var c in s.Cities){ var nd=map.FindNode(c.NodeUid); Console.WriteLine("city "+c.Name+" sec "+n+" node "+(nd==null?"?":nd.Position[0]+","+nd.Position[1]+","+nd.Position[2])); }
 }
}}
