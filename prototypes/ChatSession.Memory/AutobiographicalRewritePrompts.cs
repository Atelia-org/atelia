namespace Atelia.ChatSession.Memory;

public static class AutobiographicalRewritePrompts {
    private const string EnglishSystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.en.System.md";
    private const string EnglishUserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.en.User.md";
    private const string SimplifiedChineseSystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.zh-CN.System.md";
    private const string SimplifiedChineseUserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewrite.zh-CN.User.md";

    public static MemoryRewritePromptSet English { get; } = MemoryRewritePromptSet.ReadEmbedded(
        typeof(AutobiographicalRewritePrompts),
        EnglishSystemResourceName,
        EnglishUserResourceName
    );

    public static MemoryRewritePromptSet SimplifiedChinese { get; } = MemoryRewritePromptSet.ReadEmbedded(
        typeof(AutobiographicalRewritePrompts),
        SimplifiedChineseSystemResourceName,
        SimplifiedChineseUserResourceName
    );

    public static MemoryRewritePromptSet Default => SimplifiedChinese;
}
