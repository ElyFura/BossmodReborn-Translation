using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

namespace ProbePlugin;

// The probe body. This type lives in the *plugin* assembly, which the host loads into a collectible
// AssemblyLoadContext together with 0Harmony - exactly how Dalamud loads a plugin and its dependencies.
// Running the Harmony calls from here rather than from the host is the whole point: the earlier version
// of this probe ran them from a non-collectible context and therefore proved nothing about the real one.
public static class Probe
{
    // bootstrap:false skips pushing 0Harmony into the default context, so the probe can show the failure
    // the bootstrap exists to prevent rather than assert it from memory
    public static int Run(Assembly asm, bool bootstrap)
    {
        if (bootstrap && !Bootstrap.Ensure())
        {
            Console.WriteLine("bootstrap FAILED: " + Bootstrap.Error);
            return 1;
        }
        return Execute(asm);
    }

    // never inlined: naming a Harmony type is what forces 0Harmony to resolve, and that must not be
    // hoisted above the Ensure() call that makes it resolve to the default context
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Execute(Assembly asm)
    {
        Console.WriteLine($"target assembly: {asm.GetName().Name}, {Describe(asm)}");
        Console.WriteLine($"plugin body     = {Describe(typeof(Probe).Assembly)}");
        Console.WriteLine($"harmony         = {typeof(Harmony).Assembly.GetName().Version}, {Describe(typeof(Harmony).Assembly)}");
        Console.WriteLine($"runtime         = {Environment.Version}");

        var failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}: {detail}");
            if (!ok) { ++failures; }
        }

        var harmony = new Harmony("bmrtl.probe");

        var moduleType = asm.GetType("ProbeTarget.BossModule")!;
        var derivedType = asm.GetType("ProbeTarget.DerivedModule")!;
        var zoneType = asm.GetType("ProbeTarget.ZoneModule")!;
        var derivedZoneType = asm.GetType("ProbeTarget.DerivedZone")!;

        Console.WriteLine("\n-- 1. postfix on a public method returning a List subclass --");
        harmony.Patch(
            moduleType.GetMethod("CalculateHintsForRaidMember")!,
            postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.TextHints))));

        var module = Activator.CreateInstance(moduleType)!;
        var hints = (IList)moduleType.GetMethod("CalculateHintsForRaidMember")!.Invoke(module, [0, "Alice"])!;
        var texts = hints.Cast<object>().Select(t => t.GetType().GetField("Item1")!.GetValue(t) as string).ToList();
        Check("fixed hint translated", texts[0] == "[DE] Stay together!", texts[0] ?? "null");
        Check("interpolated hint translated", texts[1] == "[DE] Stack with Alice", texts[1] ?? "null");

        Console.WriteLine("\n-- 2. postfix on a method whose JIT already ran (patch after first call) --");
        var already = (IList)moduleType.GetMethod("CalculateGlobalHints")!.Invoke(module, ["Alice"])!;
        Check("before patch", (string?)already[0] == "Prepare for raidwide", (string?)already[0] ?? "null");
        harmony.Patch(
            moduleType.GetMethod("CalculateGlobalHints")!,
            postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.GlobalHints))));
        var after = (IList)moduleType.GetMethod("CalculateGlobalHints")!.Invoke(module, ["Alice"])!;
        Check("after patch on an already-JITted method", (string?)after[0] == "[DE] Prepare for raidwide", (string?)after[0] ?? "null");

        Console.WriteLine("\n-- 3. postfix on a virtual property override (PrePullHints, 59 real overrides) --");
        var getter = derivedType.GetProperty("PrePullHints")!.GetGetMethod()!;
        Check("override is a distinct method", getter.DeclaringType == derivedType, getter.DeclaringType?.Name ?? "null");
        harmony.Patch(getter, postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.StringArray))));
        var derived = Activator.CreateInstance(derivedType)!;
        var prePull = (string[])derivedType.GetProperty("PrePullHints")!.GetValue(derived)!;
        Check("override translated", prePull[0] == "[DE] Assign towers before pull", prePull[0]);

        Console.WriteLine("\n-- 4. postfix on a virtual method override, discovered by scanning the assembly --");
        var overrides = asm.GetTypes()
            .Where(t => !t.IsAbstract && zoneType.IsAssignableFrom(t))
            .Select(t => t.GetMethod("CalculateGlobalHints"))
            .Where(m => m != null && m.DeclaringType != zoneType)
            .ToList();
        Check("override discovery", overrides.Count == 1, $"{overrides.Count} found ({string.Join(", ", overrides.Select(m => m!.DeclaringType!.Name))})");
        foreach (var m in overrides)
        {
            harmony.Patch(m!, postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.StringList))));
        }
        var zone = Activator.CreateInstance(derivedZoneType)!;
        var zoneHints = (List<string>)derivedZoneType.GetMethod("CalculateGlobalHints")!.Invoke(zone, null)!;
        Check("override translated", zoneHints[0] == "[DE] Head to the next objective", zoneHints[0]);

        Console.WriteLine("\n-- 5. transpiler rewriting ldstr in a UI method (Milestone 3) --");
        var windowType = asm.GetType("ProbeTarget.FakeWindow")!;
        var drawMethod = windowType.GetMethod("Draw")!;
        UiPatches.Method = "ProbeTarget.FakeWindow::Draw";
        UiPatches.Table[UiPatches.Method + "/Enable radar"] = "Radar aktivieren";
        UiPatches.Table[UiPatches.Method + "/Supported fights"] = "Unterstützte Kämpfe";
        UiPatches.Table[UiPatches.Method + "/Boss: {0}"] = "Boss: {0} (DE)";
        UiPatches.Table["ProbeTarget.SomeOtherType::Draw/##ConfigSearch"] = "must not apply";
        harmony.Patch(drawMethod, transpiler: new HarmonyMethod(typeof(UiPatches).GetMethod(nameof(UiPatches.Transpile))));

        var window = Activator.CreateInstance(windowType)!;
        var drawn = (List<string>)drawMethod.Invoke(window, null)!;
        Check("Harmony supplies __originalMethod", UiPatches.SeenOriginal == UiPatches.Method, UiPatches.SeenOriginal ?? "null");
        Check("literal replaced in IL", drawn[0] == "Radar aktivieren", drawn[0]);
        Check("id untouched (entry belongs to another method)", drawn[1] == "##ConfigSearch", drawn[1]);
        Check("second literal replaced", drawn[2] == "Unterstützte Kämpfe", drawn[2]);
        Check("format string still formats after replacement", drawn[3] == "Boss: Zoraal Ja (DE)", drawn[3]);
        Check("exactly the 3 matching literals replaced", UiPatches.Lookups == 3, UiPatches.Lookups.ToString());

        Console.WriteLine("\n-- 7. a literal on an abstract generic base (DuelFarm<Duel>, EurekaZone<NM>) --");
        // In game this failed with "Specified method is not supported": UiPatcher handed Harmony the open
        // generic definition, which has no code. The fix resolves each closed instantiation instead, so
        // both halves are worth proving - that the open form is genuinely unpatchable, and that the closed
        // one works and reaches every subclass sharing it.
        var openZone = asm.GetType("ProbeTarget.Zone`1")!;
        var openMethod = openZone.GetMethod("DrawExtra")!;
        var refused = false;
        try
        {
            harmony.Patch(openMethod, transpiler: new HarmonyMethod(typeof(UiPatches).GetMethod(nameof(UiPatches.Transpile))));
        }
        catch (Exception)
        {
            refused = true;
        }
        Check("open generic definition is refused, as in game", refused, refused ? "refused" : "unexpectedly accepted");

        var closedForms = asm.GetTypes()
            .Where(t => !t.IsAbstract && !t.ContainsGenericParameters)
            .Select(t => t.BaseType)
            .Where(b => b is { IsGenericType: true } && b.GetGenericTypeDefinition() == openZone)
            .Distinct()
            .ToList();
        Check("closed instantiations discovered", closedForms.Count == 2, $"{closedForms.Count} ({string.Join(", ", closedForms.Select(t => t!.ToString()))})");

        // the key names the open definition, exactly as the IL extractor writes it
        UiPatches.Table["ProbeTarget.Zone`1::DrawExtra/Max mobs to pull"] = "Höchstzahl gepullter Gegner";
        foreach (var closed in closedForms)
        {
            harmony.Patch(closed!.GetMethod("DrawExtra")!, transpiler: new HarmonyMethod(typeof(UiPatches).GetMethod(nameof(UiPatches.Transpile))));
        }

        foreach (var name in new[] { "BozjaZone", "EurekaZone", "ZadnorZone" })
        {
            var instance = Activator.CreateInstance(asm.GetType("ProbeTarget." + name)!)!;
            var extra = (List<string>)instance.GetType().GetMethod("DrawExtra")!.Invoke(instance, null)!;
            Check($"{name} translated through its closed base", extra[0] == "Höchstzahl gepullter Gegner", extra[0]);
        }

        Console.WriteLine("\n-- 6. unpatch restores the original (needed for /bmrtl off and unload) --");
        harmony.UnpatchAll("bmrtl.probe");
        var restored = (IList)moduleType.GetMethod("CalculateGlobalHints")!.Invoke(module, ["Alice"])!;
        Check("postfix removed", (string?)restored[0] == "Prepare for raidwide", (string?)restored[0] ?? "null");
        var redrawn = (List<string>)drawMethod.Invoke(window, null)!;
        Check("transpiler removed", redrawn[0] == "Enable radar", redrawn[0]);

        return failures;
    }

    private static string Describe(Assembly asm)
    {
        var alc = AssemblyLoadContext.GetLoadContext(asm);
        return alc == null ? "?" : $"ALC={alc.Name} collectible={alc.IsCollectible}";
    }
}

static class UiPatches
{
    public static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal);
    public static string Method = "";
    public static int Lookups;
    public static string? SeenOriginal;

    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        // A closed generic type reports an assembly-qualified name (Zone`1[[System.Int32, System...]]),
        // which no key could ever spell. Keys come from IL, where the literal sits in the open definition,
        // so the owner is normalised back to that definition before the lookup.
        var declaring = __originalMethod.DeclaringType;
        if (declaring is { IsGenericType: true })
        {
            declaring = declaring.GetGenericTypeDefinition();
        }
        var owner = (declaring?.FullName ?? "?") + "::" + __originalMethod.Name;
        SeenOriginal = owner;
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string literal
                && Table.TryGetValue(owner + "/" + literal, out var translated))
            {
                ++Lookups;
                instruction.operand = translated;
            }
            yield return instruction;
        }
    }
}

static class Patches
{
    public static void TextHints(IList __result)
    {
        for (var i = 0; i < __result.Count; ++i)
        {
            var boxed = __result[i]!;
            var f = boxed.GetType().GetField("Item1")!;
            f.SetValue(boxed, "[DE] " + f.GetValue(boxed));
            __result[i] = boxed;
        }
    }

    public static void GlobalHints(IList __result) => Prefix(__result);
    public static void StringList(IList __result) => Prefix(__result);

    public static void StringArray(ref string[] __result)
    {
        for (var i = 0; i < __result.Length; ++i)
        {
            __result[i] = "[DE] " + __result[i];
        }
    }

    private static void Prefix(IList list)
    {
        for (var i = 0; i < list.Count; ++i)
        {
            list[i] = "[DE] " + (string)list[i]!;
        }
    }
}
