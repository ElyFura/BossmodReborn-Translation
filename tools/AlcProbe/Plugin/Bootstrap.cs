using System.IO;
using System.Runtime.Loader;

namespace ProbePlugin;

// A copy of BmrTranslation.Interop.HarmonyBootstrap, kept deliberately identical in mechanism: embedded
// 0Harmony, pushed into the default (non-collectible) context before any Harmony type is named. If this
// diverges from the real one the probe stops proving anything, so keep them in step.
public static class Bootstrap
{
    private const string AssemblyName = "0Harmony";
    private const string ResourceName = "ProbePlugin.0Harmony.dll";

    private static bool _attempted;

    public static bool Ready { get; private set; }
    public static string? Error { get; private set; }

    public static bool Ensure()
    {
        if (_attempted)
        {
            return Ready;
        }
        _attempted = true;

        try
        {
            var existing = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Ready = true;
                return true;
            }

            using var stream = typeof(Bootstrap).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException($"embedded resource {ResourceName} is missing");
            AssemblyLoadContext.Default.LoadFromStream(stream);
            Ready = true;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }
}
