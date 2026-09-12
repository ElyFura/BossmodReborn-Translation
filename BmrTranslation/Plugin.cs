using System.IO;
using BmrTranslation.Patching;
using BmrTranslation.Translation;
using Dalamud.Game.Command;
using Dalamud.Plugin;

namespace BmrTranslation;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/bmrtl";
    private const string Language = "de";

    private readonly TranslationEngine _engine;

    public Plugin(IDalamudPluginInterface dalamud)
    {
        dalamud.Create<Service>();

        var configDir = dalamud.ConfigDirectory;
        configDir.Create();

        _engine = new TranslationEngine(Language, configDir);

        Service.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "BossMod Reborn translation: status | reload | extract | on | off"
        });
        Service.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnUpdate;
        Service.CommandManager.RemoveHandler(Command);
        _engine.Dispose();
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework) => _engine.Tick();

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
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
        var dir = new DirectoryInfo(Path.Combine(Service.PluginInterface.ConfigDirectory.FullName, "extract"));
        var (full, missing, total, missed) = CatalogueWriter.Write(_engine.Catalogue, _engine.Language, dir);
        Print($"{total} strings ({missed} untranslated) written to:");
        Print(full);
        Print(missing);
    }

    private static void Print(string message) => Service.ChatGui.Print("[BMR-TL] " + message);
}
