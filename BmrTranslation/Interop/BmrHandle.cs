using System.Collections;

namespace BmrTranslation.Interop;

// resolves the loaded BossModReborn assembly and the handful of types we reach into.
// BossMod may load before or after us, so this is retried until it succeeds.
public sealed class BmrHandle
{
    public const string AssemblyName = "BossModReborn";

    public Assembly Assembly { get; }

    // BossMod.GeneratedConfigMetadata._byType : Dictionary<Type, ConfigTypeMetadata>
    public Type ConfigMetadata { get; }
    // BossMod.GeneratedEnumMetadata._byType : Dictionary<Type, Lazy<EnumMetadata>>
    public Type EnumMetadata { get; }
    // BossMod.Service - holds public statics we can use without chasing the plugin instance
    public Type Service { get; }
    // BossMod.Autorotation.RotationModuleRegistry.Modules : Dictionary<Type, Entry>
    public Type? RotationRegistry { get; }

    private BmrHandle(Assembly assembly, Type configMetadata, Type enumMetadata, Type service, Type? rotationRegistry)
    {
        Assembly = assembly;
        ConfigMetadata = configMetadata;
        EnumMetadata = enumMetadata;
        Service = service;
        RotationRegistry = rotationRegistry;
    }

    // Every loaded assembly called BossModReborn, in the order the runtime reports them.
    //
    // There can be more than one. Dalamud updates a plugin by unloading it and loading the new build into
    // a fresh collectible context, and the old context lingers until the GC gets to it - so right after a
    // BossMod update, the previous version is still listed and its types still resolve. Patching that one
    // installs perfectly and then serves nothing, which is indistinguishable from "the translation stopped
    // working". It is exactly what happened on the 7.5.6.5 -> 7.5.6.11 update.
    public static IReadOnlyList<Assembly> LoadedCopies()
    {
        var found = AppDomain.CurrentDomain.GetAssemblies().Where(IsBossMod).ToList();
        if (found.Count > 0)
        {
            return found;
        }
        // fallback for a stale domain view: ask the load contexts directly
        return System.Runtime.Loader.AssemblyLoadContext.All
            .SelectMany(context =>
            {
                try
                {
                    return context.Assemblies;
                }
                catch
                {
                    return [];
                }
            })
            .Where(IsBossMod)
            .ToList();

        static bool IsBossMod(Assembly a) => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal);
    }

    // The copy to attach to: highest version wins, and among equal versions the most recently loaded.
    //
    // Version is the real signal - an update changes it, and it says outright which build is the new one.
    // Load order is only the tie-breaker for a disable/re-enable of the *same* version, where nothing else
    // distinguishes the two; the runtime appends newly loaded assemblies but does not promise to, so it is
    // used no further than it has to be.
    public static Assembly? Live()
    {
        var copies = LoadedCopies();
        return copies.Count <= 1
            ? copies.FirstOrDefault()
            : copies.Select((a, index) => (Assembly: a, Index: index))
                .OrderBy(c => c.Assembly.GetName().Version ?? new Version(0, 0))
                .ThenBy(c => c.Index)
                .Last().Assembly;
    }

    public static BmrHandle? TryResolve(out string? error)
    {
        var copies = LoadedCopies();
        var assembly = Live();
        if (assembly == null)
        {
            error = "BossModReborn assembly is not loaded";
            return null;
        }
        if (copies.Count > 1)
        {
            // qualified: BmrHandle.Service is BossMod's Service type, not ours
            BmrTranslation.Service.Log.Warning($"{copies.Count} copies of BossMod Reborn are loaded ("
                + string.Join(", ", copies.Select(a => a.GetName().Version?.ToString() ?? "?"))
                + $") - this happens right after a BossMod update. Using {assembly.GetName().Version}.");
        }

        var configMetadata = assembly.GetType("BossMod.GeneratedConfigMetadata");
        var enumMetadata = assembly.GetType("BossMod.GeneratedEnumMetadata");
        var service = assembly.GetType("BossMod.Service");
        if (configMetadata == null || enumMetadata == null || service == null)
        {
            error = $"BossModReborn layout changed: config={configMetadata != null} enum={enumMetadata != null} service={service != null}";
            return null;
        }

        error = null;
        return new BmrHandle(assembly, configMetadata, enumMetadata, service,
            assembly.GetType("BossMod.Autorotation.RotationModuleRegistry"));
    }

    public string Version => Assembly.GetName().Version?.ToString() ?? "unknown";

    // Dictionary<Type, ConfigTypeMetadata>
    public IDictionary? ConfigByType => Reflect.GetStatic(ConfigMetadata, "_byType") as IDictionary;

    // Dictionary<Type, Lazy<EnumMetadata>>
    public IDictionary? EnumByType => Reflect.GetStatic(EnumMetadata, "_byType") as IDictionary;

    // Dictionary<Type, RotationModuleRegistry.Entry>
    public IDictionary? RotationModules => Reflect.GetStatic(RotationRegistry, "Modules") as IDictionary;

    // BossMod.Service.WindowSystem - null until BossMod finished initialising
    public object? WindowSystem => Reflect.GetStatic(Service, "WindowSystem");
}
