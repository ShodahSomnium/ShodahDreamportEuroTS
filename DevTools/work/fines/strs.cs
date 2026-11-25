using System; using System.IO; using System.Text;
static class P { static void Main(string[] a){ var b=File.ReadAllBytes(a[0]); var o=new StreamWriter(a[1]); var sb=new StringBuilder(); int st=0;
 for(int i=0;i<=b.Length;i++){ byte c= i<b.Length?b[i]:(byte)0; if(c>=0x20&&c<0x7f){ if(sb.Length==0) st=i; sb.Append((char)c);} else { if(sb.Length>=4) o.WriteLine(st.ToString("x")+" "+sb); sb.Clear(); } }
 o.Close(); } }
