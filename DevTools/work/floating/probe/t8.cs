using System; using System.Collections.Generic; using System.Linq; using System.Globalization; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var s=w.Get(int.Parse(a[0]),int.Parse(a[1])); ulong uid=Convert.ToUInt64(a[2],16);
 foreach(var t in s.Terrains){ if(t.Uid!=uid) continue; var q=t.Right.Quads; Console.WriteLine("mats: "+string.Join(" ",q.MaterialTokens.Select((m,i)=>i+"="+EtsToken.Decode(m))));
  for(int r=q.Rows-1;r>=0;r--){ var line=""; for(int c=0;c<q.Cols;c++) line+=q.MainMaterial(r,c).ToString("x"); Console.WriteLine("row "+r.ToString("00")+" "+line); }
  var o=q.Offsets; var grid=new string[q.Rows+1, q.Cols+1]; for(int k=0;k<q.OffsetCount;k++){ int c=(int)o[k*5], r=(int)o[k*5+1]; if(r<=q.Rows&&c<=q.Cols) grid[r,c]=o[k*5+3].ToString("0"); }
  for(int k=0;k<q.OffsetCount;k++){ int c=(int)o[k*5], r=(int)o[k*5+1]; if((c==0||c==1||c==9||c==18) && (r==10||r==7||r==3||r==0||r==1||r==6)) Console.WriteLine("c"+c+" r"+r+" game ("+(16650.21+o[k*5+2]).ToString("0")+", "+(34.25+o[k*5+3]).ToString("0")+", "+(18403.17+o[k*5+4]).ToString("0")+")"); }
  for(int r=q.Rows;r>=0;r--){ var line="y r"+r.ToString("00")+" "; for(int c=0;c<=q.Cols;c++) line+=(grid[r,c]??".").PadLeft(5); Console.WriteLine(line); }
 }
}}
