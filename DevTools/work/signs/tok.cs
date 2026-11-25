using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ foreach (var h in a){ var b=new byte[8]; for(int i=0;i<8;i++) b[i]=Convert.ToByte(h.Substring(i*2,2),16); Console.WriteLine(h+" -> '"+EtsToken.Decode(BitConverter.ToUInt64(b,0))+"'"); } } }
