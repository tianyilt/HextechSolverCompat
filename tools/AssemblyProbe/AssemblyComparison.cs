using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class AssemblyComparison
{
    // Compare metadata and normalized IL, never load either assembly. Compiler
    // generated nested state machines are included with their declaring type.
    internal static void Run(string officialPath, string sourcePath, string[] names)
    {
        using var official = ModuleDefinition.ReadModule(officialPath);
        using var source = ModuleDefinition.ReadModule(sourcePath);
        var left = Methods(official, names);
        var right = Methods(source, names);
        var entries = left.Keys.Union(right.Keys).Order(StringComparer.Ordinal).Select(key => new {
            method = key,
            official = left.GetValueOrDefault(key), source = right.GetValueOrDefault(key),
            equal = left.ContainsKey(key) && right.ContainsKey(key) && left[key] == right[key]
        }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new {
            officialPath, sourcePath,
            officialSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(officialPath))).ToLowerInvariant(),
            sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant(),
            selectedTypes = names, methodCount = entries.Length,
            equalCount = entries.Count(e => e.equal), differences = entries.Where(e => !e.equal)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, string> Methods(ModuleDefinition module, string[] names)
    {
        var missing = names.Where(name => !module.Types.Any(type => type.Name == name)).ToArray();
        if (missing.Length != 0)
            throw new ArgumentException($"Requested types absent from {module.Name}: {string.Join(", ", missing)}");
        Dictionary<string, List<string>> groups = new(StringComparer.Ordinal);
        foreach (var type in module.Types.Where(t => names.Length == 0 || names.Contains(t.Name))) Visit(type);
        // Obfuscated binaries may contain multiple methods with an identical
        // metadata signature. Compare every body as a multiset; never drop one.
        return groups.ToDictionary(pair => pair.Key,
            pair => string.Join('|', pair.Value.Order(StringComparer.Ordinal)), StringComparer.Ordinal);
        void Visit(TypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                if (!groups.TryGetValue(method.FullName, out var bodies))
                    groups.Add(method.FullName, bodies = []);
                bodies.Add(Hash(method));
            }
            foreach (var nested in type.NestedTypes) Visit(nested);
        }
    }

    private static string Hash(MethodDefinition method)
    {
        StringBuilder text = new();
        text.AppendLine(method.Attributes.ToString());
        if (method.HasBody)
        {
            var body = method.Body;
            var instructions = body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
            int Index(Instruction? target)
            {
                if (target is null) return -1;
                while (target.OpCode == OpCodes.Nop && target.Next is not null) target = target.Next;
                return Array.IndexOf(instructions, target);
            }
            foreach (var variable in body.Variables) text.AppendLine("local:" + variable.VariableType.FullName);
            foreach (var instruction in instructions)
            {
                string operand = instruction.Operand switch {
                    null => "", Instruction target => "label:" + Index(target),
                    Instruction[] targets => "labels:" + string.Join(',', targets.Select(Index)),
                    MethodReference reference => reference.FullName,
                    FieldReference reference => reference.FullName,
                    TypeReference reference => reference.FullName,
                    VariableDefinition variable => "local:" + variable.Index,
                    ParameterDefinition parameter => "arg:" + parameter.Index,
                    string value => JsonSerializer.Serialize(value),
                    _ => Convert.ToString(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture)!
                };
                text.AppendLine(instruction.OpCode.Name + " " + operand);
            }
            foreach (var handler in body.ExceptionHandlers)
                text.AppendLine($"{handler.HandlerType}:{handler.CatchType?.FullName}:{Index(handler.TryStart)}:{Index(handler.TryEnd)}:{Index(handler.HandlerStart)}:{Index(handler.HandlerEnd)}:{Index(handler.FilterStart)}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
