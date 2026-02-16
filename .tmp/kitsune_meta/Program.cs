using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length == 0)
{
    Console.WriteLine("usage: kitsune_meta <path-to-dll>");
    return;
}

var path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

foreach (var handle in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(handle);
    var ns = md.GetString(type.Namespace);
    var name = md.GetString(type.Name);
    var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

    if (!full.Contains("KitsuneMenu", StringComparison.OrdinalIgnoreCase) &&
        !full.StartsWith("Menu", StringComparison.OrdinalIgnoreCase))
        continue;

    Console.WriteLine($"TYPE {full}");

    foreach (var mHandle in type.GetMethods())
    {
        var m = md.GetMethodDefinition(mHandle);
        var methodName = md.GetString(m.Name);

        if (methodName != ".ctor" && methodName != "Create" && methodName != "Init" &&
            methodName != "TryGetSession" && methodName != "CloseMenu" && methodName != "Cleanup")
            continue;

        var sig = md.GetBlobReader(m.Signature);
        var header = sig.ReadSignatureHeader();
        if (header.IsGeneric)
            _ = sig.ReadCompressedInteger();
        var parameterCount = sig.ReadCompressedInteger();

        Console.WriteLine($"  METHOD {methodName} (params={parameterCount})");
    }
}
