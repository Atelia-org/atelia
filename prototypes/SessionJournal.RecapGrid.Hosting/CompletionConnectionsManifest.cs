using Atelia.Completion;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

/// <summary>
/// Defensively normalizes and freezes programmatic host input. Connections
/// document bytes are owned exclusively by
/// <see cref="CompletionConnectionConfigLoader.Decode"/>.
/// </summary>
internal static class RecapGridCompletionConnectionsManifest {
    internal static CompletionConnectionsFileConfig Freeze(
        CompletionConnectionsFileConfig config
    ) {
        try {
            return CompletionConnectionConfigLoader.NormalizeAndValidate(
                config
            );
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException) {
            throw new InvalidDataException(
                "Completion connections are not a valid bounded programmatic value.",
                exception
            );
        }
    }
}
