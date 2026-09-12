using System.Reflection;

namespace ShapeCheck;

// Rebuilds the enum/ keys EnumMetadataPatcher produces at runtime - offline, from attribute metadata.
//
// Doing it offline is not just convenience. BossMod materialises its enum tables lazily, so an in-game
// /bmrtl extract only ever reports the enums that session happened to touch: a combo box nobody opened
// stays invisible, and its keys silently never get written. Deriving them from the assembly gives the
// complete set, and gives them the same drift detection every config key has.
//
// Scope has to match EnumMetadataPatcher.InScope exactly, or the two disagree about which keys exist:
//   * enums that are the declared type of a config field - the combo boxes in the settings window
//   * enums whose members carry PropertyDisplay - somebody wrote display text for them on purpose
//
// Everything else is convention: AID, SID, OID, IconID and the rest, whose member names are identity.
public static class EnumDerivation
{
    private const BindingFlags AllFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public static void Derive(Assembly asm, Dictionary<string, string> into)
    {
        var types = asm.GetTypes();
        var scope = new HashSet<Type>();

        foreach (var type in types)
        {
            if (type.GetCustomAttributesData().Any(a => a.AttributeType.Name == "ConfigDisplayAttribute"))
            {
                foreach (var field in type.GetFields(AllFields))
                {
                    if (field.FieldType.IsEnum)
                    {
                        scope.Add(field.FieldType);
                    }
                }
            }
            else if (type.IsEnum && Members(type).Any(m => Display(m) != null))
            {
                scope.Add(type);
            }
        }

        foreach (var enumType in scope)
        {
            foreach (var member in Members(enumType))
            {
                var prefix = "enum/" + enumType.FullName + "/" + member.Name + "/";
                var display = Display(member);
                var label = display == null ? null : Arg(display, 0);
                var tooltip = display == null ? null : Arg(display, 2);

                // DisplayNames defaults to the member name and is only replaced where PropertyDisplay
                // supplies a label, which is exactly how the source generator fills the array
                Add(into, prefix + "display", label ?? member.Name);
                Add(into, prefix + "label", label);
                Add(into, prefix + "tooltip", tooltip);
            }
        }
    }

    // enum members are the static literal fields; the instance field holding the value is not one
    private static IEnumerable<FieldInfo> Members(Type enumType)
        => enumType.GetFields(BindingFlags.Public | BindingFlags.Static);

    private static CustomAttributeData? Display(FieldInfo member)
        => member.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "PropertyDisplayAttribute");

    private static string? Arg(CustomAttributeData attribute, int index)
        => index < attribute.ConstructorArguments.Count ? attribute.ConstructorArguments[index].Value as string : null;

    private static void Add(Dictionary<string, string> into, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            into[key] = value;
        }
    }
}
