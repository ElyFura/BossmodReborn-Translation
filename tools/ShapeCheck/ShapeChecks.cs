using System.Reflection;

namespace ShapeCheck;

// asserts that every type, field, property and method the runtime patchers reach for still exists in the
// installed BossModReborn assembly, and reports readonly-ness (which decides whether Reflect.Set needs to
// emit a setter). Grouped by the patcher that depends on it, so a failure points straight at the code to fix.
public static class ShapeChecks
{
    private const BindingFlags AllI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AllS = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Assembly asm, Report report)
    {
        report.Section("Interop/BmrHandle.cs");
        var cfgMeta = Type(asm, report, "BossMod.GeneratedConfigMetadata");
        var enumMeta = Type(asm, report, "BossMod.GeneratedEnumMetadata");
        var service = Type(asm, report, "BossMod.Service");
        var rotReg = Type(asm, report, "BossMod.Autorotation.RotationModuleRegistry");
        Field(report, cfgMeta, "_byType", AllS);
        Field(report, enumMeta, "_byType", AllS);
        Field(report, service, "WindowSystem", AllS);
        Field(report, rotReg, "Modules", AllS);

        report.Section("Patching/ConfigMetadataPatcher.cs");
        var typeMeta = Type(asm, report, "BossMod.ConfigTypeMetadata");
        var fieldMeta = Type(asm, report, "BossMod.ConfigFieldMetadata");
        Field(report, typeMeta, "Display", AllI);
        Field(report, typeMeta, "Fields", AllI);
        Property(report, Type(asm, report, "BossMod.ConfigDisplayAttribute"), "Name");
        foreach (var name in new[] { "Name", "Display", "SectionStart", "Combo", "StringOrder", "Group", "GroupPresets" })
        {
            Field(report, fieldMeta, name, AllI);
        }
        Backing(asm, report, "BossMod.PropertyDisplayAttribute", "Label");
        Backing(asm, report, "BossMod.PropertyDisplayAttribute", "Tooltip");
        Backing(asm, report, "BossMod.SectionStartAttribute", "Label");
        Backing(asm, report, "BossMod.PropertyComboAttribute", "Values");
        Backing(asm, report, "BossMod.PropertyStringOrderAttribute", "Values");
        Backing(asm, report, "BossMod.GroupDetailsAttribute", "Names");
        Backing(asm, report, "BossMod.GroupPresetAttribute", "Name");

        report.Section("Patching/EnumMetadataPatcher.cs");
        var em = Type(asm, report, "BossMod.EnumMetadata");
        Field(report, em, "Names", AllI);
        Field(report, em, "DisplayNames", AllI);
        Field(report, em, "Attributes", AllI);
        AssertAliasRisk(report, em);

        report.Section("Patching/StrategyPatcher.cs");
        var entry = rotReg?.GetNestedType("Entry", AllI | BindingFlags.Public);
        report.Check(entry != null, "type RotationModuleRegistry.Entry");
        Property(report, entry, "Definition");
        var def = Type(asm, report, "BossMod.Autorotation.RotationModuleDefinition");
        Backing(asm, report, def, "DisplayName");
        Backing(asm, report, def, "Description");
        Backing(asm, report, def, "Category");
        Field(report, def, "Configs", AllI);
        var sc = Type(asm, report, "BossMod.Autorotation.StrategyConfig");
        Backing(asm, report, sc, "InternalName");
        Backing(asm, report, sc, "DisplayName");
        Field(report, Type(asm, report, "BossMod.Autorotation.StrategyConfigTrack"), "Options", AllI);
        var so = Type(asm, report, "BossMod.Autorotation.StrategyOption");
        Field(report, so, "InternalName", AllI);
        Field(report, so, "DisplayName", AllI);

        report.Section("Patching/ConfigUiPatcher.cs");
        var configUi = Type(asm, report, "BossMod.ConfigUI");
        Field(report, configUi, "_roots", AllI);
        Field(report, configUi, "_tabs", AllI);
        Method(report, configUi, "ResolvePaths");
        var uiNode = configUi?.GetNestedType("UINode", AllI);
        report.Check(uiNode != null, "type ConfigUI.UINode");
        Field(report, uiNode, "Node", AllI);
        Field(report, uiNode, "Name", AllI);
        Field(report, uiNode, "Children", AllI);
        var uiTabs = Type(asm, report, "BossMod.UITabs");
        Field(report, uiTabs, "_tabs", AllI);
        Field(report, uiTabs, "_forceSelect", AllI);
        Field(report, Type(asm, report, "BossMod.UISimpleWindow"), "_draw", AllI);
    }

    // EnumMetadata assigns DisplayNames = displayNames ?? names, so the two can be the same array, and
    // Names is what GeneratedEnumMetadata.Parse matches during deserialization. If that constructor ever
    // stops aliasing, EnumMetadataPatcher's defensive clone becomes dead weight - worth noticing.
    private static void AssertAliasRisk(Report report, Type? enumMetadata)
    {
        var ctor = enumMetadata?.GetConstructors(AllI).FirstOrDefault();
        var hasNullableDisplayNames = ctor?.GetParameters().Any(p => p.Name == "displayNames") ?? false;
        report.Check(hasNullableDisplayNames, "EnumMetadata ctor still takes an optional 'displayNames' (DisplayNames may alias Names)");
    }

    private static Type? Type(Assembly asm, Report report, string name)
    {
        var t = asm.GetType(name);
        report.Check(t != null, "type " + name);
        return t;
    }

    private static void Field(Report report, Type? type, string name, BindingFlags flags)
    {
        if (type == null)
        {
            return;
        }
        FieldInfo? f = null;
        for (var cur = type; cur != null && f == null; cur = cur.BaseType)
        {
            f = cur.GetField(name, flags);
        }
        report.Check(f != null, $"{type.Name}.{name}" + (f != null ? $" : {f.FieldType.Name}{(f.IsInitOnly ? " (readonly)" : "")}" : ""));
    }

    private static void Backing(Assembly asm, Report report, string typeName, string property)
        => Backing(asm, report, asm.GetType(typeName), property);

    private static void Backing(Assembly asm, Report report, Type? type, string property)
    {
        if (type == null)
        {
            report.Fail($"cannot check backing field of {property}: declaring type missing");
            return;
        }
        Field(report, type, $"<{property}>k__BackingField", AllI);
    }

    private static void Property(Report report, Type? type, string name)
    {
        if (type == null)
        {
            return;
        }
        var p = type.GetProperty(name, AllI);
        report.Check(p != null, $"{type.Name}.{name}" + (p != null ? $" : {p.PropertyType.Name} (writable={p.CanWrite})" : ""));
    }

    private static void Method(Report report, Type? type, string name)
    {
        if (type == null)
        {
            return;
        }
        var m = type.GetMethod(name, AllI);
        report.Check(m != null, $"{type.Name}.{name}({(m != null ? string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) : "")})");
    }
}
