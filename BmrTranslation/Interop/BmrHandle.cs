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

    public static BmrHandle? TryResolve(out string? error)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal));
        if (assembly == null)
        {
            error = "BossModReborn assembly is not loaded";
            return null;
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
