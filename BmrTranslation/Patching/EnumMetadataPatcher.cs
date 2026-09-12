using System.Collections;
using BmrTranslation.Interop;
using BmrTranslation.Translation;

namespace BmrTranslation.Patching;

// translates enum member display names - the labels inside every config combo box.
//
// Two things make this different from the config metadata:
//
// 1) BossMod stores the tables as Dictionary<Type, Lazy<EnumMetadata>> and materialises them on demand.
//    Forcing all of them would build large ID-enum tables nobody asked for, so we only translate what has
//    already been created and re-sweep periodically as more appear.
//
// 2) EnumMetadata.DisplayNames is often the *same array instance* as EnumMetadata.Names, and Names is what
//    GeneratedEnumMetadata.Parse matches against when deserializing config. Writing into a shared array
//    would corrupt deserialization, so we clone first and repoint DisplayNames at the copy.
public static class EnumMetadataPatcher
{
    private const string Origin = "enum";

    public static void Sweep(BmrHandle bmr, PatchSession session)
    {
        var byType = bmr.EnumByType;
        if (byType == null)
        {
            Service.Log.Warning("GeneratedEnumMetadata._byType not found - enum labels stay English");
            return;
        }

        foreach (DictionaryEntry entry in byType)
        {
            if (entry.Key is not Type enumType || entry.Value is not { } lazy)
            {
                continue;
            }
            if (Reflect.GetProp(lazy, "IsValueCreated") is not true)
            {
                continue; // not materialised yet - a later sweep will pick it up
            }
            if (Reflect.GetProp(lazy, "Value") is { } metadata)
            {
                ApplyEnum(enumType, metadata, session);
            }
        }
    }

    private static void ApplyEnum(Type enumType, object metadata, PatchSession session)
    {
        if (Reflect.Get(metadata, "Names") is not string[] names)
        {
            return;
        }

        var displayNames = EnsurePrivateDisplayNames(metadata, names, session);
        if (displayNames != null)
        {
            for (var i = 0; i < displayNames.Length && i < names.Length; ++i)
            {
                session.ArrayElement(Keys.EnumMember(enumType, names[i], "display"), displayNames, i, Origin);
            }
        }

        if (Reflect.Get(metadata, "Attributes") is not Array attributes)
        {
            return;
        }
        for (var i = 0; i < attributes.Length && i < names.Length; ++i)
        {
            if (attributes.GetValue(i) is not IEnumerable memberAttributes)
            {
                continue;
            }
            foreach (var attribute in memberAttributes)
            {
                if (attribute?.GetType().Name != "PropertyDisplayAttribute")
                {
                    continue;
                }
                session.Field(Keys.EnumMember(enumType, names[i], "label"), attribute, Reflect.Backing("Label"), Origin);
                session.Field(Keys.EnumMember(enumType, names[i], "tooltip"), attribute, Reflect.Backing("Tooltip"), Origin);
            }
        }
    }

    // returns an array we are allowed to write into, cloning when DisplayNames aliases Names
    private static string[]? EnsurePrivateDisplayNames(object metadata, string[] names, PatchSession session)
    {
        var field = Reflect.InstanceField(metadata.GetType(), "DisplayNames");
        if (field?.GetValue(metadata) is not string[] displayNames)
        {
            return null;
        }
        if (!ReferenceEquals(displayNames, names))
        {
            return displayNames;
        }
        var copy = (string[])displayNames.Clone();
        Reflect.Set(field, metadata, copy);
        session.AddUndo(() => Reflect.Set(field, metadata, displayNames));
        return copy;
    }
}
