using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class Probe
{
    static Dictionary<string, List<string>> Collect(string path)
    {
        var result = new Dictionary<string, List<string>>();
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var tdh in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(tdh);
            var ns = mr.GetString(td.Namespace);
            var name = mr.GetString(td.Name);
            if (ns != "ALHPro.Views" && ns != "ALHPro") continue;
            var key = ns + "." + name;
            var methods = new List<string>();
            foreach (var mth in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mth);
                methods.Add(mr.GetString(md.Name));
            }
            result[key] = methods;
        }
        return result;
    }

    static void Main(string[] args)
    {
        var a = Collect(args[0]);
        var b = Collect(args[1]);
        foreach (var key in a.Keys.OrderBy(k => k))
        {
            if (!b.ContainsKey(key)) { Console.WriteLine("[type only in 发布版] " + key); continue; }
            var onlyA = a[key].Except(b[key]).ToList();
            var onlyB = b[key].Except(a[key]).ToList();
            if (onlyA.Count > 0 || onlyB.Count > 0)
            {
                Console.WriteLine("== " + key);
                foreach (var m in onlyA) Console.WriteLine("   [发布版有,bin无] " + m);
                foreach (var m in onlyB) Console.WriteLine("   [bin有,发布版无] " + m);
            }
        }
        foreach (var key in b.Keys.OrderBy(k => k))
            if (!a.ContainsKey(key)) Console.WriteLine("[type only in bin] " + key);
    }
}
