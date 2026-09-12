using System.Collections;
using BmrTranslation.Interop;
using BmrTranslation.Translation;
using HarmonyLib;

namespace BmrTranslation.Patching;

// Milestone 2: combat hints.
//
// Unlike the config layer there is no data to rewrite - hints are string literals compiled into 800+ call
// sites, recomputed every frame. So this is the one layer that needs IL patching.
//
// Patch points are the *aggregating* methods, not the individual Add calls:
//
//   BossModule.CalculateHintsForRaidMember  -> every player hint, from every component
//   BossModule.CalculateGlobalHints         -> every raidwide hint
//   ZoneModule.CalculateGlobalHints         -> duty-automation hints (virtual: every override patched)
//   BossModule.PrePullHints                 -> pre-pull notes (virtual: every override patched)
//
// BossComponent.TextHints.Add would look like the natural single funnel, but it is a one-line expression
// body that the JIT will inline into its callers, and a patch on an inlined callee never runs. The
// Calculate* methods loop over components and are not inlining candidates.
//
// Virtual members need every override patched individually - a patch on the base declaration does not
// intercept a subclass that overrides it. Hence the assembly scan.
public sealed class HintPatcher(BmrHandle bmr, HintTranslator translator) : IDisposable
{
    private const string HarmonyId = "de.elyfura.bmrtranslation.hints";

    private static HintTranslator? _active; // the patches are static, so they need a static way in
    private Harmony? _harmony;

    public int PatchedMethods { get; private set; }
    public List<string> Failures { get; } = [];

    public void Apply()
    {
        if (_harmony != null)
        {
            return;
        }

        _active = translator;
        _harmony = new Harmony(HarmonyId);

        var bossModule = bmr.Assembly.GetType("BossMod.BossModule");
        var zoneModule = bmr.Assembly.GetType("BossMod.ZoneModule");

        PatchList(bossModule, "CalculateHintsForRaidMember", nameof(Postfixes.TextHints));
        PatchList(bossModule, "CalculateGlobalHints", nameof(Postfixes.StringList));

        // virtuals: patch the declaration and every override in the assembly
        foreach (var method in WithOverrides(zoneModule, "CalculateGlobalHints"))
        {
            Patch(method, nameof(Postfixes.StringList));
        }
        foreach (var getter in PropertyWithOverrides(bossModule, "PrePullHints"))
        {
            Patch(getter, nameof(Postfixes.StringArray));
        }

        Service.Log.Information($"hint patches applied to {PatchedMethods} method(s), {Failures.Count} failure(s)");
    }

    private void PatchList(Type? type, string methodName, string postfix)
    {
        var method = type?.GetMethod(methodName, Reflect.AllInstance);
        if (method == null)
        {
            Failures.Add($"{type?.Name ?? "?"}.{methodName} not found");
            return;
        }
        Patch(method, postfix);
    }

    private IEnumerable<MethodInfo> WithOverrides(Type? baseType, string methodName)
    {
        if (baseType == null)
        {
            Failures.Add($"type for {methodName} not found");
            yield break;
        }
        foreach (var type in SubtypesOf(baseType))
        {
            var method = type.GetMethod(methodName, Reflect.AllInstance);
            // DeclaringType == type means this type declares its own body, base or override alike
            if (method != null && method.DeclaringType == type)
            {
                yield return method;
            }
        }
    }

    private IEnumerable<MethodInfo> PropertyWithOverrides(Type? baseType, string propertyName)
    {
        if (baseType == null)
        {
            Failures.Add($"type for {propertyName} not found");
            yield break;
        }
        foreach (var type in SubtypesOf(baseType))
        {
            var getter = type.GetProperty(propertyName, Reflect.AllInstance)?.GetGetMethod(nonPublic: true);
            if (getter != null && getter.DeclaringType == type)
            {
                yield return getter;
            }
        }
    }

    private IEnumerable<Type> SubtypesOf(Type baseType)
    {
        Type[] types;
        try
        {
            types = bmr.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // a single unloadable type must not cost us the rest of the assembly
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            Failures.Add($"{ex.Types.Length - types.Length} type(s) could not be loaded");
        }
        return types.Where(baseType.IsAssignableFrom);
    }

    private void Patch(MethodInfo target, string postfixName)
    {
        if (target.IsAbstract)
        {
            return; // nothing to intercept
        }
        try
        {
            _harmony!.Patch(target, postfix: new HarmonyMethod(typeof(Postfixes).GetMethod(postfixName)));
            ++PatchedMethods;
        }
        catch (Exception ex)
        {
            Failures.Add($"{target.DeclaringType?.Name}.{target.Name}: {ex.Message}");
            Service.Log.Warning(ex, $"could not patch {target.DeclaringType?.FullName}.{target.Name}");
        }
    }

    public void Dispose()
    {
        // BossMod keeps running after we unload, so the patches have to come off with us
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _active = null;
        PatchedMethods = 0;
        Failures.Clear();
    }

    // The return types (BossComponent.TextHints, GlobalHints) live in BossMod's assembly, which we cannot
    // reference at compile time - so the postfixes take IList, which every List<T> implements.
    public static class Postfixes
    {
        // List<(string, bool)>: translate Item1, leave the risk flag alone (BossModule.Draw reads it)
        public static void TextHints(IList __result)
        {
            var translator = _active;
            if (translator == null)
            {
                return;
            }
            for (var i = 0; i < __result.Count; ++i)
            {
                var boxed = __result[i];
                if (boxed == null)
                {
                    continue;
                }
                var field = boxed.GetType().GetField("Item1");
                if (field?.GetValue(boxed) is not string english)
                {
                    continue;
                }
                var translated = translator.Translate(english);
                if (translated != null)
                {
                    field.SetValue(boxed, translated);
                    __result[i] = boxed; // the tuple is a value type, so write the box back
                }
            }
        }

        // List<string>
        public static void StringList(IList __result)
        {
            var translator = _active;
            if (translator == null)
            {
                return;
            }
            for (var i = 0; i < __result.Count; ++i)
            {
                if (__result[i] is string english && translator.Translate(english) is { } translated)
                {
                    __result[i] = translated;
                }
            }
        }

        // string[] - PrePullHints often returns a shared static array, so never write into it in place
        public static void StringArray(ref string[] __result)
        {
            var translator = _active;
            if (translator == null || __result.Length == 0)
            {
                return;
            }
            string[]? copy = null;
            for (var i = 0; i < __result.Length; ++i)
            {
                if (translator.Translate(__result[i]) is { } translated)
                {
                    copy ??= (string[])__result.Clone();
                    copy[i] = translated;
                }
            }
            if (copy != null)
            {
                __result = copy;
            }
        }
    }
}
