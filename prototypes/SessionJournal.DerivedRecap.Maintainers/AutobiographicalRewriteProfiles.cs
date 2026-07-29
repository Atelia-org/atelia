using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public static class AutobiographicalRewriteProfiles {
    public const string MaintainerId = "roleplay.first-person-autobiography.rewrite";

    private const string EnglishSystemResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.zh-CN.User.md";

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
            typeof(AutobiographicalRewriteProfiles),
            MaintainerId,
            RolePlayRecapBlockPaths.FirstPersonAutobiography,
            systemResourceName,
            userResourceName
        );
}
