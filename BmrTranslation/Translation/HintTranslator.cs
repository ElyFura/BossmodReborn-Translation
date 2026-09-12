using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BmrTranslation.Translation;

public sealed record HintPattern(string Source, string Replacement, Regex Compiled);

// Translates combat hints. This layer works differently from the config layer, for two reasons that are
// properties of BossMod rather than choices:
//
// 1) Hints have no location. They are string literals inside 800+ call sites (`hints.Add("Stay together!")`),
//    so there is nothing stable to key on except the English text itself. Config keys are location-derived
//    precisely to avoid that; here it is unavoidable.
//
// 2) Roughly a third of them are interpolated - `hints.Add($"Stack with {name}")` - so an exact lookup can
//    never match. Those need ordered regex rules, and the rules have to be written against hints actually
//    observed in play, because interpolated text cannot be enumerated from the assembly.
//
// Consequence: every hint that passes through is recorded, so /bmrtl extract can hand back the ones that
// found no translation. That is the only way to discover the interpolated wordings.
public sealed class HintTranslator(TranslationTable table)
{
    private const int MaxObservations = 2000;

    // hints are recalculated every frame, so a miss must be as cheap as a hit: once a string has failed
    // both the dictionary and every pattern, remember that and stop re-running the regexes
    private readonly ConcurrentDictionary<string, string?> _resolved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _observed = new(StringComparer.Ordinal);

    public int ObservedCount => _observed.Count;
    public int TranslatedCount { get; private set; }

    public IReadOnlyCollection<string> Observed => _observed.Keys.ToList();

    // returns null when the hint should stay as it is
    public string? Translate(string english)
    {
        if (english.Length == 0)
        {
            return null;
        }
        if (_resolved.TryGetValue(english, out var cached))
        {
            return cached;
        }

        var resolved = Resolve(english);
        _resolved[english] = resolved;
        if (_observed.Count < MaxObservations)
        {
            _observed.TryAdd(english, 0);
        }
        if (resolved != null)
        {
            ++TranslatedCount;
        }
        return resolved;
    }

    private string? Resolve(string english)
    {
        // an exact entry wins over any pattern, so a specific wording can always override a general rule
        var entry = table.Lookup(Keys.Hint(english));
        if (entry != null)
        {
            return entry.Text; // null here means "deliberately English"
        }

        foreach (var pattern in table.HintPatterns)
        {
            var match = pattern.Compiled.Match(english);
            if (match.Success)
            {
                return match.Result(pattern.Replacement);
            }
        }
        return null;
    }

    // /bmrtl reload must forget the cache, otherwise edited translations would not take effect
    public void Reset()
    {
        _resolved.Clear();
        TranslatedCount = 0;
    }
}
