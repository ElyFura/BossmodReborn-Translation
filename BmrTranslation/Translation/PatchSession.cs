using BmrTranslation.Interop;

namespace BmrTranslation.Translation;

public sealed record CatalogueEntry(string Key, string English, string? German, bool Stale, bool KeptEnglish, string Origin);

// one apply/revert cycle over BossMod's live metadata.
//
// two jobs, deliberately in the same place:
//   - apply a translation to a field and remember how to put the English string back
//   - record every key we walked past, translated or not, so the extractor can dump a complete
//     catalogue without a second traversal (and without reading already-translated values)
//
// reverting matters: we mutate objects owned by BossMod, so without an undo log, unloading this
// plugin would leave BossMod's UI half-German until the game restarts.
public sealed class PatchSession(TranslationTable table)
{
    private readonly List<Action> _undo = [];
    private readonly Dictionary<object, HashSet<string>> _patched = new(ReferenceEqualityComparer.Instance);
    private readonly SortedDictionary<string, CatalogueEntry> _catalogue = new(StringComparer.Ordinal);

    public int Applied { get; private set; }
    public int Missing { get; private set; }
    public int Stale { get; private set; }
    // strings a previous instance left German; see Original
    public int Recovered { get; private set; }
    // keys with an explicit empty translation: decided to stay English, not an omission
    public int KeptEnglish { get; private set; }

    public IReadOnlyCollection<CatalogueEntry> Catalogue => _catalogue.Values;

    // translate a string field (or a get-only property's backing field) on a BossMod object
    public void Field(string key, object owner, string fieldName, string origin)
    {
        if (!Claim(owner, fieldName))
        {
            return;
        }
        var f = Reflect.InstanceField(owner.GetType(), fieldName);
        if (f == null || f.GetValue(owner) is not string current || current.Length == 0)
        {
            return;
        }
        var english = Original(key, current);
        var translated = Record(key, english, origin);
        if (translated == null)
        {
            return;
        }
        Reflect.Set(f, owner, translated);
        _undo.Add(() => Reflect.Set(f, owner, english));
        ++Applied;
    }

    // translate a string property that has a real setter
    public void Property(string key, object owner, string propertyName, string origin)
    {
        if (!Claim(owner, "prop:" + propertyName))
        {
            return;
        }
        var p = owner.GetType().GetProperty(propertyName, Reflect.AllInstance);
        if (p?.GetValue(owner) is not string current || current.Length == 0 || !p.CanWrite)
        {
            return;
        }
        var english = Original(key, current);
        var translated = Record(key, english, origin);
        if (translated == null)
        {
            return;
        }
        p.SetValue(owner, translated);
        _undo.Add(() => p.SetValue(owner, english));
        ++Applied;
    }

    // translate one element of a string array that BossMod hands to the UI
    public void ArrayElement(string key, string[] array, int index, string origin)
    {
        if (index < 0 || index >= array.Length || !Claim(array, index.ToString()))
        {
            return;
        }
        var current = array[index];
        if (current.Length == 0)
        {
            return;
        }
        var english = Original(key, current);
        var translated = Record(key, english, origin);
        if (translated == null)
        {
            return;
        }
        array[index] = translated;
        _undo.Add(() => array[index] = english);
        ++Applied;
    }

    // for call sites that need the translated value rather than a field write (e.g. tab labels)
    public string? Translate(string key, string english, string origin)
    {
        Record(key, english, origin);
        return table.Lookup(key)?.Text;
    }

    public void AddUndo(Action revert) => _undo.Add(revert);

    public void Revert()
    {
        for (var i = _undo.Count - 1; i >= 0; --i)
        {
            try
            {
                _undo[i]();
            }
            catch (Exception ex)
            {
                Service.Log.Error(ex, "failed to revert a translated string");
            }
        }
        _undo.Clear();
        _patched.Clear();
    }

    // Recovers the English original when a previous instance left its German behind.
    //
    // Two copies of this plugin loaded at once produce exactly that: the second one reads the first one's
    // German, records it as the original, and writes it back on unload. BossMod builds this metadata once
    // per process, so the German then sits there until the game restarts - and every key reports as stale,
    // because the stored English no longer matches what is found.
    //
    // The repair is unambiguous, which is why it is safe to do automatically: only when the value found is
    // *exactly* the translation this file would have written is the stored English treated as the original.
    // That keeps the undo log honest - it restores English rather than cementing the German - and it costs
    // one dictionary lookup that Record would perform anyway.
    private string Original(string key, string current)
    {
        var entry = table.Lookup(key);
        if (entry?.Text == null || entry.Source == null
            || !string.Equals(current, entry.Text, StringComparison.Ordinal)
            || string.Equals(current, entry.Source, StringComparison.Ordinal))
        {
            return current;
        }
        ++Recovered;
        return entry.Source;
    }

    private bool Claim(object owner, string discriminator)
    {
        if (!_patched.TryGetValue(owner, out var set))
        {
            _patched[owner] = set = [];
        }
        return set.Add(discriminator);
    }

    // returns the translation to write, or null when there is none; always records the key
    private string? Record(string key, string english, string origin)
    {
        var entry = table.Lookup(key);
        var stale = entry?.Source != null && !string.Equals(entry.Source, english, StringComparison.Ordinal);
        var keptEnglish = entry != null && entry.Text == null;
        if (!_catalogue.ContainsKey(key))
        {
            _catalogue[key] = new CatalogueEntry(key, english, entry?.Text, stale, keptEnglish, origin);
            if (entry == null)
            {
                ++Missing;
            }
            else if (keptEnglish)
            {
                ++KeptEnglish;
            }
            else if (stale)
            {
                ++Stale;
            }
        }
        return entry?.Text;
    }
}
