using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

internal static class EmbeddedRecapRewriteProfileLoader {
    public static RecapRewriteProfile Read(
        Type assemblyAnchor,
        string id,
        ContextHeaderBlockPath target,
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
