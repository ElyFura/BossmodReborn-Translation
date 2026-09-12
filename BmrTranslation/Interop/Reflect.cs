using System.Collections.Concurrent;
using System.Reflection.Emit;

namespace BmrTranslation.Interop;

// low-level reflection helpers for poking at BossMod's internals
// the interesting part is SetField: a lot of BossMod's user-facing text sits in readonly fields (including
// compiler-generated backing fields of get-only auto properties), which FieldInfo.SetValue may refuse to write.
// for those we emit a DynamicMethod that does a plain stfld - the JIT does not enforce initonly there.
public static class Reflect
{
    public const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ConcurrentDictionary<FieldInfo, Action<object, object?>> _setters = [];

    // backing field name of a get-only auto property, e.g. "Label" -> "<Label>k__BackingField"
    public static string Backing(string propertyName) => "<" + propertyName + ">k__BackingField";

    public static FieldInfo? InstanceField(Type? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, AllInstance);
            if (f != null)
            {
                return f;
            }
        }
        return null;
    }

    public static FieldInfo? StaticField(Type? type, string name) => type?.GetField(name, AllStatic);

    public static object? GetStatic(Type? type, string name) => StaticField(type, name)?.GetValue(null);

    public static object? Get(object instance, string fieldName)
        => InstanceField(instance.GetType(), fieldName)?.GetValue(instance);

    public static string? GetString(object instance, string fieldName) => Get(instance, fieldName) as string;

    // writes a field even when it is declared readonly; returns false if the field does not exist
    public static bool Set(object instance, string fieldName, object? value)
    {
        var f = InstanceField(instance.GetType(), fieldName);
        if (f == null)
        {
            return false;
        }
        Set(f, instance, value);
        return true;
    }

    public static void Set(FieldInfo field, object instance, object? value)
        => _setters.GetOrAdd(field, BuildSetter)(instance, value);

    private static Action<object, object?> BuildSetter(FieldInfo field)
    {
        if (!field.IsInitOnly)
        {
            return field.SetValue;
        }
        try
        {
            return EmitSetter(field);
        }
        catch (Exception ex)
        {
            // emitting into another plugin's assembly should work, but if the runtime ever refuses we
            // still have a decent chance with plain reflection on an instance readonly field
            Service.Log.Warning(ex, $"could not emit a setter for {field.DeclaringType?.Name}.{field.Name}, falling back to SetValue");
            return field.SetValue;
        }
    }

    private static Action<object, object?> EmitSetter(FieldInfo field)
    {
        var dm = new DynamicMethod(
            "set_" + field.DeclaringType!.Name + "_" + field.Name,
            typeof(void),
            [typeof(object), typeof(object)],
            typeof(Reflect).Module,
            skipVisibility: true);

        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, field.DeclaringType!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(field.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, field.FieldType);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);

        return dm.CreateDelegate<Action<object, object?>>();
    }

    // reads a property through its getter (records expose positional parameters as get-only properties)
    public static object? GetProp(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, AllInstance)?.GetValue(instance);
}
