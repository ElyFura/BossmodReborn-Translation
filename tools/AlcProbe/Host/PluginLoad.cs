using System.Reflection;
using System.Runtime.Loader;

// Before Dalamud constructs anything, it calls Module.GetTypes() on the plugin assembly to find the
// IDalamudPlugin implementation. That resolves every type in the assembly - and loading a type resolves
// its *field* types. So a field typed Harmony, or a compiler-generated iterator state machine holding
// CodeInstruction, demands 0Harmony at a moment when it is still only an embedded resource, and the whole
// plugin fails to load with ReflectionTypeLoadException.
//
// The fake plugin the rest of this probe uses cannot catch that: it names Harmony in the same places, but
// it is loaded by a host that asks for one type, not for all of them. So this check runs against the real
// BmrTranslation.dll, in a context where 0Harmony genuinely is not findable.
internal static class PluginLoad
{
    public static bool Check(string pluginDll, string dalamudDir)
    {
        if (!File.Exists(pluginDll))
        {
            Console.WriteLine($"  SKIP  plugin assembly not built: {pluginDll}");
            return true;
        }
        if (!Directory.Exists(dalamudDir))
        {
            Console.WriteLine($"  SKIP  Dalamud not found: {dalamudDir}");
            return true;
        }

        var directory = Path.GetDirectoryName(pluginDll)!;
        if (File.Exists(Path.Combine(directory, "0Harmony.dll")))
        {
            Console.WriteLine("  FAIL  0Harmony.dll sits next to the plugin; it must be embedded, not shipped");
            return false;
        }

        // Dalamud's own assemblies resolve, 0Harmony deliberately does not - exactly the state the plugin
        // is enumerated in
        var alc = new PluginContext(directory, dalamudDir);
        var assembly = alc.LoadFromAssemblyPath(pluginDll);

        try
        {
            var types = assembly.GetTypes();
            Console.WriteLine($"  ok    {Path.GetFileName(pluginDll)}: {types.Length} types enumerate with 0Harmony absent");
            return true;
        }
        catch (ReflectionTypeLoadException ex)
        {
            var missing = ex.Types.Length - ex.Types.Count(t => t != null);
            Console.WriteLine($"  FAIL  {Path.GetFileName(pluginDll)}: {missing} of {ex.Types.Length} types will not load - Dalamud rejects the plugin");
            foreach (var message in ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct())
            {
                Console.WriteLine("          " + message);
            }
            Console.WriteLine("          a Harmony type in a field, or in a compiler-generated iterator, is the usual cause");
            return false;
        }
    }

    private sealed class PluginContext(string pluginDirectory, string dalamudDirectory)
        : AssemblyLoadContext("plugin-load-check", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            foreach (var directory in new[] { pluginDirectory, dalamudDirectory })
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }
            return null;
        }
    }
}
