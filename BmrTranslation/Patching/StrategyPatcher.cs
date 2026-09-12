using System.Collections;
using BmrTranslation.Interop;
using BmrTranslation.Translation;

namespace BmrTranslation.Patching;

// translates the autorotation UI: module names, descriptions, strategy tracks and their options.
//
// BossMod keeps display text and persisted identity in separate members here (StrategyConfig.InternalName
// vs DisplayName, StrategyOption likewise), and Preset serialization only ever writes InternalName. So
// translating DisplayName cannot corrupt saved presets - which is why this layer is safe without hooks.
public static class StrategyPatcher
{
    private const string Origin = "rotation";

    public static void Apply(BmrHandle bmr, PatchSession session)
    {
        var modules = bmr.RotationModules;
        if (modules == null)
        {
            Service.Log.Warning("RotationModuleRegistry.Modules not found - autorotation names stay English");
            return;
        }

        foreach (DictionaryEntry entry in modules)
        {
            if (entry.Key is not Type moduleType || entry.Value is not { } registryEntry)
            {
                continue;
            }
            if (Reflect.GetProp(registryEntry, "Definition") is { } definition)
            {
                ApplyDefinition(moduleType, definition, session);
            }
        }
    }

    private static void ApplyDefinition(Type moduleType, object definition, PatchSession session)
    {
        session.Field(Keys.Rotation(moduleType, "name"), definition, Reflect.Backing("DisplayName"), Origin);
        session.Field(Keys.Rotation(moduleType, "desc"), definition, Reflect.Backing("Description"), Origin);
        session.Field(Keys.Rotation(moduleType, "category"), definition, Reflect.Backing("Category"), Origin);

        if (Reflect.Get(definition, "Configs") is not IEnumerable configs)
        {
            return;
        }

        foreach (var config in configs)
        {
            if (config == null || Reflect.Get(config, Reflect.Backing("InternalName")) is not string internalName)
            {
                continue;
            }

            session.Field(Keys.Track(moduleType, internalName, "name"), config, Reflect.Backing("DisplayName"), Origin);

            // only StrategyConfigTrack carries selectable options
            if (Reflect.Get(config, "Options") is not IEnumerable options)
            {
                continue;
            }
            foreach (var option in options)
            {
                if (option != null && Reflect.GetString(option, "InternalName") is { } optionName)
                {
                    // StrategyOption declares DisplayName as a plain mutable field, not a record property
                    session.Field(Keys.TrackOption(moduleType, internalName, optionName), option, "DisplayName", Origin);
                }
            }
        }
    }
}
