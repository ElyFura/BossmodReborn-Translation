using System.IO;
using BmrTranslation.Interop;
using BmrTranslation.Patching;
using BmrTranslation.Translation;
using BmrTranslation.Ui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace BmrTranslation;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/bmrtl";
    private const string Language = "de";

    private readonly IDalamudPluginInterface _dalamud;
    private readonly TranslationEngine _engine;
    private readonly WindowSystem _windows = new("BmrTranslation");
    private readonly StatusWindow _window;

    public Plugin(IDalamudPluginInterface dalamud)
    {
        dalamud.Create<Service>();
        _dalamud = dalamud;

        // before anything can name a Harmony type: it has to be loaded outside this plugin's collectible
        // load context or no patch will ever apply
        HarmonyBootstrap.Ensure();

        var configDir = dalamud.ConfigDirectory;
        configDir.Create();

        _engine = new TranslationEngine(Language, configDir);
        _window = new StatusWindow(_engine);
        _window.ExportRequested += Extract;
        _windows.AddWindow(_window);

        Service.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "BossMod Reborn translation: status | reload | extract | on | off"
        });
        Service.Framework.Update += OnUpdate;
        dalamud.UiBuilder.Draw += _windows.Draw;
        dalamud.UiBuilder.OpenConfigUi += ToggleWindow;
        dalamud.UiBuilder.OpenMainUi += ToggleWindow;
    }

    public void Dispose()
    {
        _dalamud.UiBuilder.OpenMainUi -= ToggleWindow;
        _dalamud.UiBuilder.OpenConfigUi -= ToggleWindow;
        _dalamud.UiBuilder.Draw -= _windows.Draw;
        Service.Framework.Update -= OnUpdate;
        Service.CommandManager.RemoveHandler(Command);
        _window.ExportRequested -= Extract;
        _windows.RemoveAllWindows();
        _engine.Dispose();
    }

    private void ToggleWindow() => _window.Toggle();

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework) => _engine.Tick();

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                ToggleWindow();
                break;
            case "status":
                Print(_engine.Status());
                break;
            case "reload":
                _engine.Reload();
                Print("reloaded: " + _engine.Status());
                break;
            case "extract":
                Extract();
                break;
            case "on":
                _engine.Reload();
                Print("enabled: " + _engine.Status());
                break;
            case "off":
                _engine.Disable();
                Print("disabled, original English strings restored");
                break;
            default:
                Print("usage: /bmrtl [status|reload|extract|on|off]");
                break;
        }
    }

    private void Extract()
    {
        if (!_engine.Attached)
        {
            Print("not attached to BossMod Reborn yet - nothing to extract");
            return;
        }
        var dir = new DirectoryInfo(Path.Combine(_dalamud.ConfigDirectory.FullName, "extract"));
        var (full, missing, total, missed) = CatalogueWriter.Write(_engine.Catalogue, _engine.Language, dir);
        Print($"{total} strings ({missed} untranslated) written to:");
        Print(full);
        Print(missing);
    }

    private static void Print(string message) => Service.ChatGui.Print("[BMR-TL] " + message);
}
