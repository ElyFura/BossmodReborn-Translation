using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace BmrTranslation.Translation;

// Text is null for an entry that is deliberately left English (see below).
public sealed record TranslationEntry(string? Text, string? Source);

// translations keyed by a stable, location-derived key (see Keys.cs).
// three shapes are accepted per key:
//   "some.key": "Deutscher Text"
//   "some.key": { "de": "Deutscher Text", "en": "English text at translation time" }
//   "some.key": { "de": "", "en": "..." }        <- deliberately kept English
//
// The optional "en" lets us detect upstream drift: if BossMod's live English string no longer matches,
// the translation is still applied but reported as stale so it can be re-reviewed.
//
// The empty-"de" form matters for a project that will never be 100% German: raid shorthand like
// "MT/R1 N, OT/R2 S" or "LPDU (global): M1>M2>MT>OT>R1>R2>H1>H2" must stay English, because users
// cross-reference it against guides. Without a way to say "decided, stays English", those keys would
// sit in the missing count forever and the number would stop meaning anything.
public sealed class TranslationTable
{
    private readonly Dictionary<string, TranslationEntry> _entries = new(StringComparer.Ordinal);

    private readonly List<HintPattern> _hintPatterns = [];

    public int Count => _entries.Count;
    public string? UserFile { get; private set; }
    public List<string> LoadErrors { get; } = [];

    // ordered: first match wins, so a narrow rule can be placed above a broad one
    public IReadOnlyList<HintPattern> HintPatterns => _hintPatterns;

    public static TranslationTable Load(string language, DirectoryInfo configDir)
    {
        var table = new TranslationTable();
        table.MergeEmbedded(language);
        table.MergeUserFile(Path.Combine(configDir.FullName, language + ".json"));
        return table;
    }

    private void MergeEmbedded(string language)
    {
        var name = "BmrTranslation.Resources." + language + ".json";
        using var stream = typeof(TranslationTable).Assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            LoadErrors.Add($"no embedded translation for '{language}'");
            return;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Merge(reader.ReadToEnd(), name);
    }

    // a file next to the plugin config wins over the shipped resource, so translations can be
    // corrected without rebuilding the plugin
    private void MergeUserFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        UserFile = path;
        Merge(File.ReadAllText(path, Encoding.UTF8), path);
    }

    private void Merge(string json, string origin)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "$hintPatterns")
                {
                    MergeHintPatterns(prop.Value, origin);
                    continue;
                }
                if (prop.Name.StartsWith('$'))
                {
                    continue; // reserved for metadata such as "$schema" / "$notes"
                }
                var entry = Parse(prop.Value);
                if (entry != null)
                {
                    _entries[prop.Name] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            LoadErrors.Add($"{origin}: {ex.Message}");
        }
    }

    // "$hintPatterns": [ { "en": "^Stack with (.+)$", "de": "Mit $1 stacken" } ]
    //
    // Interpolated hints cannot be matched by an exact lookup, so they need rules. A bad regex must not
    // take the whole file down with it, hence per-entry error reporting.
    private void MergeHintPatterns(JsonElement array, string origin)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            LoadErrors.Add($"{origin}: $hintPatterns must be an array");
            return;
        }
        foreach (var item in array.EnumerateArray())
        {
            var en = item.TryGetProperty("en", out var enProp) ? enProp.GetString() : null;
            var de = item.TryGetProperty("de", out var deProp) ? deProp.GetString() : null;
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(de))
            {
                LoadErrors.Add($"{origin}: $hintPatterns entry needs both 'en' and 'de'");
                continue;
            }
            try
            {
                _hintPatterns.Add(new HintPattern(en, de, new Regex(en, RegexOptions.Compiled | RegexOptions.CultureInvariant)));
            }
            catch (ArgumentException ex)
            {
                LoadErrors.Add($"{origin}: invalid hint pattern '{en}': {ex.Message}");
            }
        }
    }

    private static TranslationEntry? Parse(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                return string.IsNullOrEmpty(text) ? null : new TranslationEntry(text, null);
            case JsonValueKind.Object:
                var hasDe = value.TryGetProperty("de", out var deProp);
                var de = hasDe ? deProp.GetString() : null;
                var en = value.TryGetProperty("en", out var enProp) ? enProp.GetString() : null;
                // present but empty is a decision, absent is an omission
                return hasDe ? new TranslationEntry(string.IsNullOrEmpty(de) ? null : de, en) : null;
            default:
                return null;
        }
    }

    public TranslationEntry? Lookup(string key) => _entries.GetValueOrDefault(key);

    // used by the UI patcher to discover which methods need patching at all
    public IEnumerable<KeyValuePair<string, TranslationEntry>> WithPrefix(string prefix)
        => _entries.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal));
}
