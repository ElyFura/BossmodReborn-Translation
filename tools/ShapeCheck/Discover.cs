namespace ShapeCheck;

// locates the installed BossMod assembly, the Dalamud reference assemblies and this repo's translation
// file, so the usual invocation needs no arguments at all
public static class Discover
{
    public static string? BossModAssembly()
    {
        var xiv = XivLauncherRoot();
        if (xiv == null)
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (var sub in new[] { "installedPlugins", "devPlugins" })
        {
            var dir = Path.Combine(xiv, sub);
            if (Directory.Exists(dir))
            {
                candidates.AddRange(Directory.GetFiles(dir, "BossModReborn.dll", SearchOption.AllDirectories));
            }
        }

        // prefer the highest version directory, falling back to the newest file
        return candidates
            .OrderByDescending(p => ParseVersion(Path.GetFileName(Path.GetDirectoryName(p))))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string? DalamudDirectory()
    {
        var home = Environment.GetEnvironmentVariable("DALAMUD_HOME");
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
        {
            return home;
        }
        var xiv = XivLauncherRoot();
        if (xiv == null)
        {
            return null;
        }
        var dev = Path.Combine(xiv, "addon", "Hooks", "dev");
        return Directory.Exists(dev) ? dev : null;
    }

    public static string? SeedFile()
    {
        var relative = Path.Combine("BmrTranslation", "Resources", "de.json");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? XivLauncherRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return null;
        }
        var root = Path.Combine(appData, "XIVLauncher");
        return Directory.Exists(root) ? root : null;
    }

    private static Version ParseVersion(string? text)
        => Version.TryParse(text, out var v) ? v : new Version(0, 0);
}
