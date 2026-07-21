namespace Atelia.ChatSession.Memory;

public static class AutobiographicalRewritePrompts {
    private const string SystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewriteSystem.md";
    private const string UserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRewriteUser.md";

    public static string SystemPrompt { get; } = ReadEmbeddedPrompt(SystemResourceName);
    public static string UserPrompt { get; } = ReadEmbeddedPrompt(UserResourceName);

    private static string ReadEmbeddedPrompt(string resourceName) {
        var assembly = typeof(AutobiographicalRewritePrompts).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
