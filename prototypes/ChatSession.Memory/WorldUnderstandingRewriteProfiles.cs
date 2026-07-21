using Atelia.ChatSession;

namespace Atelia.ChatSession.Memory;

public static class WorldUnderstandingRewriteProfiles {
    public const string MaintainerId = "roleplay.world-understanding.rewrite";

    private const string EnglishSystemResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.zh-CN.User.md";

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
            typeof(WorldUnderstandingRewriteProfiles),
            MaintainerId,
            RolePlayMemoryBlockPaths.WorldUnderstanding,
            systemResourceName,
            userResourceName
        );
}
