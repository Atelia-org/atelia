namespace Atelia.ChatSession.Memory;

public static class WorldUnderstandingRewritePrompts {
    private const string SystemResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewriteSystem.md";
    private const string UserResourceName = "Atelia.ChatSession.Memory.Prompts.WorldUnderstandingRewriteUser.md";

    public static string SystemPrompt { get; } = ReadEmbeddedPrompt(SystemResourceName);
    public static string UserPrompt { get; } = ReadEmbeddedPrompt(UserResourceName);

    private static string ReadEmbeddedPrompt(string resourceName) {
        var assembly = typeof(WorldUnderstandingRewritePrompts).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
