using System; using System.IO;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
 int end=1536+31166976;
 for(int i=1536;i<end-10;i++){
  // mov dword [r+disp32], imm32 : C7 /0 with mod=10 ; optional REX 41
  if(b[i]==0xC7 && (b[i+1]&0xC0)==0x80 && ((b[i+1]>>3)&7)==0 && (b[i+1]&7)!=4 && BitConverter.ToInt32(b,i+2)==0x134){
    float v=BitConverter.ToSingle(b,i+6);
    // look back 64 bytes for write to 0x130 or 0x11c
    bool near=false; for(int j=Math.Max(0,i-96);j<i+96;j++) if(b[j]==0xC7 && (b[j+1]&0xC0)==0x80 && (BitConverter.ToInt32(b,j+2)==0x130||BitConverter.ToInt32(b,j+2)==0x11c)) near=true;
    if(near) Console.WriteLine(i.ToString("x")+" [+134]= "+v+" (0x"+BitConverter.ToUInt32(b,i+6).ToString("x")+")");
  }
 } } }
