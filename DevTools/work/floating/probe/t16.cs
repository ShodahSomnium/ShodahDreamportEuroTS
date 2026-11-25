using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var w=new Win(1); var m=w.Map; var fs=w.Fs;
 foreach(var name in a){ var d=m.PrefabDefinition(EtsToken.Encode(name)) ?? m.ModelDefinition(EtsToken.Encode(name)); if(d==null){Console.WriteLine(name+" no def"); continue;} Console.WriteLine(name+": "+string.Join(" ", d.Attributes.Select(kv=>kv.Key+"="+kv.Value)));
 string md=d.GetString("prefab_desc",""); if(md=="") md=d.GetString("model_desc",""); md=md.Replace(".ppd",".pmd");
 var data=EtsModelData.Parse(fs.Read(md), fs.Read(md.Replace(".pmd",".pmg")), md); if(data==null){ Console.WriteLine("load fail "+md); continue; }
 Console.WriteLine(" "+md+" looks "+string.Join(",",data.Looks)+" variants "+string.Join(",",data.Variants));
 var mats=data.LookMaterials.Length>0?data.LookMaterials[0]:new string[0];
 foreach(var p in data.Parts){ var mi=Enumerable.Range(p.PieceStart,p.PieceCount).Where(i=>i<data.Pieces.Length).Select(i=>data.Pieces[i].MaterialIndex).Distinct();
   Console.WriteLine("  part "+p.Name+" pieces "+p.PieceCount+" mats "+string.Join(" ",mi.Select(i=>{ string mp=i<mats.Length?mats[i]:"?"; var t=fs.ReadText(mp); string e=t==null?"MISSING":(t.Split('\n').FirstOrDefault(l=>l.Contains("effect")||l.Contains("material :"))??"").Trim(); return e.Replace("effect : ","").Replace("material : ",""); }))); }
 } } }
