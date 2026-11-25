using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// m5 name... : model definitions by token: model_desc, bounds, parts, pieces (material, verts, bounds y)
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _); var m=new EtsMap(fs);
 foreach(var n in a){ var d=m.ModelDefinition(EtsToken.Encode(n)); if(d==null){ Console.WriteLine(n+": no def"); continue; } string p=d.GetString("model_desc",""); Console.WriteLine($"== {n}: {p} (unit {d.Name}) lod {d.GetString("lod_desc","")} cat {d.GetString("category","")}");
  try{ var md=EtsModelData.Parse(fs.Read(p), fs.Read(p.Substring(0,p.Length-4)+".pmg"), p);
   Console.WriteLine($"   bounds ({string.Join(",",md.BoundsMin.Select(f=>f.ToString("0")))})..({string.Join(",",md.BoundsMax.Select(f=>f.ToString("0")))}) looks {md.LookMaterials.Length} variants {md.Variants.Length}");
   for(int i=0;i<md.Parts.Length;i++){ var pt=md.Parts[i]; Console.WriteLine($"   part {pt.Name} pieces {pt.PieceCount} vis {string.Join("",pt.VisibleInVariant.Select(b=>b?"1":"0"))}");
     for(int k=pt.PieceStart;k<pt.PieceStart+pt.PieceCount;k++){ var pc=md.Pieces[k]; float y0=999,y1=-999; for(int v=0;v<pc.VertexCount;v++){ float y=pc.Positions[v*3+1]; y0=Math.Min(y0,y); y1=Math.Max(y1,y);} Console.WriteLine($"     piece {k} mat {(md.LookMaterials.Length>0&&pc.MaterialIndex<md.LookMaterials[0].Length?md.LookMaterials[0][pc.MaterialIndex]:pc.MaterialIndex.ToString())} verts {pc.VertexCount} y {y0:0}..{y1:0}"); } }
  }catch(Exception e){ Console.WriteLine("  parse failed "+e.Message); } } } }
