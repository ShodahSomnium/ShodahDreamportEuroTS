using System; using System.Text; using System.Diagnostics; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  var signs = fs.ReadDefinitionFamily("/def/world","sign"); int n=0, cams=0, noword=0, missing=0; var sw=Stopwatch.StartNew();
  foreach(var u in signs.Units){ string d=u.GetString("model_desc"); if(d==null||!d.EndsWith(".pmd")) continue; n++;
    var b=fs.Read(d.Substring(0,d.Length-4)+".pmg"); if(b==null){missing++; continue;} if(b.Length<112||b[0]!=0x15) continue;
    int c=BitConverter.ToInt32(b,20), t=BitConverter.ToInt32(b,80), pool=BitConverter.ToInt32(b,88); bool cam=false;
    for(int i=0;i<c;i++){ uint h=BitConverter.ToUInt32(b,t+i*44+40); if(h==0xFFFFFFFF||pool+h>=b.Length) continue; int s=(int)(pool+h),e=s; while(e<b.Length&&b[e]!=0) e++; if(Encoding.ASCII.GetString(b,s,e-s).StartsWith("speedcam")) cam=true; }
    if(cam){ cams++; bool word=d.Contains("camera"); if(!word) noword++; Console.WriteLine((word?"  ":"!!")+" "+u.Name+" "+d); } }
  Console.WriteLine(n+" sign models with a model ("+missing+" pmg not in the demo), "+cams+" with a speedcam hookup, "+noword+" of them without 'camera' in the path; scan "+sw.ElapsedMilliseconds+" ms");
  var map=new EtsMap(fs); var fm=new EtsFineMap(fs,map); foreach(var (x,z) in new[]{(-9,-2),(3,0),(3,-4),(2,-3)}){ var s=map.GetSector(x,z); if(s==null) continue; var w=Stopwatch.StartNew(); var g=fm.Gather(s); Console.WriteLine("gather sec "+x+","+z+": "+s.Signs.Count+" signs, "+s.Roads.Count+" roads -> "+g.Cameras.Count+" cameras, "+g.Signs.Count+" rule signs in "+w.Elapsed.TotalMilliseconds.ToString("0.0")+" ms"); var w2=Stopwatch.StartNew(); fm.Forget(x,z); fm.Gather(s); Console.WriteLine("   again (models cached) "+w2.Elapsed.TotalMilliseconds.ToString("0.0")+" ms"); }
}}
