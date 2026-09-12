using System.Collections;
using BmrTranslation.Interop;
using BmrTranslation.Translation;

namespace BmrTranslation.Patching;

// translates BossMod's generated config metadata.
//
// BossMod's source generator emits, for every config class, one ConfigTypeMetadata holding a fresh
// attribute instance per field (see BossMod.SourceGen/ConfigGenerator.cs). ConfigUI reads Label/Tooltip
// from those attributes on every frame, so rewriting the instances in place is enough - no IL patching,
// no hooks. Attribute instances are never shared between fields, so an in-place rewrite cannot bleed.
//
// The metadata objects keep several references to the same ConfigFieldMetadata (Fields,
// SerializableFields, DisplayFields, FieldsByName), which is exactly why we mutate rather than rebuild:
// swapping in new instances would have to keep all four collections consistent.
public static class ConfigMetadataPatcher
{
    private const string Origin = "config";

    public static void Apply(BmrHandle bmr, PatchSession session)
    {
        var byType = bmr.ConfigByType;
        if (byType == null)
        {
            Service.Log.Warning("GeneratedConfigMetadata._byType not found - config labels stay English");
            return;
        }

        foreach (DictionaryEntry entry in byType)
        {
            if (entry.Key is not Type configType || entry.Value is not { } meta)
            {
                continue;
            }

            // the config tree section name; ConfigUI caches this at construction time, so
            // ConfigUiPatcher additionally fixes up an already-built tree
            if (Reflect.Get(meta, "Display") is { } display)
            {
                session.Property(Keys.Node(configType), display, "Name", Origin);
            }

            if (Reflect.Get(meta, "Fields") is not IEnumerable fields)
            {
                continue;
            }

            foreach (var field in fields)
            {
                if (field != null)
                {
                    ApplyField(configType, field, session);
                }
            }
        }
    }

    private static void ApplyField(Type configType, object field, PatchSession session)
    {
        if (Reflect.GetString(field, "Name") is not { } name)
        {
            return;
        }

        if (Reflect.Get(field, "Display") is { } display)
        {
            session.Field(Keys.Field(configType, name, "label"), display, Reflect.Backing("Label"), Origin);
            session.Field(Keys.Field(configType, name, "tooltip"), display, Reflect.Backing("Tooltip"), Origin);
        }

        if (Reflect.Get(field, "SectionStart") is { } section)
        {
            session.Field(Keys.Field(configType, name, "section"), section, Reflect.Backing("Label"), Origin);
        }

        ApplyStringArray(configType, name, field, "Combo", "Values", "combo", session);
        ApplyStringArray(configType, name, field, "StringOrder", "Values", "order", session);
        ApplyStringArray(configType, name, field, "Group", "Names", "group", session);

        if (Reflect.Get(field, "GroupPresets") is IEnumerable presets)
        {
            var i = 0;
            foreach (var preset in presets)
            {
                if (preset != null)
                {
                    session.Field(Keys.FieldIndexed(configType, name, "preset", i), preset, Reflect.Backing("Name"), Origin);
                }
                ++i;
            }
        }
    }

    private static void ApplyStringArray(Type configType, string field, object metadata, string attributeField, string arrayProperty, string part, PatchSession session)
    {
        if (Reflect.Get(metadata, attributeField) is not { } attribute)
        {
            return;
        }
        if (Reflect.Get(attribute, Reflect.Backing(arrayProperty)) is not string[] values)
        {
            return;
        }
        for (var i = 0; i < values.Length; ++i)
        {
            session.ArrayElement(Keys.FieldIndexed(configType, field, part, i), values, i, Origin);
        }
    }
}
