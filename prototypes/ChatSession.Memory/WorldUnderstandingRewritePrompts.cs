namespace Atelia.ChatSession.Memory;

public static class WorldUnderstandingRewritePrompts {
    private const string EnglishSystemResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewrite.zh-CN.User.md";

    public static MemoryRewritePromptSet English { get; } = MemoryRewritePromptSet.ReadEmbedded(
        typeof(WorldUnderstandingRewritePrompts),
        EnglishSystemResourceName,
        EnglishUserResourceName
    );

    public static MemoryRewritePromptSet SimplifiedChinese { get; } = MemoryRewritePromptSet.ReadEmbedded(
        typeof(WorldUnderstandingRewritePrompts),
        SimplifiedChineseSystemResourceName,
        SimplifiedChineseUserResourceName
    );

    public static MemoryRewritePromptSet Default => SimplifiedChinese;
}
