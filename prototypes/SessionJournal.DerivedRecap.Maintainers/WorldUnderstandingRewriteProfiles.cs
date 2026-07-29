using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public static class WorldUnderstandingRewriteProfiles {
    public const string MaintainerId = "roleplay.world-understanding.rewrite";

    private const string EnglishSystemResourceName = "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.SessionJournal.Maintainers.Prompts.WorldUnderstandingRewrite.zh-CN.User.md";

    public static RecapRewriteProfile English { get; } = Read(
        EnglishSystemResourceName,
        EnglishUserResourceName
    );

    public static RecapRewriteProfile SimplifiedChinese { get; } = Read(
        SimplifiedChineseSystemResourceName,
        SimplifiedChineseUserResourceName
    );

    public static RecapRewriteProfile Default => SimplifiedChinese;

    private static RecapRewriteProfile Read(string systemResourceName, string userResourceName)
        => EmbeddedRecapRewriteProfileLoader.Read(
            typeof(WorldUnderstandingRewriteProfiles),
            MaintainerId,
            RolePlayRecapBlockPaths.WorldUnderstanding,
            systemResourceName,
            userResourceName
        );
}
