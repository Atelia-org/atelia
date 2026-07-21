using Atelia.ChatSession;

namespace Atelia.ChatSession.Memory;

public static class AutobiographicalRewriteProfiles {
    public const string MaintainerId = "roleplay.first-person-autobiography.rewrite";

    private const string EnglishSystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.zh-CN.User.md";

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
