using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;

if (args.Length != 2)
    throw new ArgumentException("Usage: PatchQuic <input System.Net.Quic.dll> <output assembly>");

using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var module = assembly.MainModule;
var version = assembly.CustomAttributes.Single(a =>
    a.AttributeType.Name == "AssemblyInformationalVersionAttribute").ConstructorArguments[0].Value.ToString()!;
if (!version.StartsWith("10.0.12+", StringComparison.Ordinal) &&
    !version.StartsWith("10.0.13+", StringComparison.Ordinal) &&
    !version.StartsWith("11.0.0-rc.2.26504.105+", StringComparison.Ordinal))
    throw new InvalidOperationException($"Unvalidated QUIC runtime: {version}. Expected 10.0.12, 10.0.13 or 11.0.0-rc.2.26504.105.");

var api = module.Types.Single(t => t.FullName == "System.Net.Quic.MsQuicApi");
var initializer = api.Methods.Single(m => m.Name == ".cctor");
var loads = FindNativeCalls("TryLoad");
var exports = FindNativeCalls("GetExport");
if (loads.Length != 3 || exports.Length != 2 ||
    loads.Any(i => ((MethodReference)i.Operand).Parameters.Count != 4) ||
    !exports.Select(i => i.Previous.Operand as string).SequenceEqual(new[] { "MsQuicOpenVersion", "MsQuicClose" }))
    throw new InvalidOperationException($"Unexpected QUIC loader IL ({loads.Length} loads, {exports.Length} exports). Refusing to patch.");

// Rewrite only a build-local copy of the implementation assembly. Preserve all
// platform/version/TLS checks, API table handling and certificate validation.
module.Architecture = TargetArchitecture.I386;
module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
module.Attributes |= ModuleAttributes.ILOnly;
var tryLoad = AddReplacement("StaticMsQuicTryLoad", (MethodReference)loads[0].Operand);
var il = tryLoad.Body.GetILProcessor();
il.Emit(OpCodes.Ldarg_3);
il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Conv_I);
il.Emit(OpCodes.Stind_I); // A nonzero sentinel, never passed to the real NativeLibrary API.
il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Ret);

var nativeModule = new ModuleReference("__Internal");
module.ModuleReferences.Add(nativeModule);
var getOpen = AddNative("StaticMsQuicGetOpen");
var getClose = AddNative("StaticMsQuicGetClose");
var getExport = AddReplacement("StaticMsQuicGetExport", (MethodReference)exports[0].Operand);
var equality = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String)
{
    HasThis = false,
    Parameters = { new ParameterDefinition(module.TypeSystem.String), new ParameterDefinition(module.TypeSystem.String) }
};
il = getExport.Body.GetILProcessor();
var close = Instruction.Create(OpCodes.Call, getClose);
il.Emit(OpCodes.Ldarg_1);
il.Emit(OpCodes.Ldstr, "MsQuicOpenVersion");
il.Emit(OpCodes.Call, equality);
il.Emit(OpCodes.Brfalse, close);
il.Emit(OpCodes.Call, getOpen);
il.Emit(OpCodes.Ret);
il.Append(close);
il.Emit(OpCodes.Ret);
foreach (var call in loads) call.Operand = tryLoad;
foreach (var call in exports) call.Operand = getExport;

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);
Console.WriteLine($"Patched QUIC loader {version}; source SHA256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])))}");

Instruction[] FindNativeCalls(string name) => initializer.Body.Instructions.Where(i =>
    i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
    m.DeclaringType.FullName == "System.Runtime.InteropServices.NativeLibrary" && m.Name == name).ToArray();

MethodDefinition AddReplacement(string name, MethodReference original)
{
    var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static, original.ReturnType);
    foreach (var parameter in original.Parameters)
        method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
    api.Methods.Add(method);
    return method;
}

MethodDefinition AddNative(string name)
{
    // The two tiny bridge functions are already present in each supplied archive.
    var method = new MethodDefinition(name,
        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.IntPtr)
    {
        PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, name, nativeModule),
        ImplAttributes = MethodImplAttributes.PreserveSig
    };
    api.Methods.Add(method);
    return method;
}
