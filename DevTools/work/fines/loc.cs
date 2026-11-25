using System; using System.Text.RegularExpressions; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  EtsLocale.Load(fs); var re=new Regex(a[0], RegexOptions.IgnoreCase);
  var f=typeof(EtsLocale).GetField("_table", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static); object t=f.GetValue(null);
  foreach(System.Collections.Generic.KeyValuePair<string,string> kv in (System.Collections.Generic.Dictionary<string,string>)f.FieldType.GetField("Words").GetValue(t)) if(re.IsMatch(kv.Key)||(a.Length>1&&re.IsMatch(kv.Value))) Console.WriteLine(kv.Key+" = "+kv.Value);
}}
