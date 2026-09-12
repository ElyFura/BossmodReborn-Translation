using System.IO;
using System.Runtime.Loader;

namespace BmrTranslation.Interop;

// Puts 0Harmony into the default AssemblyLoadContext before anything in this plugin touches a Harmony
// type.
//
// Dalamud loads every plugin - and everything next to it - into a *collectible* ALC, so the plugin can be
// disabled and reloaded without restarting the game. Harmony cannot live there. To build a patch it has
// MonoMod emit a proxy type derived from System.Reflection.Emit.ILGenerator, and that proxy has to resolve
// HarmonyLib's own ILGeneratorShim by name; the runtime refuses to resolve a type name to a collectible
// assembly, so every single Patch() call dies with
//
//     FileLoadException: Could not load file or assembly '0Harmony'
//       ---> NotSupportedException: Resolving to a collectible assembly is not supported.
//
// This is not a version problem: 2.3.3 through 2.4.2 fail identically, as does every MONOMOD_DMDType
// backend. The assembly itself has to be non-collectible.
//
// So 0Harmony is not shipped beside the plugin at all (ExcludeAssets="runtime"); it is embedded and loaded
// from the stream into the default context here. Nothing in the plugin directory can then satisfy the
// reference, the runtime falls through to the default ALC, and binds to this copy. The patch methods,
// transpilers and BossMod itself stay collectible - only Harmony moves, which is the minimum that works.
//
// The cost is that 0Harmony stays loaded after the plugin is unloaded. One assembly, and unavoidable:
// non-collectible is the requirement.
public static class HarmonyBootstrap
{
    private const string AssemblyName = "0Harmony";
    private const string ResourceName = "BmrTranslation.0Harmony.dll";

    private static bool _attempted;
    private static string? _error;

    // true once Harmony is usable. Callers must check this *before* naming a Harmony type, which is why
    // this class does not reference one.
    public static bool Ready { get; private set; }

    public static string? Error => _error;

    public static bool Ensure()
    {
        if (_attempted)
        {
            return Ready;
        }
        _attempted = true;

        try
        {
            // a reload lands here a second time, with the previous copy still in the default context -
            // and LoadFromStream does not deduplicate, so loading again would produce a second 0Harmony
            // of the same identity. Reuse whatever is already there.
            var existing = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Ready = true;
                Service.Log.Debug($"{AssemblyName} already present in the default load context ({existing.GetName().Version})");
                return true;
            }

            using var stream = typeof(HarmonyBootstrap).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException($"embedded resource {ResourceName} is missing");
            var loaded = AssemblyLoadContext.Default.LoadFromStream(stream);
            Ready = true;
            Service.Log.Debug($"{AssemblyName} {loaded.GetName().Version} loaded into the default (non-collectible) load context");
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Service.Log.Error(ex, "could not load Harmony - combat hints and window text stay English");
            return false;
        }
    }
}
