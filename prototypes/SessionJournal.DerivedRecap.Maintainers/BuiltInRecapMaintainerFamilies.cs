namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public static class BuiltInRecapMaintainerFamilies {
    private const string EnglishSystemResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.SharedRewrite.en.System.md";
    private const string SimplifiedChineseSystemResourceName =
        "Atelia.SessionJournal.Maintainers.Prompts.SharedRewrite.zh-CN.System.md";

    public static RecapMaintainerFamilyDefinition English { get; } =
        Create(
            "shared-rewrite-en",
            EnglishSystemResourceName
        );

    public static RecapMaintainerFamilyDefinition SimplifiedChinese {
        get;
    } = Create(
        "shared-rewrite-zh-CN",
        SimplifiedChineseSystemResourceName
    );

    public static RecapMaintainerFamilyDefinition Default =>
        SimplifiedChinese;

    private static RecapMaintainerFamilyDefinition Create(
        string diagnosticName,
        string systemResourceName
    ) => new(
        diagnosticName,
        EmbeddedRecapPromptLoader.Read(
            typeof(BuiltInRecapMaintainerFamilies),
            systemResourceName
        ),
        StructuredRecapMaintainerOutputProtocol.Shared
    );
}
