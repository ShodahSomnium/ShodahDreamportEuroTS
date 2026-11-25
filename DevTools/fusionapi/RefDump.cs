using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
// Lists every type and member the world assembly references from Fusion.* and SomniumSpace.* assemblies.
static class RefDump
{
    static bool Watched(IMetadataScope s)
    {
        string n = s == null ? "" : s.Name;
        return n.StartsWith("Fusion") || n.StartsWith("SomniumSpace");
    }
    static IMetadataScope Scope(TypeReference t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        if (t is TypeSpecification ts) return Scope(ts.ElementType);
        return t.Scope;
    }
    static void Main(string[] args)
    {
        var m = ModuleDefinition.ReadModule(args[0]);
        var outp = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in m.GetTypeReferences()) if (Watched(Scope(t))) outp.Add("T " + t.FullName);
        foreach (var r in m.GetMemberReferences())
        {
            var dt = r.DeclaringType;
            if (dt is GenericInstanceType g) dt = g.ElementType;
            if (dt == null || !Watched(Scope(dt))) continue;
            string sig = r.Name;
            if (r is MethodReference mr) sig += "(" + string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName)) + ")";
            outp.Add("M " + dt.FullName + "::" + sig);
        }
        foreach (var s in outp) Console.WriteLine(s);
    }
}
