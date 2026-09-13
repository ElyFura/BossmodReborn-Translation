using BmrTranslation.Patching;
using BmrTranslation.Translation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BmrTranslation.Ui;

// status and maintenance window, wired to both UiBuilder.OpenConfigUi and OpenMainUi.
//
// Beyond satisfying Dalamud's UI-callback expectations, this is the translation work loop: it shows what
// attached, what is untranslated and - the actionable part - which entries drifted, meaning BossMod changed
// an English string out from under a translation that is still being applied.
public sealed class StatusWindow : Window
{
    private readonly TranslationEngine _engine;
    private string _filter = "";
    private bool _showMissing;

    public StatusWindow(TranslationEngine engine) : base("BossMod Reborn Translation###BmrTranslation")
    {
        _engine = engine;
        Size = new(640f, 480f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(380f, 220f), MaximumSize = new(4000f, 4000f) };
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawActions();
        ImGui.Separator();
        DrawEntries();
    }

    private void DrawStatus()
    {
        if (!_engine.Enabled)
        {
            ImGui.TextUnformatted("Übersetzung ist abgeschaltet — die englischen Originale sind wiederhergestellt.");
            return;
        }
        if (_engine.Blocked)
        {
            // loud, because the symptom without this notice is a screen full of "veraltet" and,
            // on unload, German text that never goes back to English
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), "Dieses Plugin ist doppelt geladen.");
            ImGui.TextUnformatted("Diese Kopie bleibt untätig, damit sie nicht das Deutsch der anderen");
            ImGui.TextUnformatted("für BossMods Englisch hält. Entferne eine der beiden:");
            ImGui.BulletText("Dev-Plugin-Pfad unter /xlsettings → Experimental");
            ImGui.BulletText("Installation aus dem Plugin-Repository");
            ImGui.TextUnformatted("Danach greift diese Kopie von selbst wieder, ohne Neuladen.");
            return;
        }
        if (!_engine.Attached)
        {
            ImGui.TextUnformatted("Warte auf BossMod Reborn …");
            ImGui.TextUnformatted(_engine.Status());
            return;
        }

        ImGui.TextUnformatted($"BossMod Reborn {_engine.BossModVersion}  ·  Sprache '{_engine.Language}'  ·  {_engine.EntryCount} Einträge geladen");
        ImGui.TextUnformatted($"{_engine.AppliedCount} übersetzt  ·  {_engine.KeptEnglishCount} bewusst englisch  ·  {_engine.MissingCount} offen  ·  {_engine.StaleCount} veraltet");

        if (_engine.RecoveredCount > 0)
        {
            // the user cannot see this any other way: the strings look right, but the English behind them
            // was gone until this run put it back
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.3f, 1f),
                $"{_engine.RecoveredCount} Strings standen noch deutsch in BossMod — vermutlich war dieses Plugin einmal doppelt geladen.");
            ImGui.TextUnformatted("Das englische Original ist daraus wiederhergestellt; beim Entladen kommt jetzt wieder Englisch zurück.");
        }

        var total = _engine.Catalogue.Count;
        if (total > 0)
        {
            ImGui.ProgressBar(_engine.AppliedCount / (float)total, new(-1f, 0f), $"{_engine.AppliedCount}/{total}");
        }
        if (_engine.OverrideFile is { } file)
        {
            ImGui.TextDisabled($"Override-Datei aktiv: {file}");
        }

        ImGui.Spacing();
        if (_engine.HintsPatchedMethods > 0)
        {
            ImGui.TextUnformatted($"Kampfhinweise: {_engine.HintsPatchedMethods} Methoden gepatcht, {_engine.HintsObserved} Hinweise bisher gesehen");
            ImGui.TextDisabled("Interpolierte Hinweise lassen sich nicht vorab auflisten — sie tauchen erst im Kampf auf.");
        }
        else
        {
            ImGui.TextUnformatted("Kampfhinweise: nicht gepatcht (Hinweise bleiben englisch)");
        }
        foreach (var failure in _engine.HintPatchFailures)
        {
            ImGui.TextDisabled("    " + failure);
        }

        if (_engine.UiPatchedMethods > 0)
        {
            ImGui.TextUnformatted($"Fenstertexte: {_engine.UiPatchedMethods} Methoden gepatcht, {_engine.UiReplacedLiterals} Literale ersetzt");
        }
        else
        {
            ImGui.TextUnformatted("Fenstertexte: nicht gepatcht (bleiben englisch)");
        }
        foreach (var failure in _engine.UiPatchFailures)
        {
            ImGui.TextDisabled("    " + failure);
        }
    }

    private void DrawActions()
    {
        if (ImGui.Button(_engine.Enabled ? "Abschalten" : "Einschalten"))
        {
            if (_engine.Enabled)
            {
                _engine.Disable();
            }
            else
            {
                _engine.Reload();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Neu laden"))
        {
            _engine.Reload();
        }
        ImGui.SameLine();
        if (ImGui.Button("Katalog exportieren"))
        {
            ExportRequested?.Invoke();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Schreibt strings.<lang>.json und missing.<lang>.json in den Plugin-Konfigurationsordner");
        }
    }

    // the plugin owns the file paths and the chat feedback, so exporting is raised rather than done here
    public event Action? ExportRequested;

    private void DrawEntries()
    {
        // drifted entries first: those are the ones a BossMod update silently invalidated
        var stale = _engine.Catalogue.Where(e => e.Stale).ToList();
        if (stale.Count > 0 && ImGui.CollapsingHeader($"Veraltet ({stale.Count})###stale", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var entry in stale)
            {
                ImGui.TextUnformatted(entry.Key);
                ImGui.TextDisabled($"    englisch jetzt: {entry.English}");
            }
        }

        ImGui.Checkbox("Fehlende Übersetzungen anzeigen", ref _showMissing);
        if (!_showMissing)
        {
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextEx("##filter", "Filter (Schlüssel oder englischer Text) …", ref _filter);

        var missing = _engine.Catalogue.Where(e => e.German == null && !e.KeptEnglish && Matches(e)).Take(500).ToList();
        ImGui.TextDisabled($"{missing.Count} angezeigt (max. 500)");

        using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("missing", new(0f, 0f), true);
        if (!child)
        {
            return;
        }
        foreach (var entry in missing)
        {
            ImGui.TextUnformatted(entry.English);
            ImGui.TextDisabled("    " + entry.Key);
        }
    }

    private bool Matches(CatalogueEntry entry)
        => _filter.Length == 0
        || entry.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || entry.English.Contains(_filter, StringComparison.OrdinalIgnoreCase);
}
