namespace Atelia.ChatSession.Memory;

public sealed record MemoryRewritePromptSet(string SystemPrompt, string UserPrompt) {
    internal static MemoryRewritePromptSet ReadEmbedded(
        Type assemblyAnchor,
        string systemResourceName,
        string userResourceName
    ) => new(
        ReadEmbeddedPrompt(assemblyAnchor, systemResourceName),
        ReadEmbeddedPrompt(assemblyAnchor, userResourceName)
    );

    private static string ReadEmbeddedPrompt(Type assemblyAnchor, string resourceName) {
        using var stream = assemblyAnchor.Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
