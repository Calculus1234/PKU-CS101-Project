using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

class ILDiff
{
    static Dictionary<string, string> Dump(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        foreach (var handle in md.MethodDefinitions)
        {
            var def = md.GetMethodDefinition(handle);
            if (def.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(def.RelativeVirtualAddress);
            var il = body.GetILBytes() ?? Array.Empty<byte>();
            string name = md.GetString(def.Name);
            string typeName = "";
            if (def.GetDeclaringType().IsNil == false)
            {
                var td = md.GetTypeDefinition(def.GetDeclaringType());
                typeName = md.GetString(td.Namespace) + "." + md.GetString(td.Name);
            }
            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(il));
            string key = typeName + "::" + name;
            int dup = 0;
            while (map.ContainsKey(key)) key = typeName + "::" + name + "#" + (++dup);
            map[key] = hash;
        }
        return map;
    }

    static int Main(string[] args)
    {
        var a = Dump(args[0]);
        var b = Dump(args[1]);
        int same = 0, diff = 0, onlyA = 0, onlyB = 0;
        foreach (var kv in a)
        {
            if (b.TryGetValue(kv.Key, out var h))
            {
                if (h == kv.Value) same++;
                else { diff++; Console.WriteLine("DIFF  " + kv.Key); }
            }
            else { onlyA++; Console.WriteLine("ONLY-A " + kv.Key); }
        }
        foreach (var kv in b) if (!a.ContainsKey(kv.Key)) { onlyB++; Console.WriteLine("ONLY-B " + kv.Key); }
        Console.WriteLine($"same={same} diff={diff} onlyA={onlyA} onlyB={onlyB} totalA={a.Count} totalB={b.Count}");
        return (diff + onlyA + onlyB) == 0 ? 0 : 1;
    }
}
