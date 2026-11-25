using System; using System.IO; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class S2 {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    string dir=a[0], outDir=a[1]; Directory.CreateDirectory(outDir);
    fs.TryList(dir, out var files, out var folders);
    var names=new List<string>(); foreach(var f in files) if(f.EndsWith(".tobj")) names.Add(f);
    names.Sort();
    int tw=320, th=113, cols=4, per=24; int sheetN=0;
    for(int s0=0;s0<names.Count;s0+=per){
      int n=Math.Min(per,names.Count-s0); int rows=(n+cols-1)/cols; var sheet=new byte[tw*cols*th*rows*4];
      for(int k=0;k<n;k++){ string p=dir+"/"+names[s0+k];
        if(!fs.TryGetEntry(p, out EtsHashFs arc, out EtsHashFs.Entry e)) continue;
        var dds=EtsDds.FromPackedSurface(arc.ReadPackedImage(e), e.ImageWidth, e.ImageHeight, e.DxgiFormat, e.MipCount,1,false,e.PitchAlign,e.ImageAlign);
        var bmp=EtsPixels.DecodeLevel(dds,0); if(bmp==null) continue;
        P.Png(outDir+"/"+names[s0+k].Replace(".tobj",".png"), bmp.Width,bmp.Height,bmp.Rgba);
        int cx=(k%cols)*tw, cy=(k/cols)*th;
        for(int y=0;y<th;y++) for(int x=0;x<tw;x++){ int sx=x*bmp.Width/tw, sy=y*bmp.Height/th; int so=(sy*bmp.Width+sx)*4, d=((cy+y)*tw*cols+cx+x)*4; for(int c=0;c<4;c++) sheet[d+c]=bmp.Rgba[so+c]; }
        Console.WriteLine("sheet"+sheetN+" #"+k+" "+names[s0+k]);
      }
      P.Png(outDir+"/_sheet"+sheetN+".png", tw*cols, th*rows, sheet); sheetN++;
    }
  }
}
