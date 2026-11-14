using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;

// args[0]: plugin dll; args[1]: game Managed dir. Lists UnityEngine type/member references the
// plugin makes that the game's UnityEngine assemblies do not define (by type full name and
// member name + parameter count for methods).
var plugin = args[0]; var managed = args[1];
var types = new Dictionary<string, (HashSet<string> fields, Dictionary<string, HashSet<int>> methods)>();
foreach (var f in Directory.GetFiles(managed, "UnityEngine*.dll"))
{
    using var pe = new PEReader(File.OpenRead(f));
    if (!pe.HasMetadata) continue;
    var md = pe.GetMetadataReader();
    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        string name = Full(md, td);
        if (!types.TryGetValue(name, out var t)) types[name] = t = (new HashSet<string>(), new Dictionary<string, HashSet<int>>());
        foreach (var fh in td.GetFields()) t.fields.Add(md.GetString(md.GetFieldDefinition(fh).Name));
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var n = md.GetString(m.Name);
            int pc = m.DecodeSignature(new Dummy(), null).ParameterTypes.Length;
            if (!t.methods.TryGetValue(n, out var set)) t.methods[n] = set = new HashSet<int>();
            set.Add(pc);
        }
    }
}
// forwarded types (UnityEngine.dll facade) are fine as long as a definition exists somewhere
static string Full(MetadataReader md, TypeDefinition td)
{
    var ns = md.GetString(td.Namespace); var n = md.GetString(td.Name);
    if (td.IsNested) { var parent = md.GetTypeDefinition(td.GetDeclaringType()); return Full(md, parent) + "/" + n; }
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}
static string RefName(MetadataReader md, TypeReferenceHandle h)
{
    var tr = md.GetTypeReference(h);
    var n = md.GetString(tr.Name); var ns = md.GetString(tr.Namespace);
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return RefName(md, (TypeReferenceHandle)tr.ResolutionScope) + "/" + n;
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}
static bool IsUnity(MetadataReader md, TypeReferenceHandle h)
{
    var tr = md.GetTypeReference(h);
    while (tr.ResolutionScope.Kind == HandleKind.TypeReference) tr = md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope);
    if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
    var a = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
    return a.StartsWith("UnityEngine");
}
using var ppe = new PEReader(File.OpenRead(plugin));
var pmd = ppe.GetMetadataReader();
var missing = new SortedSet<string>();
foreach (var th in pmd.TypeReferences)
    if (IsUnity(pmd, th) && !types.ContainsKey(RefName(pmd, th))) missing.Add("TYPE   " + RefName(pmd, th));
foreach (var mh in pmd.MemberReferences)
{
    var mr = pmd.GetMemberReference(mh);
    if (mr.Parent.Kind != HandleKind.TypeReference) continue;
    var th = (TypeReferenceHandle)mr.Parent;
    if (!IsUnity(pmd, th)) continue;
    var tn = RefName(pmd, th); var n = pmd.GetString(mr.Name);
    if (!types.TryGetValue(tn, out var t)) continue; // reported as TYPE
    if (mr.GetKind() == MemberReferenceKind.Field) { if (!t.fields.Contains(n)) missing.Add($"FIELD  {tn}.{n}"); continue; }
    int pc = mr.DecodeMethodSignature(new Dummy(), null).ParameterTypes.Length;
    if (!t.methods.TryGetValue(n, out var set) || !set.Contains(pc)) missing.Add($"METHOD {tn}.{n}({pc})");
}
// Which plugin methods use each missing member (IL scan: every token operand).
var missingRefs = new Dictionary<int, string>();
foreach (var mh in pmd.MemberReferences)
{
    var mr = pmd.GetMemberReference(mh);
    if (mr.Parent.Kind != HandleKind.TypeReference) continue;
    var th = (TypeReferenceHandle)mr.Parent;
    if (!IsUnity(pmd, th)) continue;
    var tn = RefName(pmd, th); var n = pmd.GetString(mr.Name);
    bool miss;
    if (!types.TryGetValue(tn, out var t)) miss = true;
    else if (mr.GetKind() == MemberReferenceKind.Field) miss = !t.fields.Contains(n);
    else { int pc = mr.DecodeMethodSignature(new Dummy(), null).ParameterTypes.Length; miss = !t.methods.TryGetValue(n, out var set) || !set.Contains(pc); }
    if (miss) missingRefs[MetadataTokens.GetToken(mh)] = tn + "." + n;
}
foreach (var th in pmd.TypeReferences)
    if (IsUnity(pmd, th) && !types.ContainsKey(RefName(pmd, th))) missingRefs[MetadataTokens.GetToken(th)] = RefName(pmd, th);
var opSizes = new Dictionary<ushort, int>();
foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields())
{
    var op = (System.Reflection.Emit.OpCode)f.GetValue(null);
    int size = op.OperandType switch {
        System.Reflection.Emit.OperandType.InlineNone => 0,
        System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
        System.Reflection.Emit.OperandType.InlineVar => 2,
        System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
        System.Reflection.Emit.OperandType.InlineSwitch => -1,
        _ => 4 };
    opSizes[(ushort)op.Value] = size;
}
var uses = new SortedSet<string>();
foreach (var tdh in pmd.TypeDefinitions)
{
    var td = pmd.GetTypeDefinition(tdh);
    string tname = Full(pmd, td);
    foreach (var mdh in td.GetMethods())
    {
        var md = pmd.GetMethodDefinition(mdh);
        if (md.RelativeVirtualAddress == 0) continue;
        var body = ppe.GetMethodBody(md.RelativeVirtualAddress);
        var il = body.GetILBytes();
        // locals signature types too
        int i = 0;
        while (i < il.Length)
        {
            ushort code = il[i];
            if (code == 0xFE) { code = (ushort)(0xFE00 | il[i + 1]); i += 2; } else i += 1;
            if (!opSizes.TryGetValue(code, out int size)) break;
            if (size == -1) { int count = BitConverter.ToInt32(il, i); i += 4 + 4 * count; continue; }
            if (size == 4)
            {
                int token = BitConverter.ToInt32(il, i);
                if (missingRefs.TryGetValue(token, out var what)) uses.Add($"{tname}::{pmd.GetString(md.Name)}  ->  {what}");
                else if ((token >> 24) == 0x1B) { } // typespec: ignored
            }
            i += size;
        }
    }
}
foreach (var u in uses) Console.WriteLine(u);

class Dummy : ISignatureTypeProvider<int, object>
{
    public int GetArrayType(int e, ArrayShape s) => 0; public int GetByReferenceType(int e) => 0; public int GetFunctionPointerType(MethodSignature<int> s) => 0;
    public int GetGenericInstantiation(int g, System.Collections.Immutable.ImmutableArray<int> a) => 0; public int GetGenericMethodParameter(object c, int i) => 0;
    public int GetGenericTypeParameter(object c, int i) => 0; public int GetModifiedType(int m, int u, bool r) => 0; public int GetPinnedType(int e) => 0;
    public int GetPointerType(int e) => 0; public int GetPrimitiveType(PrimitiveTypeCode t) => 0; public int GetSZArrayType(int e) => 0;
    public int GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => 0; public int GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => 0;
    public int GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => 0;
}
