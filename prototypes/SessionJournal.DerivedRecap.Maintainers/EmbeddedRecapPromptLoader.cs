namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

internal static class EmbeddedRecapPromptLoader {
    internal static string Read(
        Type assemblyAnchor,
        string resourceName
    ) {
        using Stream stream = assemblyAnchor.Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded prompt resource '{resourceName}' was not found."
            );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    internal static string ReadTaskInstruction(
        Type assemblyAnchor,
        string roleContextResourceName,
        string taskResourceName
    ) => Read(assemblyAnchor, roleContextResourceName)
        + "\n\n"
        + Read(assemblyAnchor, taskResourceName);
}
