using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

// Metadata-only inspection: never initializes game, Steam, or model classes.
if (args[0] == "--types")
{
    using var assembly = ModuleDefinition.ReadModule(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(assembly.Types
        .Where(type => args.Skip(2).Any(part => type.FullName.Contains(part, StringComparison.OrdinalIgnoreCase)))
        .Select(type => new { type.FullName, baseType = type.BaseType?.FullName }),
        new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args[0] == "--callers")
{
    using var assembly = ModuleDefinition.ReadModule(args[1]);
    IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(AllTypes));
    var callers = assembly.Types.SelectMany(AllTypes).SelectMany(type => type.Methods)
        .Where(method => method.HasBody)
        .SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference target && target.Name == args[2])
            .Select(instruction => new { caller = method.FullName, instruction = instruction.ToString() }));
    Console.WriteLine(JsonSerializer.Serialize(callers, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args[0] == "--compare")
{
    AssemblyComparison.Run(args[1], args[2], args.Skip(3).ToArray());
    return;
}
bool fullIl = args[0] == "--il";
if (fullIl) args = args.Skip(1).ToArray();
using var module = ModuleDefinition.ReadModule(args[0]);
var names = args.Skip(1).Where(n => !n.StartsWith("method:", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
var methods = args.Skip(1).Where(n => n.StartsWith("method:", StringComparison.Ordinal)).Select(n => n[7..]).ToHashSet(StringComparer.Ordinal);
IEnumerable<TypeDefinition> Descendants(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Descendants));
var selected = module.Types.Where(t => names.Contains(t.Name));
var types = (fullIl ? selected.SelectMany(Descendants) : selected).Select(t => new
{
    type = t.FullName,
    baseType = t.BaseType?.FullName,
    fields = t.Fields.Select(f => new { name = f.Name, type = f.FieldType.FullName, f.IsStatic }),
    methods = t.Methods.Where(m => methods.Count == 0 || methods.Contains(m.Name) || methods.Any(n => t.Name.Contains("<" + n + ">", StringComparison.Ordinal))).Select(m => new
    {
        name = m.Name,
        result = m.ReturnType.FullName,
        m.IsStatic,
        m.IsGenericInstance,
        genericParameters = m.GenericParameters.Select(p => p.Name),
        parameters = m.Parameters.Select(p => new { name = p.Name, type = p.ParameterType.FullName }),
        operations = m.HasBody ? m.Body.Instructions
            .Where(i => fullIl || i.OpCode.FlowControl == FlowControl.Call || i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Stfld)
            .Select(i => i.ToString()) : []
    })
});
Console.WriteLine(JsonSerializer.Serialize(types, new JsonSerializerOptions { WriteIndented = true }));
