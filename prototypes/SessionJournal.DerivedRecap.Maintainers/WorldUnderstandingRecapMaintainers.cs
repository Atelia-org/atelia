namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public static class WorldUnderstandingRecapMaintainers {
    public const string MaintainerId =
        "roleplay.world-understanding.rewrite";

    private const string EnglishRoleResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.en.System.md";
    private const string EnglishTaskResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.en.User.md";
    private const string SimplifiedChineseRoleResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.zh-CN.System.md";
    private const string SimplifiedChineseTaskResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.zh-CN.User.md";

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
        RolePlayRecapBlockPaths.WorldUnderstanding,
        family,
        EmbeddedRecapPromptLoader.ReadTaskInstruction(
            typeof(WorldUnderstandingRecapMaintainers),
            roleResourceName,
            taskResourceName
        )
    );
}
