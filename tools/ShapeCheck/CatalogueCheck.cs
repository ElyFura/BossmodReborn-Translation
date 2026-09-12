using System.Text.Json;

namespace ShapeCheck;

// Drift detection for the keys that cannot be derived from the assembly.
//
// Config and enum strings are rebuilt from metadata, so a changed English wording shows up immediately.
// Autorotation strings cannot be: their keys only exist once BossMod has built its module registry, and
// the display text is assembled by generated code. With 2996 of them translated, "no check at all" means
// a BossMod update can silently leave a third of the language file describing the previous version.
//
// So they are checked against an /bmrtl extract instead - the same catalogue the translations came from.
// Run the extract after a BossMod update and point this at it:
//
//     dotnet run --project tools/ShapeCheck -- --catalogue <pluginConfigs>/BmrTranslation/extract/strings.de.json
//
// It reports two things, the same two the derived checks report: keys that no longer exist, and keys whose
// English source moved out from under the translation.
public static class CatalogueCheck
{
    private static readonly string[] Prefixes = ["rot/", "ui.tab/"];

    private static string Shorten(string text)
        => text.Length <= 70 ? text : text[..70] + "...";

    public static void Run(string cataloguePath, string seedPath, Report report)
    {
        report.Section("catalogue drift: " + Path.GetFileName(cataloguePath));

        Dictionary<string, string> catalogue;
        try
        {
            catalogue = LoadCatalogue(cataloguePath);
        }
        catch (Exception ex)
        {
            report.Fail($"cannot read {cataloguePath}: {ex.Message}");
            return;
        }

        var seed = LoadSeedEnglish(seedPath);
        var checkedKeys = 0;
        var orphans = 0;
        var drifted = 0;
        var unsourced = 0;

        // A catalogue only contains what that session actually walked past. Tab labels, for instance, are
        // recorded when the settings window is first opened - extract without opening it and every tab key
        // looks deleted. Reporting those as orphans would be wrong, and a check that cries wolf gets
        // ignored, so a prefix the catalogue does not mention at all is skipped and said so out loud.
        var exercised = Prefixes
            .Where(p => catalogue.Keys.Any(k => k.StartsWith(p, StringComparison.Ordinal)))
            .ToArray();
        foreach (var prefix in Prefixes.Except(exercised))
        {
            report.Note($"'{prefix}' does not appear in this catalogue - that layer was not exercised in the session, skipped");
        }

        foreach (var (key, storedEnglish) in seed)
        {
            if (!exercised.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }
            ++checkedKeys;
            if (!catalogue.TryGetValue(key, out var current))
            {
                report.Fail($"orphan key, the catalogue no longer contains it: {Shorten(key)}");
                ++orphans;
                continue;
            }
            if (storedEnglish == null)
            {
                ++unsourced;
                continue;
            }
            if (!string.Equals(storedEnglish, current, StringComparison.Ordinal))
            {
                report.Warn($"drift: {Shorten(key)}");
                report.Info($"stored:  {storedEnglish}");
                report.Info($"current: {current}");
                ++drifted;
            }
        }

        // the other direction: text BossMod has that the language file has never seen
        var newKeys = catalogue.Keys.Count(k => exercised.Any(p => k.StartsWith(p, StringComparison.Ordinal)) && !seed.ContainsKey(k));

        if (orphans == 0 && drifted == 0)
        {
            report.Check(true, $"{checkedKeys} runtime-only key(s) present with matching English source");
        }
        if (unsourced > 0)
        {
            report.Note($"{unsourced} key(s) without a stored 'en' - those cannot be drift-checked");
        }
        report.Note(newKeys == 0
            ? "no new runtime-only keys in the catalogue"
            : $"{newKeys} runtime-only key(s) in the catalogue are not in the language file yet");
    }

    // the extract is {"key": {"de": ..., "en": ...}} with $-prefixed metadata at the top
    private static Dictionary<string, string> LoadCatalogue(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('$') || prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (prop.Value.TryGetProperty("en", out var english) && english.GetString() is { } text)
            {
                result[prop.Name] = text;
            }
        }
        return result;
    }

    private static Dictionary<string, string?> LoadSeedEnglish(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('$'))
            {
                continue;
            }
            result[prop.Name] = prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("en", out var en)
                ? en.GetString()
                : null;
        }
        return result;
    }
}
