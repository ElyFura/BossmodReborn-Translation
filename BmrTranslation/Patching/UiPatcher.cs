using System.Reflection.Emit;
using BmrTranslation.Interop;
using BmrTranslation.Translation;
using HarmonyLib;

namespace BmrTranslation.Patching;

// Milestone 3: the remaining UI text - window titles, buttons, checkboxes, table headers, everything
// BossMod passes to ImGui as a literal rather than storing in metadata.
//
// The transpiler replaces the `ldstr` operand outright, at patch time. It does not emit a lookup call:
// the literal is already available as an operand while transpiling, so resolving it then costs nothing at
// runtime and there is no per-frame work and no helper to misbehave. Reload simply unpatches and re-patches.
//
// The set of methods to patch comes from the translation file, not from scanning the assembly. Only methods
// that actually have a translation are touched - roughly 80 instead of several thousand - which keeps both
// the JIT cost and the blast radius small.
//
// Keys are "ui/<Type>::<Method>/<literal>". The literal belongs in the key because the same text can be a
// visible label in one method and an ImGui id or a string comparison in another; only the first may be
// translated. "::" separates type from method because a constructor's name is literally ".ctor".
public sealed class UiPatcher(BmrHandle bmr, TranslationTable table) : IDisposable
{
    private const string HarmonyId = "de.elyfura.bmrtranslation.ui";
    private const string KeyPrefix = "ui/";

    // static, because the transpiler must be a static method Harmony can call
    private static readonly Dictionary<string, string> _replacements = new(StringComparer.Ordinal);
    private static int _replaced;

    // object, not Harmony: Dalamud enumerates this assembly's types (Module.GetTypes) to find the
    // IDalamudPlugin before it constructs anything, and loading a type resolves its field types - a
    // Harmony-typed field here would demand 0Harmony before HarmonyBootstrap has had a chance to run.
    // See HintPatcher for the same reason stated at length.
    private object? _harmonyInstance;
    private Harmony Harmony => (Harmony)_harmonyInstance!;

    public int PatchedMethods { get; private set; }
    public int ReplacedLiterals => _replaced;
    public List<string> Failures { get; } = [];

    public void Apply()
    {
        if (_harmonyInstance != null)
        {
            return;
        }

        _replacements.Clear();
        _replaced = 0;

        // owner ("Type::Method") -> nothing; we only need the distinct set, the texts live in _replacements
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, entry) in table.WithPrefix(KeyPrefix))
        {
            if (entry.Text == null)
            {
                continue; // deliberately English
            }
            // the owner never contains '/', so the first slash after the prefix ends it - which keeps
            // literals containing slashes (URLs, paths) unambiguous
            var rest = key[KeyPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1)
            {
                Failures.Add($"malformed ui key: {key}");
                continue;
            }
            owners.Add(rest[..slash]);
            _replacements[rest] = entry.Text;
        }

        if (owners.Count == 0)
        {
            return;
        }

        _harmonyInstance = new Harmony(HarmonyId);
        var transpiler = new HarmonyMethod(typeof(UiPatcher).GetMethod(nameof(Transpile), Reflect.AllStatic));

        foreach (var owner in owners)
        {
            foreach (var method in Resolve(owner))
            {
                try
                {
                    Harmony.Patch(method, transpiler: transpiler);
                    ++PatchedMethods;
                }
                catch (Exception ex)
                {
                    Failures.Add($"{owner}: {ex.Message}");
                    Service.Log.Warning(ex, $"could not patch {owner}");
                }
            }
        }

        Service.Log.Information($"ui patches applied to {PatchedMethods} method(s), {_replaced} literal(s) replaced, {Failures.Count} failure(s)");
    }

    // all overloads of the named method: the literal only exists in one of them, and patching a sibling
    // that has no matching literal changes nothing.
    // MethodBase, not MethodInfo: constructors are ConstructorInfo, which is not a MethodInfo.
    private IEnumerable<MethodBase> Resolve(string owner)
    {
        var split = owner.LastIndexOf("::", StringComparison.Ordinal);
        if (split <= 0)
        {
            Failures.Add($"malformed ui owner: {owner}");
            yield break;
        }
        var typeName = owner[..split];
        var methodName = owner[(split + 2)..];

        var type = bmr.Assembly.GetType(typeName);
        if (type == null)
        {
            Failures.Add($"type not found: {typeName}");
            yield break;
        }

        var found = false;
        // constructors are named ".ctor" and are not returned by GetMethods
        if (methodName is ".ctor" or ".cctor")
        {
            foreach (var ctor in type.GetConstructors(Reflect.AllInstance))
            {
                found = true;
                yield return ctor;
            }
            if (!found)
            {
                Failures.Add($"no constructor on {typeName}");
            }
            yield break;
        }

        foreach (var method in type.GetMethods(Reflect.AllInstance | BindingFlags.Static))
        {
            if (method.Name == methodName && method.DeclaringType == type && !method.IsAbstract)
            {
                found = true;
                yield return method;
            }
        }
        if (!found)
        {
            Failures.Add($"method not found: {owner}");
        }
    }

    // Builds a list instead of yielding. An iterator would compile to a state machine whose fields are
    // typed CodeInstruction - and a field type is resolved when its declaring type loads, which happens
    // during the Module.GetTypes() Dalamud runs before constructing the plugin. That one hidden type was
    // enough to fail the entire plugin load; see HintPatcher for the full reasoning.
    //
    // Harmony materialises the sequence anyway, so nothing is lost by not streaming it.
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var owner = (__originalMethod.DeclaringType?.FullName ?? "?") + "::" + __originalMethod.Name;
        var result = new List<CodeInstruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldstr
                && instruction.operand is string literal
                && _replacements.TryGetValue(owner + "/" + literal, out var translated))
            {
                instruction.operand = translated;
                ++_replaced;
            }
            result.Add(instruction);
        }
        return result;
    }

    public void Dispose()
    {
        if (_harmonyInstance != null)
        {
            Harmony.UnpatchAll(HarmonyId);
        }
        _harmonyInstance = null;
        _replacements.Clear();
        _replaced = 0;
        PatchedMethods = 0;
        Failures.Clear();
    }
}
