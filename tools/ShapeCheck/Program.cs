using System.Reflection;
using ShapeCheck;

// Offline guard against the main risk of this plugin: BossMod Reborn moves something and the runtime
// reflection silently stops matching. Run it after every BossMod update - it needs no game and no Dalamud
// process, only the installed assembly on disk.

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        usage: dotnet run --project tools/ShapeCheck [options]

          --bmr <path>       BossModReborn.dll (default: newest under XIVLauncher installedPlugins/devPlugins)
          --dalamud <dir>    Dalamud assembly directory (default: $DALAMUD_HOME or XIVLauncher addon/Hooks/dev)
          --seed <path>      translation file to validate (default: BmrTranslation/Resources/de.json)
          --strict           treat drift warnings as failures
          --missing          print untranslated keys as "key<TAB>english" and exit
        """);
    return 0;
}

var bmrPath = Arg("--bmr") ?? Discover.BossModAssembly();
var dalamudDir = Arg("--dalamud") ?? Discover.DalamudDirectory();
var seedPath = Arg("--seed") ?? Discover.SeedFile();
var strict = args.Contains("--strict");

if (bmrPath == null)
{
    Console.Error.WriteLine("could not find BossModReborn.dll - pass --bmr <path>");
    return 2;
}
if (dalamudDir == null)
{
    Console.Error.WriteLine("could not find the Dalamud assemblies - pass --dalamud <dir>");
    return 2;
}

Console.Error.WriteLine($"assembly: {bmrPath}");
Console.Error.WriteLine($"dalamud:  {dalamudDir}");
Console.Error.WriteLine($"seed:     {seedPath ?? "(none found, skipping seed checks)"}");

var probe = new List<string> { bmrPath };
probe.AddRange(Directory.GetFiles(dalamudDir, "*.dll"));
probe.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(probe.Distinct()));
var asm = mlc.LoadFromAssemblyPath(bmrPath);
Console.Error.WriteLine($"version:  {asm.GetName().Name} {asm.GetName().Version}");

if (args.Contains("--missing"))
{
    SeedChecks.DumpMissing(asm, seedPath, Console.Out);
    return 0;
}

var report = new Report();
ShapeChecks.Run(asm, report);
if (seedPath != null)
{
    SeedChecks.Run(asm, seedPath, report);
}
return report.Summarise(strict);

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
