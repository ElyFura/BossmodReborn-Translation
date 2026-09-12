using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BmrTranslation.Translation;

// the string extractor.
//
// It does not parse BossMod's source: it dumps what the patchers actually walked past in memory, so the
// keys are by construction the same ones the lookup uses, and the English text is whatever the installed
// BossMod version really contains. That removes the usual failure mode of source-scraped translation
// files - keys that never match at runtime.
//
// Output next to the plugin config:
//   strings.<lang>.json  - every key, with English source and current translation (a ready-to-edit file)
//   missing.<lang>.json  - only the untranslated keys, pre-filled with the English text
public static class CatalogueWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static (string Full, string Missing, int Total, int Missed) Write(IReadOnlyCollection<CatalogueEntry> catalogue, string language, DirectoryInfo dir)
    {
        dir.Create();
        var full = Path.Combine(dir.FullName, $"strings.{language}.json");
        var missing = Path.Combine(dir.FullName, $"missing.{language}.json");

        var ordered = catalogue.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();

        File.WriteAllText(full, Render(ordered, onlyMissing: false, language), Encoding.UTF8);
        var missed = ordered.Count(e => e.German == null && !e.KeptEnglish);
        File.WriteAllText(missing, Render(ordered, onlyMissing: true, language), Encoding.UTF8);

        return (full, missing, ordered.Count, missed);
    }

    private static string Render(List<CatalogueEntry> entries, bool onlyMissing, string language)
    {
        var sb = new StringBuilder(entries.Count * 96);
        sb.AppendLine("{");
        sb.Append("  \"$generated\": ").Append(Json(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))).AppendLine(",");
        sb.Append("  \"$language\": ").Append(Json(language)).AppendLine(",");
        sb.AppendLine(onlyMissing
            ? "  \"$notes\": \"Untranslated keys only. Replace each empty \\\"de\\\" with the translation and merge into the language file.\","
            : "  \"$notes\": \"Full catalogue of translatable strings found in the installed BossMod Reborn.\",");

        var written = 0;
        var last = entries.Count - 1;
        for (var i = 0; i <= last; ++i)
        {
            var e = entries[i];
            if (onlyMissing && (e.German != null || e.KeptEnglish))
            {
                continue;
            }
            if (written++ > 0)
            {
                sb.AppendLine(",");
            }
            sb.Append("  ").Append(Json(e.Key)).Append(": { \"de\": ").Append(Json(e.German ?? ""))
              .Append(", \"en\": ").Append(Json(e.English));
            if (e.KeptEnglish)
            {
                // an explicit empty "de" - this key is decided, it stays English
                sb.Append(", \"kept_english\": true");
            }
            else if (e.Stale)
            {
                // the translation was written against a different English text and needs re-review
                sb.Append(", \"stale\": true");
            }
            sb.Append(" }");
        }

        sb.AppendLine();
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Json(string value) => JsonSerializer.Serialize(value, Options);
}
