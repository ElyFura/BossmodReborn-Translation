using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ShapeCheck;

// Extracts the UI string literals for Milestone 3, straight from the installed assembly's IL.
//
// No source tree and no regexes: a method's literals are its `ldstr` operands, and whether a method draws
// UI is answered by whether it calls into ImGui. That makes the candidate set exact rather than guessed,
// and it is automatically right for the version actually installed.
//
// Keys are location-derived again - "ui/<Type>::<Method>/<literal>". Keying by text alone would be wrong
// here: the same literal can be a label in one method and an ImGui id or a comparison in another, and only
// the first may be translated. Including the literal in the key (rather than an index) means adding a
// literal to a method does not shift every other key in it.
public static class UiExtraction
{
    // ImGui wrappers BossMod draws through; a method calling any of these is UI code
    private static readonly string[] UiNamespaces =
    [
        "Dalamud.Bindings.ImGui",
        "Dalamud.Interface"
    ];

    private static readonly string[] UiTypeNames =
    [
        "BossMod.UITree", "BossMod.UITabs", "BossMod.UIMisc", "BossMod.UICombo",
        "BossMod.UIWindow", "BossMod.UISimpleWindow", "BossMod.UIText", "BossMod.UIPlot"
    ];

    public static int Run(string assemblyPath, TextWriter output, Report report, bool includeIds)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            // we only read instruction operands, never resolve types, so a missing Dalamud is fine
            AssemblyResolver = new NullResolver()
        });

        var literals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var uiMethods = 0;
        var skippedIds = 0;
        var skippedNoLetter = 0;

        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                var body = method.Body;
                var drawsUi = false;
                var found = new List<string>();

                foreach (var instruction in body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string literal)
                    {
                        found.Add(literal);
                    }
                    else if (!drawsUi && IsUiCall(instruction))
                    {
                        drawsUi = true;
                    }
                }

                if (!drawsUi || found.Count == 0)
                {
                    continue;
                }
                ++uiMethods;

                var owner = Describe(method);
                foreach (var literal in found)
                {
                    if (!literal.Any(char.IsLetter))
                    {
                        ++skippedNoLetter; // separators, punctuation, format-only fragments
                        continue;
                    }
                    if (!includeIds && literal.StartsWith("##", StringComparison.Ordinal))
                    {
                        // "##id" is an ImGui identity with no visible text; "label###id" is a label and stays
                        ++skippedIds;
                        continue;
                    }
                    literals[$"ui/{owner}/{literal}"] = literal;
                }
            }
        }

        report.Section("UI literal extraction");
        report.Info($"{uiMethods} method(s) call ImGui and contain literals");
        report.Check(literals.Count > 0, $"{literals.Count} distinct UI literal(s)");
        report.Info($"skipped: {skippedIds} pure ImGui id(s), {skippedNoLetter} literal(s) without a letter");

        output.Write(JsonSerializer.Serialize(literals, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        output.WriteLine();
        return 0;
    }

    private static bool IsUiCall(Instruction instruction)
    {
        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
        {
            return false;
        }
        if (instruction.Operand is not MethodReference target)
        {
            return false;
        }
        var declaring = target.DeclaringType;
        var ns = declaring.Namespace ?? "";
        var full = declaring.FullName;
        return UiNamespaces.Any(u => ns.StartsWith(u, StringComparison.Ordinal))
            || UiTypeNames.Any(u => full.StartsWith(u, StringComparison.Ordinal));
    }

    // "Namespace.Type::Method" - nested types keep their '/' from Cecil, normalised to '+' so
    // Assembly.GetType can resolve them. The "::" separator matters: a '.' would be ambiguous for
    // constructors, whose method name is literally ".ctor".
    private static string Describe(MethodDefinition method)
        => method.DeclaringType.FullName.Replace('/', '+') + "::" + method.Name;

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in Nested(type))
            {
                yield return nested;
            }
        }

        static IEnumerable<TypeDefinition> Nested(TypeDefinition type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deeper in Nested(nested))
                {
                    yield return deeper;
                }
            }
        }
    }

    // BossMod references Dalamud, FFXIVClientStructs and friends; none of them need resolving to read IL
    private sealed class NullResolver : IAssemblyResolver
    {
        public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;
        public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
        public void Dispose() { }
    }
}
