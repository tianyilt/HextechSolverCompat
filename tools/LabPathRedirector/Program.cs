// Test clone only: relocate managed Godot user:// file I/O into the workspace.
// Never touch the installed game's files or alter any combat implementation.
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Expected cloned GodotSharp.dll and workspace data directory");
string assemblyPath = Path.GetFullPath(args[0]);
string dataPath = Path.GetFullPath(args[1]);
string lab = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.local/headless-instances/hextech"));
if (!assemblyPath.StartsWith(lab + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
    dataPath != Path.Combine(lab, "user-data"))
    throw new InvalidOperationException("Only the project's isolated clone and user-data directory can be patched");

string backup = assemblyPath + ".lab-original";
if (!File.Exists(backup)) File.Copy(assemblyPath, backup);
using var assembly = AssemblyDefinition.ReadAssembly(backup);
var module = assembly.MainModule;
var helper = new TypeDefinition("HextechCompatLab", "IsolatedPaths",
    TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
module.Types.Add(helper);
var redirect = new MethodDefinition("Redirect", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
redirect.Parameters.Add(new ParameterDefinition("path", ParameterAttributes.None, module.TypeSystem.String));
helper.Methods.Add(redirect);
var il = redirect.Body.GetILProcessor();
var unchanged = Instruction.Create(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Brfalse, unchanged);
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ldstr, "user://");
il.Emit(OpCodes.Ldc_I4, (int)StringComparison.Ordinal);
il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(string).GetMethod("StartsWith", [typeof(string), typeof(StringComparison)])!));
il.Emit(OpCodes.Brfalse, unchanged);
il.Emit(OpCodes.Ldstr, dataPath + "/");
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ldc_I4_7);
il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(string).GetMethod("Substring", [typeof(int)])!));
il.Emit(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!));
il.Emit(OpCodes.Ret);
il.Append(unchanged);
il.Emit(OpCodes.Ret);

var os = module.Types.Single(t => t.FullName == "Godot.OS");
var userDir = os.Methods.Single(m => m.Name == "GetUserDataDir" && m.Parameters.Count == 0);
userDir.Body = new MethodBody(userDir);
userDir.Body.GetILProcessor().Emit(OpCodes.Ldstr, dataPath);
userDir.Body.GetILProcessor().Emit(OpCodes.Ret);
List<string> patched = [userDir.FullName];
foreach (var type in module.Types.Where(t => t.FullName is "Godot.FileAccess" or "Godot.DirAccess" or "Godot.ProjectSettings"))
{
    foreach (var method in type.Methods.Where(m => m.HasBody))
    {
        // Never rewrite content buffers (StoreString), passphrases or extensions.
        var parameters = method.Parameters.Where(p => p.ParameterType.FullName == "System.String" &&
            (p.Name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
             p.Name is "from" or "to" or "dir" or "file")).ToArray();
        if (parameters.Length == 0) continue;
        var processor = method.Body.GetILProcessor();
        var first = method.Body.Instructions.First();
        foreach (var parameter in parameters)
        {
            processor.InsertBefore(first, Instruction.Create(OpCodes.Ldarg, parameter));
            processor.InsertBefore(first, Instruction.Create(OpCodes.Call, redirect));
            processor.InsertBefore(first, Instruction.Create(OpCodes.Starg, parameter));
        }
        patched.Add(method.FullName);
    }
}
assembly.Write(assemblyPath);
Directory.CreateDirectory(dataPath);
File.WriteAllText(Path.Combine(lab, "path-redirect.json"), System.Text.Json.JsonSerializer.Serialize(
    new { dataPath, assemblyPath, patched }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Relocated {patched.Count} managed path entry points in the isolated clone only.");
