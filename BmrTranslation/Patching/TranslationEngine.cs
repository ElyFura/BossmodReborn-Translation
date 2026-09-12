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
        return $"BossModReborn {_bmr.Version} | {_table.Count} entries in '{Language}' | {_session.Applied} applied, {_session.Missing} missing, {_session.Stale} stale{user}";
    }

    public IReadOnlyCollection<CatalogueEntry> Catalogue => _session?.Catalogue ?? [];

    public void Dispose() => Revert();
}
