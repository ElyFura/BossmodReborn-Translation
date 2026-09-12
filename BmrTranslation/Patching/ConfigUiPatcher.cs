using System.Collections;
using BmrTranslation.Interop;
using BmrTranslation.Translation;
using Dalamud.Interface.Windowing;

namespace BmrTranslation.Patching;

// fixes up the settings window itself.
//
// ConfigUI copies two things into its own state at construction time and never reads them again:
//   - the section name of every config node (ConfigUI.UINode.Name)
//   - its tab names (UITabs)
// Translating the metadata alone therefore leaves the tree and the tab bar in English, so we reach the
// live ConfigUI instance. We get there without chasing BossMod's plugin object: BossMod.Service.WindowSystem
// is a public static, the config window is a UISimpleWindow, and its draw delegate's Target *is* the ConfigUI.
//
// Tab names double as identity (UITabs compares them for ImGui ids and for ConfigUI.ShowTab), so they are
// rewritten as "Deutsch###Original" to keep ImGui ids stable, and a small draw wrapper maps ShowTab's
// pending selection back onto the translated name.
public sealed class ConfigUiPatcher(BmrHandle bmr)
{
    private const string Origin = "ui";
    private const string WindowName = "BossModReborn";

    private object? _configUi;
    private readonly Dictionary<string, string> _tabMap = new(StringComparer.Ordinal);

    // called repeatedly: the config window is detached and disposed on close, so it reappears as a new
    // object every time the user opens the settings
    public void Sweep(PatchSession session)
    {
        var window = FindConfigWindow();
        if (window == null)
        {
            return;
        }

        if (_configUi == null && Reflect.Get(window, "_draw") is Action draw && draw.Target is { } target)
        {
            _configUi = target;
            ApplyTree(_configUi, session);
            ApplyTabs(_configUi, session);
        }

        InstallTabSelectFix(window, session);
    }

    private object? FindConfigWindow()
    {
        if (bmr.WindowSystem is not WindowSystem ws)
        {
            return null; // BossMod has not finished initialising
        }
        foreach (var window in ws.Windows)
        {
            if (window.WindowName.StartsWith(WindowName, StringComparison.Ordinal))
            {
                return window;
            }
        }
        return null;
    }

    private static void ApplyTree(object configUi, PatchSession session)
    {
        if (Reflect.Get(configUi, "_roots") is IEnumerable roots)
        {
            ApplyNodes(roots, session);
        }

        // node names feed the search index, so rebuild the cached paths afterwards
        var resolve = configUi.GetType().GetMethod("ResolvePaths", Reflect.AllInstance);
        var rootsList = Reflect.Get(configUi, "_roots");
        if (resolve != null && rootsList != null)
        {
            try
            {
                resolve.Invoke(configUi, [rootsList, Activator.CreateInstance(resolve.GetParameters()[1].ParameterType)]);
            }
            catch (Exception ex)
            {
                Service.Log.Warning(ex, "could not rebuild config tree paths - the settings search may still match English text");
            }
        }
    }

    private static void ApplyNodes(IEnumerable nodes, PatchSession session)
    {
        foreach (var node in nodes)
        {
            if (node == null)
            {
                continue;
            }
            // nodes without a config object are per-encounter hint nodes named after the boss - left alone
            if (Reflect.Get(node, "Node") is { } configNode)
            {
                session.Field(Keys.Node(configNode.GetType()), node, "Name", Origin);
            }
            if (Reflect.Get(node, "Children") is IEnumerable children)
            {
                ApplyNodes(children, session);
            }
        }
    }

    private void ApplyTabs(object configUi, PatchSession session)
    {
        if (Reflect.Get(configUi, "_tabs") is not { } tabs || Reflect.Get(tabs, "_tabs") is not IList entries)
        {
            return;
        }

        for (var i = 0; i < entries.Count; ++i)
        {
            var boxed = entries[i];
            if (boxed == null)
            {
                continue;
            }
            var nameField = boxed.GetType().GetField("Item1");
            if (nameField?.GetValue(boxed) is not string original)
            {
                continue;
            }

            var translated = session.Translate(Keys.Tab(original), original, Origin);
            if (translated == null)
            {
                continue;
            }

            // "label###id" keeps the ImGui id equal to the original name
            var label = translated + "###" + original;
            nameField.SetValue(boxed, label);
            entries[i] = boxed;
            _tabMap[original] = label;

            var index = i;
            session.AddUndo(() =>
            {
                var revert = entries[index];
                if (revert != null)
                {
                    nameField.SetValue(revert, original);
                    entries[index] = revert;
                }
            });
        }
    }

    private void InstallTabSelectFix(object window, PatchSession session)
    {
        if (_tabMap.Count == 0 || _configUi == null)
        {
            return;
        }
        if (Reflect.Get(window, "_draw") is not Action draw || draw.Target is TabSelectFix)
        {
            return; // already wrapped (or nothing to wrap)
        }
        if (Reflect.Get(_configUi, "_tabs") is not { } tabs)
        {
            return;
        }

        var fix = new TabSelectFix(tabs, draw, _tabMap);
        if (Reflect.Set(window, "_draw", (Action)fix.Draw))
        {
            session.AddUndo(() => Reflect.Set(window, "_draw", draw));
        }
    }

    // ConfigUI.ShowTab stores the English tab name in UITabs._forceSelect, which no longer matches the
    // translated label. Remapping it right before the draw keeps the "jump to tab" callers working.
    private sealed class TabSelectFix(object tabs, Action inner, Dictionary<string, string> tabMap)
    {
        public void Draw()
        {
            if (Reflect.GetString(tabs, "_forceSelect") is { Length: > 0 } pending && tabMap.TryGetValue(pending, out var translated))
            {
                Reflect.Set(tabs, "_forceSelect", translated);
            }
            inner();
        }
    }
}
