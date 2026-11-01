using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IEngine.Execution;

static class IlFieldScanner
{
    static readonly OpCode[] oneByte = new OpCode[256];
    static readonly OpCode[] twoByte = new OpCode[256];

    static IlFieldScanner()
    {
        foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null);
            ushort v = (ushort)op.Value;
            if (op.Size == 1) { oneByte[v] = op; } else { twoByte[v & 0xFF] = op; }
        }
    }

    public readonly record struct Hit(MethodBase Method, int Stores, int Addresses);

    public static List<Hit> ScanAssembly(Assembly assembly, FieldInfo field, List<string> problems) =>
        Scan(assembly, field, IsFieldWrite, problems);

    public static List<Hit> ScanCalls(Assembly assembly, MethodBase method, List<string> problems) =>
        Scan(assembly, method, IsCall, problems);

    static bool IsFieldWrite(OpCode op, out bool address)
    {
        address = op == OpCodes.Ldflda;
        return op == OpCodes.Stfld || address;
    }

    static bool IsCall(OpCode op, out bool address)
    {
        address = false;
        return op == OpCodes.Call || op == OpCodes.Callvirt;
    }

    delegate bool OpFilter(OpCode op, out bool address);

    static List<Hit> Scan(Assembly assembly, MemberInfo member, OpFilter filter, List<string> problems)
    {
        var hits = new List<Hit>();
        if (!string.IsNullOrEmpty(assembly.Location) && assembly == member.Module.Assembly)
        {
            ScanFile(assembly, member, filter, hits, problems);
        }
        else
        {
            ScanReflection(assembly, member, filter, hits, problems);
        }
        return hits;
    }

    static void ScanFile(Assembly assembly, MemberInfo member, OpFilter filter, List<Hit> hits, List<string> problems)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        int token = member.MetadataToken;
        Module module = assembly.ManifestModule;
        foreach (MethodDefinitionHandle h in md.MethodDefinitions)
        {
            MethodDefinition def = md.GetMethodDefinition(h);
            if (def.RelativeVirtualAddress == 0) { continue; }
            byte[] il = pe.GetMethodBody(def.RelativeVirtualAddress).GetILBytes();
            (int stores, int addresses) = Count(il, filter, t => t == token);
            if (stores + addresses == 0) { continue; }
            int methodToken = MetadataTokens.GetToken(h);
            try { hits.Add(new Hit(module.ResolveMethod(methodToken), stores, addresses)); }
            catch (Exception e) { problems.Add($"{assembly.GetName().Name} 0x{methodToken:X8}: {e.Message}"); }
        }
    }

    static void ScanReflection(Assembly assembly, MemberInfo member, OpFilter filter, List<Hit> hits, List<string> problems)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in types)
        {
            foreach (MethodBase m in Methods(type, all))
            {
                byte[] il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                catch { continue; }
                if (il == null) { continue; }
                Module module = m.Module;
                (int stores, int addresses) = Count(il, filter, t => Resolves(module, t, member));
                if (stores + addresses > 0) { hits.Add(new Hit(m, stores, addresses)); }
            }
        }
    }

    static IEnumerable<MethodBase> Methods(Type type, BindingFlags flags)
    {
        foreach (MethodInfo m in type.GetMethods(flags)) { yield return m; }
        foreach (ConstructorInfo c in type.GetConstructors(flags)) { yield return c; }
        if (type.TypeInitializer is { } cctor) { yield return cctor; }
    }

    static bool Resolves(Module module, int token, MemberInfo member)
    {
        int table = token >>> 24;
        try
        {
            return member switch
            {
                FieldInfo f => (table == 0x04 || table == 0x0A) && module.ResolveField(token) == f,
                MethodBase m => (table == 0x06 || table == 0x0A || table == 0x2B) && module.ResolveMethod(token) == m,
                _ => false
            };
        }
        catch { return false; }
    }

    static (int Stores, int Addresses) Count(ReadOnlySpan<byte> il, OpFilter filter, Func<int, bool> isMember)
    {
        int stores = 0, addresses = 0;
        int i = 0;
        while (i < il.Length)
        {
            OpCode op;
            byte b = il[i++];
            if (b == 0xFE) { op = twoByte[il[i++]]; } else { op = oneByte[b]; }
            int operand = OperandSize(op, il, i);
            if (filter(op, out bool address) && isMember(BitConverter.ToInt32(il.Slice(i, 4))))
            {
                if (address) { addresses++; } else { stores++; }
            }
            i += operand;
        }
        return (stores, addresses);
    }

    static int OperandSize(OpCode op, ReadOnlySpan<byte> il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il.Slice(at, 4)) * 4,
        _ => 4
    };
}
