using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Does Harmony work under .NET 10, across an AssemblyLoadContext boundary, on the exact member shapes
// Milestones 2 and 3 need? That is the gating question, and it can be answered without the game - but
// only if the host reproduces how Dalamud actually loads a plugin:
//
//   * the plugin assembly goes into a COLLECTIBLE ALC, so the plugin can be unloaded and reloaded
//   * the target plugin (BossMod) sits in a second, separate collectible ALC
//
// The first version of this probe kept Harmony in the default, non-collectible context and called it
// green. That configuration does not exist in Dalamud, and the difference is not cosmetic: MonoMod's
// IL generator builds a proxy type that has to resolve 0Harmony by name, and the runtime refuses to
// resolve a name to a collectible assembly. So the host holds nothing but the two loaders - it does not
// reference Harmony, does not preload it, and leaves every Harmony call to the plugin, which carries
// 0Harmony as an embedded resource and pushes it into the default context itself, exactly as
// BmrTranslation does.
//
// usage: dotnet run --project tools/AlcProbe/Host [-- <flag>...]
//
//   --as-shipped-before   reproduce the arrangement that failed in game: 0Harmony sitting next to the
//                         plugin and no bootstrap. Expected to fail, and the probe says so.
//   --non-collectible     the configuration the first version of this probe tested by mistake.
//   --no-bootstrap        skip the bootstrap without staging the file.
//
// The negative flags are here so the failure can be demonstrated on demand rather than taken on trust -
// the earlier green result is exactly what happens when it cannot be.

var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var here = AppContext.BaseDirectory;
var pluginDll = Path.GetFullPath(paths.Length > 0 ? paths[0] : Path.Combine(here, "..", "..", "..", "..", "Plugin", "bin", Configuration, "net10.0", "ProbePlugin.dll"));
var targetDll = Path.GetFullPath(paths.Length > 1 ? paths[1] : Path.Combine(here, "..", "..", "..", "..", "Target", "bin", Configuration, "net10.0", "ProbeTarget.dll"));
var asShippedBefore = args.Contains("--as-shipped-before");
var collectible = !args.Contains("--non-collectible");
var bootstrap = !args.Contains("--no-bootstrap") && !asShippedBefore;

Console.WriteLine($"host            = ALC={AssemblyLoadContext.GetLoadContext(typeof(Program).Assembly)?.Name} collectible=False");
Console.WriteLine($"plugin context  = collectible={collectible}, harmony bootstrap={bootstrap}");

if (asShippedBefore)
{
    pluginDll = StageHarmonyBesidePlugin(pluginDll);
}

// Step 0, against the real plugin: Dalamud enumerates a plugin's types before it constructs it, and that
// is where the second attempt at this fell over - the patches were fine, the assembly would not load.
Console.WriteLine("\n-- 0. the shipped plugin assembly enumerates its types with 0Harmony absent --");
var loadFailures = PluginLoad.Check(RealPluginDll, DalamudDirectory) ? 0 : 1;

var failures = Load(1);
if (failures >= 0)
{
    // Toggling the plugin off and on in Dalamud discards the collectible context and builds a fresh one,
    // while 0Harmony stays in the default context from the first load. The second pass exercises the
    // branch that reuses it instead of loading a second copy of the same identity.
    Console.WriteLine("\n===== second load, fresh collectible context (plugin toggled off and on) =====");
    var again = Load(2);
    failures = again < 0 ? -1 : failures + again;
}
if (failures >= 0)
{
    failures += loadFailures;
}

if (asShippedBefore)
{
    Console.WriteLine(failures == 0
        ? "\nUNEXPECTED - shipping 0Harmony beside the plugin now works; the bootstrap may no longer be needed"
        : "\nEXPECTED FAILURE - this is the arrangement that produced 64 + 75 failed patches in game");
    return failures == 0 ? 1 : 0;
}

Console.WriteLine(failures == 0
    ? "\nPASS - Harmony works on this runtime, from a collectible plugin context, on every shape Milestones 2 and 3 need"
    : failures < 0 ? "\nFAIL - probe crashed" : $"\nFAIL - {failures} probe(s) failed");
return failures == 0 ? 0 : 1;

[MethodImpl(MethodImplOptions.NoInlining)]
int Load(int pass)
{
    var pluginAlc = new PrivateContext($"plugin-{pass}", Path.GetDirectoryName(pluginDll)!, collectible);
    var targetAlc = new PrivateContext($"target-{pass}", Path.GetDirectoryName(targetDll)!, collectible);

    var plugin = pluginAlc.LoadFromAssemblyPath(pluginDll);
    var target = targetAlc.LoadFromAssemblyPath(targetDll);
    var run = plugin.GetType("ProbePlugin.Probe")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

    int result;
    try
    {
        result = (int)run.Invoke(null, [target, bootstrap])!;
    }
    catch (TargetInvocationException ex)
    {
        Console.WriteLine("\nUNHANDLED: " + ex.InnerException);
        return -1;
    }

    if (collectible)
    {
        pluginAlc.Unload();
        targetAlc.Unload();
    }
    return result;
}

// Puts 0Harmony where a normal PackageReference would have put it - next to the plugin, where the
// plugin's own collectible context resolves it. That is the arrangement this whole exercise exists to
// rule out.
//
// Into a throwaway directory, never the build output: loading an assembly memory-maps and locks the
// file, and after the failure the context is never unloaded, so a copy staged into bin/ could not be
// removed again and would quietly poison every later run.
string StageHarmonyBesidePlugin(string original)
{
    var source = Path.Combine(
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        "lib.harmony", HarmonyVersion, "lib", "net10.0", "0Harmony.dll");
    var staging = Directory.CreateTempSubdirectory("alcprobe-").FullName;
    var destination = Path.Combine(staging, Path.GetFileName(original));
    File.Copy(original, destination);
    File.Copy(source, Path.Combine(staging, "0Harmony.dll"));
    Console.WriteLine($"staged          = plugin + 0Harmony.dll side by side in {staging}");
    return destination;
}

partial class Program
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    // kept in step with Plugin.csproj by hand; only --as-shipped-before reads it
    private const string HarmonyVersion = "2.4.2";

    // the real plugin, checked by step 0; skipped with a message when it has not been built
    internal static string RealPluginDll { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
        "BmrTranslation", "bin", "x64", "Release", "BmrTranslation.dll"));

    // same rule the csproj uses: DALAMUD_HOME wins, so this runs on a CI machine that has no XIVLauncher
    internal static string DalamudDirectory { get; } =
        Environment.GetEnvironmentVariable("DALAMUD_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "addon", "Hooks", "dev");
}

// mirrors Dalamud's per-plugin loader: private dependencies resolve out of the plugin's own directory,
// everything else falls through to the default context - which is how the plugin's Harmony reference
// finds the copy the plugin itself put there
sealed class PrivateContext(string name, string directory, bool collectible) : AssemblyLoadContext(name, collectible)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
