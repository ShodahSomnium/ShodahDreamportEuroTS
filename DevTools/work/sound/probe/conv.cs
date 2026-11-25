// Converts python-fsb5's vorbis_headers.py (MIT, Simon Pinfold) into a binary table: int count, then {uint crc, int len, bytes}.
using System; using System.IO; using System.Collections.Generic; using System.Text;
static class P{ static void Main(string[] a){
 string s=File.ReadAllText(a[0]); int i=s.IndexOf('{')+1; var outp=new List<KeyValuePair<uint,byte[]>>();
 const char BS=(char)92;
 while(true){
  while(i<s.Length && (char.IsWhiteSpace(s[i])||s[i]==',')) i++;
  if(i>=s.Length||s[i]=='}') break;
  int c=s.IndexOf(':',i); uint key=uint.Parse(s.Substring(i,c-i).Trim()); i=c+1;
  var ms=new MemoryStream();
  while(true){
   while(i<s.Length && char.IsWhiteSpace(s[i])) i++;
   if(s[i]!='b') break; i++; char q=s[i++];
   while(s[i]!=q){ char ch=s[i++];
    if(ch==BS){ char e=s[i++];
     if(e=='x'){ ms.WriteByte(Convert.ToByte(s.Substring(i,2),16)); i+=2; }
     else if(e=='n') ms.WriteByte(10); else if(e=='r') ms.WriteByte(13); else if(e=='t') ms.WriteByte(9);
     else if(e==BS||e=='\''||e=='"') ms.WriteByte((byte)e); else throw new Exception("esc "+e);
    } else ms.WriteByte((byte)ch); }
   i++; }
  outp.Add(new KeyValuePair<uint,byte[]>(key, ms.ToArray()));
 }
 using(var w=new BinaryWriter(File.Create(a[1]))){ w.Write(outp.Count); foreach(var kv in outp){ w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value);} }
 long tot=0; foreach(var kv in outp) tot+=kv.Value.Length; Console.WriteLine(outp.Count+" headers, "+tot+" bytes; first starts "+outp[0].Value[0]+" "+Encoding.ASCII.GetString(outp[0].Value,1,6));
 int ok=0; foreach(var kv in outp) if(Crc(kv.Value)==kv.Key) ok++; Console.WriteLine("crc32 matches key: "+ok+"/"+outp.Count);
}
 static uint Crc(byte[] b){ uint c=0xFFFFFFFF; foreach(var x in b){ c^=x; for(int k=0;k<8;k++) c=(c>>1)^(0xEDB88320u&(uint)-(int)(c&1)); } return ~c; }
}
