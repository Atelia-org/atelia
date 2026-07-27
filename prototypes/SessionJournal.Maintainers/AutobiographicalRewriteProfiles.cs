using Atelia.SessionJournal;

namespace Atelia.SessionJournal.Maintainers;

public static class AutobiographicalRewriteProfiles {
    public const string MaintainerId = "roleplay.first-person-autobiography.rewrite";

    private const string EnglishSystemResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.SessionJournal.Maintainers.Prompts.AutobiographicalRewrite.zh-CN.User.md";

    public static MemoryRewriteProfile English { get; } = Read(
        EnglishSystemResourceName,
        EnglishUserResourceName
    );

    public static MemoryRewriteProfile SimplifiedChinese { get; } = Read(
        SimplifiedChineseSystemResourceName,
        SimplifiedChineseUserResourceName
    );

    public static MemoryRewriteProfile Default => SimplifiedChinese;

    private static MemoryRewriteProfile Read(string systemResourceName, string userResourceName)
        => EmbeddedMemoryRewriteProfileLoader.Read(
            typeof(AutobiographicalRewriteProfiles),
            MaintainerId,
            RolePlayMemoryBlockPaths.FirstPersonAutobiography,
            systemResourceName,
            userResourceName
        );
}
