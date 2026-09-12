using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShapeCheck;

// Extracts the fixed combat-hint literals.
//
// These cannot be derived from attributes like the config strings - they are `hints.Add("...")` arguments
// inside 800+ call sites. So this reads BossMod's source tree, and then verifies every literal against the
// installed assembly's bytes: string literals live UTF-16LE in the #US heap, so a literal the shipped build
// does not contain is either from a newer working copy or a mis-parse, and gets dropped rather than
// shipped as a translation key that can never match.
//
// Interpolated hints ($"...") are deliberately not extracted. They compile to string.Format calls with no
// single literal to key on, so they need the $hintPatterns rules instead, written against hints actually
// observed in play.
public static partial class HintExtraction
{
    public static int Run(string sourceDir, string assemblyPath, TextWriter output, Report report)
    {
        if (!Directory.Exists(sourceDir))
        {
            report.Fail($"source directory not found: {sourceDir}");
            return 1;
        }

        var literals = new SortedSet<string>(StringComparer.Ordinal);
        var skippedInterpolated = 0;
        var skippedVerbatim = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (var match in HintAdd().Matches(text).Cast<Match>())
            {
                literals.Add(Unescape(match.Groups[1].Value));
            }
            // An interpolated literal with no placeholder compiles to a plain string, so it is a fixed
            // hint despite the $ prefix. Skipping all $"..." calls would silently lose those.
            foreach (var match in InterpolatedHintAdd().Matches(text).Cast<Match>())
            {
                var body = match.Groups[1].Value;
                if (body.Contains('{') || body.Contains('}'))
                {
                    ++skippedInterpolated; // has holes - needs a $hintPatterns rule instead
                }
                else
                {
                    literals.Add(Unescape(body));
                }
            }
            skippedVerbatim += VerbatimHintAdd().Matches(text).Count;

            foreach (var block in PrePullHintsArray().Matches(text).Cast<Match>())
            {
                foreach (var item in PlainString().Matches(block.Groups[1].Value).Cast<Match>())
                {
                    var literal = Unescape(item.Groups[1].Value);
                    // Some pre-pull arrays mix plain and interpolated entries. A single regex cannot
                    // tokenise that, so it occasionally captures the gap *between* two literals (", " or
                    // "]"). Requiring a letter discards those without discarding real hints.
                    if (literal.Any(char.IsLetter))
                    {
                        literals.Add(literal);
                    }
                }
            }
        }

        report.Section("hint extraction");
        report.Info($"{literals.Count} distinct fixed hint literals in {sourceDir}");
        report.Info($"{skippedInterpolated} interpolated and {skippedVerbatim} verbatim hint call(s) skipped - those need $hintPatterns");

        var assembly = File.ReadAllBytes(assemblyPath);
        var verified = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unverified = new List<string>();
        foreach (var literal in literals)
        {
            if (ContainsUtf16(assembly, literal))
            {
                verified["hint/" + literal] = literal;
            }
            else
            {
                unverified.Add(literal);
            }
        }

        report.Check(verified.Count > 0, $"{verified.Count} literal(s) present in the installed assembly");
        if (unverified.Count > 0)
        {
            report.Warn($"{unverified.Count} literal(s) not found in the installed assembly - dropped");
            foreach (var literal in unverified.Take(10))
            {
                report.Info("dropped: " + literal);
            }
        }

        output.Write(JsonSerializer.Serialize(verified, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        output.WriteLine();
        return 0;
    }

    // user strings are stored UTF-16LE, so a plain byte search over the file is a sound presence test
    private static bool ContainsUtf16(byte[] haystack, string needle)
    {
        var pattern = Encoding.Unicode.GetBytes(needle);
        if (pattern.Length == 0 || pattern.Length > haystack.Length)
        {
            return false;
        }
        var last = haystack.Length - pattern.Length;
        for (var i = 0; i <= last; ++i)
        {
            var j = 0;
            while (j < pattern.Length && haystack[i + j] == pattern[j])
            {
                ++j;
            }
            if (j == pattern.Length)
            {
                return true;
            }
        }
        return false;
    }

    // one pass, so a literal backslash cannot be re-interpreted as the start of another escape
    private static string Unescape(string literal)
    {
        var sb = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; ++i)
        {
            if (literal[i] != '\\' || i + 1 >= literal.Length)
            {
                sb.Append(literal[i]);
                continue;
            }
            var escape = literal[++i];
            sb.Append(escape switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => escape // covers escaped quote, escaped backslash and anything else verbatim
            });
        }
        return sb.ToString();
    }

    // hints.Add("text" ...) - the receiver is uniformly named `hints` in BossMod
    [GeneratedRegex(""""hints\.Add\("((?:[^"\\]|\\.)*)"""")]
    private static partial Regex HintAdd();

    // an interpolated hint; group 1 is the format text, which is a fixed literal when it has no holes
    [GeneratedRegex(""""hints\.Add\(\$"((?:[^"\\]|\\.)*)"""")]
    private static partial Regex InterpolatedHintAdd();

    [GeneratedRegex("""hints\.Add\(@""")]
    private static partial Regex VerbatimHintAdd();

    // private readonly string[] _prePullHints = [ "...", "..." ];
    [GeneratedRegex("""_prePullHints\s*=\s*(\[[^\]]*\])""", RegexOptions.Singleline)]
    private static partial Regex PrePullHintsArray();

    // a plain (non-interpolated, non-verbatim) string literal inside an array initialiser
    [GeneratedRegex(""""(?<![$@])"((?:[^"\\]|\\.)*)"""")]
    private static partial Regex PlainString();
}
