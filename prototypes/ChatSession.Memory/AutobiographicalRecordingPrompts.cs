using System.Reflection;

namespace Atelia.ChatSession.Memory;

public static class AutobiographicalRecordingPrompts {
    private const string SystemResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRecordingSystem.md";
    private const string UserResourceName = "Atelia.ChatSession.Memory.Prompts.AutobiographicalRecordingUser.md";

    public static string SystemPrompt { get; } = ReadEmbeddedPrompt(SystemResourceName);
    public static string UserPrompt { get; } = ReadEmbeddedPrompt(UserResourceName);

    private static string ReadEmbeddedPrompt(string resourceName) {
        var assembly = typeof(AutobiographicalRecordingPrompts).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
