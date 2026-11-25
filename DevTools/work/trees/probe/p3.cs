using System; using System.Globalization; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// p3 <roadUidHex> <gx> <gz>: a road's nodes, look and points every 25 m (game coords), searched in the sector of (gx, gz) and its neighbours.
static class P3 {
  static void Main(string[] a){
    var ic=CultureInfo.InvariantCulture; ulong uid=Convert.ToUInt64(a[0],16); float gx=float.Parse(a[1],ic), gz=float.Parse(a[2],ic);
    var w=new Win(9); EtsMapSector.SectorOf(gx,gz,out int sx,out int sz);
    for(int x=sx-1;x<=sx+1;x++) for(int z=sz-1;z<=sz+1;z++){ var s=w.Get(x,z); if(s==null) continue;
      foreach(var r in s.Roads.Where(r=>r.Uid==uid)){
        var st=s.NodesByUid[r.StartNode]; var en=s.NodesByUid[r.EndNode]; var lk=w.Map.RoadLook(r.LookToken);
        Console.WriteLine($"road {uid:x} look {r.Look} \"{lk?.GetString("name","")}\" len {r.Length:0.0} start ({st.Position[0]:0.0},{st.Position[1]:0.0},{st.Position[2]:0.0}) end ({en.Position[0]:0.0},{en.Position[1]:0.0},{en.Position[2]:0.0})");
        var f0=st.Forward(); var f1=en.Forward(); Console.WriteLine($"  fwd0 ({f0[0]:0.000},{f0[2]:0.000}) fwd1 ({f1[0]:0.000},{f1[2]:0.000}); unity yaw a->b {Math.Atan2(f0[0], f0[2]*-1)*180/Math.PI:0}");
        // Unity-frame Hermite, game coords printed
        UnityEngine.Vector3 p0=new UnityEngine.Vector3(st.Position[0],st.Position[1],-st.Position[2]), p1=new UnityEngine.Vector3(en.Position[0],en.Position[1],-en.Position[2]);
        UnityEngine.Vector3 t0=new UnityEngine.Vector3(f0[0],f0[1],-f0[2]).normalized*r.Length, t1=new UnityEngine.Vector3(f1[0],f1[1],-f1[2]).normalized*r.Length;
        int n=Math.Max(1,(int)(r.Length/25)); for(int i=0;i<=n;i++){ float t=i/(float)n, t2=t*t,t3=t2*t; var p=(2*t3-3*t2+1)*p0+(t3-2*t2+t)*t0+(-2*t3+3*t2)*p1+(t3-t2)*t1; var d=(6*t2-6*t)*p0+(3*t2-4*t+1)*t0+(-6*t2+6*t)*p1+(3*t2-2*t)*t1; Console.WriteLine($"  s {t:0.00}: game ({p.x:0.0}, {p.y:0.0}, {-p.z:0.0}) unity heading yaw {Math.Atan2(d.x,d.z)*180/Math.PI:0}"); }
      }
    }
  }
}
