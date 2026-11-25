using System; using System.IO; using System.Text;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo/bin/win_x64/eurotrucks2.exe");
 long ib=0x140000000; long rdva=31174656, rdraw=31168512; long dva=40738816, draw=40729088;
 Func<long,long> off = va => { long r=va-ib; if(r>=rdva && r<rdva+9560576) return r-rdva+rdraw; if(r>=dva&&r<dva+9784832) return r-dva+draw; return -1; };
 long start=Convert.ToInt64(a[0],16); int n=int.Parse(a[1]);
 for(int k=0;k<n;k++){ long o=start+k*40; long nm=BitConverter.ToInt64(b,(int)o); long so=off(nm); string s="?"; if(so>0){int L=0; while(L<60&&b[so+L]!=0)L++; s=Encoding.ASCII.GetString(b,(int)so,L);} Console.WriteLine(o.ToString("x")+" "+s+" off="+BitConverter.ToInt64(b,(int)o+16).ToString("x")+" type="+BitConverter.ToInt64(b,(int)o+24)); } } }
