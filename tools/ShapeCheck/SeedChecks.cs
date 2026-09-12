using System.Reflection;
using System.Text.Json;

namespace ShapeCheck;

// validates the shipped translation file against the installed assembly, offline.
//
// Two failure modes are worth catching before the game ever runs:
//   orphan - a key that no longer exists in BossMod (field or type renamed), so the translation is dead
//   drift  - the key still exists but BossMod's English wording changed, so the translation needs review
//
// Attribute arguments are read straight out of the metadata blob. Attributes with optional constructor
// parameters carry every argument positionally in the blob (defaults filled in), so PropertyDisplay's
// tooltip is simply ConstructorArguments[2] - no named-argument handling required.
public static class SeedChecks
{
    private const BindingFlags AllFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // key prefixes that cannot be derived from metadata alone: enum display names live in generated tables
    // and autorotation names are built by module constructors at runtime
    private static readonly string[] RuntimeOnlyPrefixes = ["ui.tab/", "enum/", "rot/"];

    // dumps untranslated config keys with their English text, so a translation batch can be prepared
    // without launching the game (the in-game /bmrtl extract additionally covers the runtime-only keys
    // this cannot see).
    //
    // JSON, not a flat table: BossMod tooltips contain embedded newlines, and a line-oriented format would
    // have to mangle them - which silently corrupts the 'en' value that drift detection compares against.
    public static void DumpMissing(Assembly asm, string? seedPath, TextWriter output, bool all = false)
    {
        // a decided key is not a pending key: an empty "de" means "stays English", so it is excluded
        // from the dump just like a translated one.
        // `all` dumps everything instead, which is what you need to revise a key that is already in the
        // language file - the merge tool only ever takes English text from this dump.
        var decided = !all && seedPath != null && File.Exists(seedPath)
            ? LoadSeed(seedPath).Where(e => e.Value.De != null).Select(e => e.Key).ToHashSet(StringComparer.Ordinal)
            : [];

        var missing = DeriveEnglish(asm)
            .Where(e => !decided.Contains(e.Key))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        output.Write(JsonSerializer.Serialize(missing, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        output.WriteLine();
    }

    public static void Run(Assembly asm, string seedPath, Report report)
    {
        report.Section("Resources/" + Path.GetFileName(seedPath));

        var english = DeriveEnglish(asm);
        report.Info($"{english.Count} config strings derivable from the assembly");

        Dictionary<string, (string? De, string? En)> seed;
        try
        {
            seed = LoadSeed(seedPath);
        }
        catch (Exception ex)
        {
            report.Fail($"cannot read {seedPath}: {ex.Message}");
            return;
        }

        var checkedKeys = 0;
        var runtimeOnly = 0;
        var orphans = 0;
        var drifted = 0;
        var keptEnglish = 0;

        foreach (var (key, entry) in seed)
        {
            if (RuntimeOnlyPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            {
                ++runtimeOnly;
                continue;
            }

            ++checkedKeys;
            if (entry.De?.Length == 0)
            {
                ++keptEnglish; // explicit empty "de": decided to stay English
            }
            if (!english.TryGetValue(key, out var current))
            {
                report.Fail($"orphan key, not present in BossMod: {key}");
                ++orphans;
                continue;
            }
            if (entry.En != null && !string.Equals(entry.En, current, StringComparison.Ordinal))
            {
                report.Warn($"drift: {key}");
                report.Info($"stored:  {entry.En}");
                report.Info($"current: {current}");
                ++drifted;
            }
        }

        if (orphans == 0 && drifted == 0)
        {
            report.Check(true, $"{checkedKeys} config key(s) present with matching English source");
        }
        report.Info($"{runtimeOnly} key(s) not statically verifiable (enum / autorotation / tab labels)");

        // coverage counts only what is actually meant to become German
        var translatable = english.Count - keptEnglish;
        var translated = checkedKeys - keptEnglish;
        var coverage = translatable <= 0 ? 100.0 : 100.0 * translated / translatable;
        report.Note(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "config coverage: {0}/{1} translated ({2:F1}%), {3} deliberately English, {4} still open",
            translated, translatable, coverage, keptEnglish, translatable - translated));
        report.Note($"missing 'en' source on {seed.Count(s => !RuntimeOnlyPrefixes.Any(p => s.Key.StartsWith(p, StringComparison.Ordinal)) && s.Value.En == null)} config key(s) - those cannot be drift-checked");
    }

    private static Dictionary<string, (string? De, string? En)> LoadSeed(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = new Dictionary<string, (string? De, string? En)>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('$'))
            {
                continue;
            }
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => (prop.Value.GetString(), null),
                JsonValueKind.Object => (Str(prop.Value, "de"), Str(prop.Value, "en")),
                _ => (null, null)
            };
        }
        return result;

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
    }

    // rebuilds the same keys ConfigMetadataPatcher produces at runtime, with their English text
    private static Dictionary<string, string> DeriveEnglish(Assembly asm)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in asm.GetTypes())
        {
            var configDisplay = type.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "ConfigDisplayAttribute");
            if (configDisplay != null)
            {
                // Name is a settable property, so it arrives as a named argument - and is absent for the
                // per-encounter configs whose section name ConfigUI derives from the type instead
                var name = configDisplay.NamedArguments.FirstOrDefault(n => n.MemberName == "Name").TypedValue.Value as string;
                Add(result, "cfg.node/" + type.FullName, name);
            }

            foreach (var field in type.GetFields(AllFields))
            {
                var prefix = "cfg/" + type.FullName + "/" + field.Name + "/";
                foreach (var attr in field.GetCustomAttributesData())
                {
                    var args = attr.ConstructorArguments;
                    switch (attr.AttributeType.Name)
                    {
                        case "PropertyDisplayAttribute":
                            Add(result, prefix + "label", At(args, 0));
                            Add(result, prefix + "tooltip", At(args, 2));
                            break;
                        case "SectionStartAttribute":
                            Add(result, prefix + "section", At(args, 0));
                            break;
                        case "PropertyComboAttribute":
                            AddMany(result, prefix + "combo", args);
                            break;
                        case "PropertyStringOrderAttribute":
                            AddMany(result, prefix + "order", args);
                            break;
                        case "GroupDetailsAttribute":
                            AddMany(result, prefix + "group", args);
                            break;
                        case "GroupPresetAttribute":
                            // repeatable attribute; the runtime keys are indexed by position
                            var index = 0;
                            while (result.ContainsKey($"{prefix}preset.{index}"))
                            {
                                ++index;
                            }
                            Add(result, $"{prefix}preset.{index}", At(args, 0));
                            break;
                    }
                }
            }
        }

        return result;
    }

    private static string? At(IList<CustomAttributeTypedArgument> args, int index)
        => index < args.Count ? args[index].Value as string : null;

    // string[] ctor, or the two-string PropertyCombo(falseText, trueText) overload
    private static void AddMany(Dictionary<string, string> into, string prefix, IList<CustomAttributeTypedArgument> args)
    {
        if (args.Count > 0 && args[0].Value is IReadOnlyList<CustomAttributeTypedArgument> items)
        {
            for (var i = 0; i < items.Count; ++i)
            {
                Add(into, $"{prefix}.{i}", items[i].Value as string);
            }
            return;
        }
        for (var i = 0; i < args.Count; ++i)
        {
            Add(into, $"{prefix}.{i}", args[i].Value as string);
        }
    }

    // the runtime patchers skip empty strings, so an empty default must not become a key
    private static void Add(Dictionary<string, string> into, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            into[key] = value;
        }
    }
}
