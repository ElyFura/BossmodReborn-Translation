using System.Collections;
using BmrTranslation.Interop;
using BmrTranslation.Translation;

namespace BmrTranslation.Patching;

// translates enum member display names - the labels inside every config combo box.
//
// Three things make this different from the config metadata:
//
// 1) BossMod stores the tables as Dictionary<Type, Lazy<EnumMetadata>> and materialises them on demand.
//    Forcing all of them would build large ID-enum tables nobody asked for, so we only translate what has
//    already been created and re-sweep periodically as more appear.
//
// 2) Most registered enums are not text at all. See InScope: only config field types and enums with
//    PropertyDisplay members are translated, which is 263 members instead of 40543.
//
// 3) EnumMetadata.DisplayNames is often the *same array instance* as EnumMetadata.Names, and Names is what
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

        var configEnums = ConfigFieldEnums(bmr);

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
            if (Reflect.GetProp(lazy, "Value") is { } metadata && InScope(enumType, metadata, configEnums))
            {
                ApplyEnum(enumType, metadata, session);
            }
        }
    }

    // BossMod registers every enum it has, and most of them are conventions, not text: AID, SID, OID,
    // IconID and friends account for 40280 of the 40543 member names in the assembly. Their "names" are
    // identity - "Ability_1234" must never become German - and because they materialise as soon as a
    // module runs, translating them buried the 263 real ones under thousands of dead keys.
    //
    // In scope is what a player can actually read:
    //   * the enum type of a config field, which is what a settings combo box lists
    //   * any enum whose members carry PropertyDisplay, because someone wrote display text for them
    //
    // Both are decided per enum, and both are cheap to answer from data already in hand.
    private static bool InScope(Type enumType, object metadata, HashSet<Type> configEnums)
        => configEnums.Contains(enumType) || HasDisplayAttribute(metadata);

    private static bool HasDisplayAttribute(object metadata)
    {
        if (Reflect.Get(metadata, "Attributes") is not Array attributes)
        {
            return false; // name-only metadata: a convention enum
        }
        foreach (var member in attributes)
        {
            if (member is IEnumerable memberAttributes)
            {
                foreach (var attribute in memberAttributes)
                {
                    if (attribute?.GetType().Name == "PropertyDisplayAttribute")
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // Rebuilt per sweep rather than cached: it is a few thousand cached-reflection reads every two
    // seconds, which is not worth the lifetime of a static that would have to be invalidated on reload.
    private static HashSet<Type> ConfigFieldEnums(BmrHandle bmr)
    {
        var result = new HashSet<Type>();
        if (bmr.ConfigByType is not { } byType)
        {
            return result;
        }
        foreach (DictionaryEntry entry in byType)
        {
            if (entry.Value is not { } metadata || Reflect.Get(metadata, "Fields") is not Array fields)
            {
                continue;
            }
            foreach (var field in fields)
            {
                if (field != null && Reflect.Get(field, "FieldType") is Type { IsEnum: true } fieldType)
                {
                    result.Add(fieldType);
                }
            }
        }
        return result;
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
