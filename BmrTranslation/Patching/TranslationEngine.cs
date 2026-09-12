using System.IO;
using BmrTranslation.Interop;
using BmrTranslation.Translation;

namespace BmrTranslation.Patching;

// owns the apply/revert lifecycle.
//
// BossMod loads independently of us and may not be installed at all, so attaching is retried from the
// framework tick instead of the constructor. Everything we change is recorded and undone on Dispose:
// we are mutating objects BossMod owns, and leaving them German after an unload would be a bug the user
// could only fix by restarting the game.
public sealed class TranslationEngine(string language, DirectoryInfo configDir) : IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);

    private BmrHandle? _bmr;
    private TranslationTable _table = new();
    private PatchSession? _session;
    private ConfigUiPatcher? _uiPatcher;
    private HintTranslator? _hints;
    private HintPatcher? _hintPatcher;
    private UiPatcher? _uiTextPatcher;
    private DateTime _nextSweep;
    private string? _lastAttachError;

    public bool Attached => _session != null;
    public bool Enabled { get; private set; } = true;
    public string Language { get; } = language;

    public void Tick()
    {
        if (!Enabled)
        {
            return;
        }
        var now = DateTime.UtcNow;
        if (now < _nextSweep)
        {
            return;
        }
        _nextSweep = now + SweepInterval;

        try
        {
            if (_session == null)
            {
                TryAttach();
            }
            else
            {
                Sweep();
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "translation sweep failed");
        }
    }

    private void TryAttach()
    {
        var bmr = BmrHandle.TryResolve(out var error);
        if (bmr == null)
        {
            if (error != _lastAttachError)
            {
                _lastAttachError = error;
                Service.Log.Information($"waiting for BossMod Reborn: {error}");
            }
            return;
        }
        if (bmr.WindowSystem == null)
        {
            return; // BossMod is loading but has not set up its services yet
        }

        _bmr = bmr;
        _table = TranslationTable.Load(Language, configDir);
        foreach (var loadError in _table.LoadErrors)
        {
            Service.Log.Warning($"translation file: {loadError}");
        }

        _session = new PatchSession(_table);
        _uiPatcher = new ConfigUiPatcher(bmr);

        ConfigMetadataPatcher.Apply(bmr, _session);
        StrategyPatcher.Apply(bmr, _session);
        Sweep();

        // combat hints are the one layer that needs IL patching; keep it last so a Harmony problem
        // cannot cost us the config translation that already succeeded
        _hints = new HintTranslator(_table);
        _hintPatcher = new HintPatcher(bmr, _hints);
        try
        {
            _hintPatcher.Apply();
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "hint patching failed - config stays translated, hints stay English");
            _hintPatcher = null;
        }

        // the remaining UI literals, also via Harmony but with an independent patch id, so a failure here
        // leaves the config and the hints translated
        _uiTextPatcher = new UiPatcher(bmr, _table);
        try
        {
            _uiTextPatcher.Apply();
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "ui patching failed - the rest stays translated, window text stays English");
            _uiTextPatcher = null;
        }

        Service.Log.Information($"attached to BossModReborn {bmr.Version}: {_session.Applied} strings translated, {_session.Missing} without translation");
    }

    // re-run the parts that can grow after attach: enum tables materialise lazily and the settings
    // window is rebuilt every time it is opened
    private void Sweep()
    {
        if (_bmr == null || _session == null)
        {
            return;
        }
        EnumMetadataPatcher.Sweep(_bmr, _session);
        _uiPatcher?.Sweep(_session);
    }

    public void Reload()
    {
        Revert();
        Enabled = true;
        _nextSweep = DateTime.MinValue;
        Tick();
    }

    public void Disable()
    {
        Revert();
        Enabled = false;
    }

    private void Revert()
    {
        _uiTextPatcher?.Dispose();
        _uiTextPatcher = null;
        _hintPatcher?.Dispose();
        _hintPatcher = null;
        _hints = null;
        _session?.Revert();
        _session = null;
        _uiPatcher = null;
        _bmr = null;
    }

    public string Status()
    {
        if (!Enabled)
        {
            return "disabled";
        }
        if (_session == null || _bmr == null)
        {
            return _lastAttachError ?? "waiting for BossMod Reborn";
        }
        var user = _table.UserFile != null ? $", override file {_table.UserFile}" : "";
        var hints = _hintPatcher != null
            ? $" | hints: {_hintPatcher.PatchedMethods} patched methods, {_hints?.ObservedCount ?? 0} seen"
            : " | hints: not patched";
        var ui = _uiTextPatcher != null
            ? $" | ui: {_uiTextPatcher.PatchedMethods} patched methods, {_uiTextPatcher.ReplacedLiterals} literals"
            : " | ui: not patched";
        return $"BossModReborn {_bmr.Version} | {_table.Count} entries in '{Language}' | {_session.Applied} applied, {_session.KeptEnglish} kept English, {_session.Missing} missing, {_session.Stale} stale{hints}{ui}{user}";
    }

    // config keys from the sweep, plus every hint seen in play so far - the only way to discover the
    // interpolated hint wordings, which cannot be enumerated from the assembly
    public IReadOnlyCollection<CatalogueEntry> Catalogue
    {
        get
        {
            var config = _session?.Catalogue ?? [];
            if (_hints == null)
            {
                return config;
            }
            var combined = new List<CatalogueEntry>(config);
            foreach (var english in _hints.Observed)
            {
                var entry = _table.Lookup(Keys.Hint(english));
                combined.Add(new CatalogueEntry(Keys.Hint(english), english, entry?.Text,
                    Stale: false, KeptEnglish: entry != null && entry.Text == null, Origin: "hint"));
            }
            return combined;
        }
    }

    public int UiPatchedMethods => _uiTextPatcher?.PatchedMethods ?? 0;
    public int UiReplacedLiterals => _uiTextPatcher?.ReplacedLiterals ?? 0;
    public IReadOnlyList<string> UiPatchFailures => _uiTextPatcher?.Failures ?? [];

    public int HintsPatchedMethods => _hintPatcher?.PatchedMethods ?? 0;
    public int HintsObserved => _hints?.ObservedCount ?? 0;
    public IReadOnlyList<string> HintPatchFailures => _hintPatcher?.Failures ?? [];

    public string? BossModVersion => _bmr?.Version;
    public int EntryCount => _table.Count;
    public string? OverrideFile => _table.UserFile;
    public int AppliedCount => _session?.Applied ?? 0;
    public int MissingCount => _session?.Missing ?? 0;
    public int StaleCount => _session?.Stale ?? 0;
    public int KeptEnglishCount => _session?.KeptEnglish ?? 0;

    public void Dispose() => Revert();
}
