namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public static class AutobiographicalRecapMaintainers {
    public const string MaintainerId =
        "roleplay.first-person-autobiography.rewrite";

    private const string EnglishRoleResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.en.System.md";
    private const string EnglishTaskResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.en.User.md";
    private const string SimplifiedChineseRoleResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.zh-CN.System.md";
    private const string SimplifiedChineseTaskResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.zh-CN.User.md";

    public static RecapMaintainerDefinition English { get; } = Create(
        BuiltInRecapMaintainerFamilies.English,
        EnglishRoleResourceName,
        EnglishTaskResourceName
    );

    public static RecapMaintainerDefinition SimplifiedChinese { get; } =
        Create(
            BuiltInRecapMaintainerFamilies.SimplifiedChinese,
            SimplifiedChineseRoleResourceName,
            SimplifiedChineseTaskResourceName
        );

    public static RecapMaintainerDefinition Default => SimplifiedChinese;

    private static RecapMaintainerDefinition Create(
        RecapMaintainerFamilyDefinition family,
        string roleResourceName,
        string taskResourceName
    ) => new(
        RecapMaintainerImplementationIds.StructuredRewrite,
        MaintainerId,
        RolePlayRecapBlockPaths.FirstPersonAutobiography,
        family,
        EmbeddedRecapPromptLoader.ReadTaskInstruction(
            typeof(AutobiographicalRecapMaintainers),
            roleResourceName,
            taskResourceName
        )
    );
}
