namespace BmrTranslation.Translation;

// translation keys are derived from where a string lives, never from the string itself.
// that way retranslating an upstream wording change is a diff on one key instead of a resurvey,
// and the stored "en" value tells us when a key's meaning drifted.
public static class Keys
{
    // config tree section, e.g. cfg.node/BossMod.BossModuleConfig
    public static string Node(Type configType) => "cfg.node/" + Name(configType);

    // config field, e.g. cfg/BossMod.BossModuleConfig/EnableRadar/label
    public static string Field(Type configType, string field, string part)
        => "cfg/" + Name(configType) + "/" + field + "/" + part;

    public static string FieldIndexed(Type configType, string field, string part, int index)
        => Field(configType, field, part) + "." + index.ToString();

    // enum member display name, e.g. enum/BossMod.AI.AIConfig+Mode/Manual/display
    public static string EnumMember(Type enumType, string member, string part)
        => "enum/" + Name(enumType) + "/" + member + "/" + part;

    // autorotation module definition, e.g. rot/BossMod.Autorotation.xyz/name
    public static string Rotation(Type moduleType, string part)
        => "rot/" + Name(moduleType) + "/" + part;

    // autorotation strategy track / option, keyed by InternalName because that is what BossMod
    // persists and therefore the only part guaranteed to stay stable across versions
    public static string Track(Type moduleType, string trackInternalName, string part)
        => "rot/" + Name(moduleType) + "/track/" + trackInternalName + "/" + part;

    public static string TrackOption(Type moduleType, string trackInternalName, string optionInternalName)
        => "rot/" + Name(moduleType) + "/track/" + trackInternalName + "/opt/" + optionInternalName;

    // config window tab, e.g. ui.tab/Settings
    public static string Tab(string originalName) => "ui.tab/" + originalName;

    private static string Name(Type type) => type.FullName ?? type.Name;
}
