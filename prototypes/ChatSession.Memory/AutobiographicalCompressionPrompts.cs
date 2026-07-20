namespace Atelia.ChatSession.Memory;

public static class AutobiographicalCompressionPrompts {
    private const string SystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalCompressionSystem.md";
    private const string UserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalCompressionUser.md";

    public static string SystemPrompt { get; } = ReadEmbeddedPrompt(SystemResourceName);
    public static string UserPromptTemplate { get; } = ReadEmbeddedPrompt(UserResourceName);

    public static string FormatUserPrompt(int currentTokenCount, int targetTokenCount) {
        if (currentTokenCount <= 0) { throw new ArgumentOutOfRangeException(nameof(currentTokenCount)); }
        if (targetTokenCount <= 0) { throw new ArgumentOutOfRangeException(nameof(targetTokenCount)); }

        int compressionPercent = Math.Clamp(
            (int)Math.Round((currentTokenCount - targetTokenCount) * 100d / currentTokenCount),
            0,
            100
        );
        return UserPromptTemplate
            .Replace("{currentTokenCount}", currentTokenCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{targetTokenCount}", targetTokenCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{compressionPercent}", compressionPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string ReadEmbeddedPrompt(string resourceName) {
        var assembly = typeof(AutobiographicalCompressionPrompts).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
