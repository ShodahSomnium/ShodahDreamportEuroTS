using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var w=new Win(1); var m=w.Map; var fs=w.Fs;
 foreach(var name in a){ EtsSiiUnit d = name.StartsWith("bld_model.") ? m.BuildingModel(name) : m.ModelDefinition(EtsToken.Encode(name));
  if(d==null){ Console.WriteLine(name+": no def"); continue; }
  string md=d.GetString("model_desc", d.GetString("model_desc","")); Console.WriteLine(name+": "+string.Join(" ", d.Attributes.Select(kv=>kv.Key+"="+kv.Value)));
  if(string.IsNullOrEmpty(md)) continue; string pmd=md; string pmg=pmd.Replace(".pmd",".pmg"); Console.WriteLine("   pmd "+(fs.Read(pmd)!=null)+" pmg "+(fs.Read(pmg)!=null));
  var txt=fs.Read(pmd); if(txt==null) continue; 
  // list material paths referenced in pmd
  string s=System.Text.Encoding.ASCII.GetString(txt); foreach(var tok in s.Split('\0').Where(t=>t.EndsWith(".mat"))) { var mt=fs.ReadText(tok); string eff= mt==null?"MISSING": (mt.Split('\n').FirstOrDefault(l=>l.Contains("effect")||l.Contains("material :"))??"").Trim(); Console.WriteLine("   mat "+tok+" -> "+eff); }
 } } }
