using Atelia.ChatSession;

namespace Atelia.ChatSession.Memory;

internal static class EmbeddedMemoryRewriteProfileLoader {
    public static MemoryRewriteProfile Read(
        Type assemblyAnchor,
        string id,
        MemoryPackBlockPath target,
        string systemResourceName,
        string userResourceName
    ) => new(
        id,
        target,
        ReadResource(assemblyAnchor, systemResourceName),
        ReadResource(assemblyAnchor, userResourceName)
    );

    private static string ReadResource(Type assemblyAnchor, string resourceName) {
        using var stream = assemblyAnchor.Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
