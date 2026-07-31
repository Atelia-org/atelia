using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;

namespace Atelia.Galatea.Server;

/// <summary>
/// Opt-in Completion call logging owned by the Galatea composition root. The
/// wrapper preserves the inner client's Name and ApiSpecId, so durable
/// completion-target identity remains independent of logging configuration.
/// </summary>
internal static class GalateaCompletionLogging {
    internal static ICompletionClient CreateAgentClient(
        ICompletionClient inner,
        CompletionConnectionConfig connection,
        string? callLogDirectory
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(connection);
        return callLogDirectory is null
            ? inner
            : new LoggingCompletionClient(
                inner,
                connection,
                Path.Combine(callLogDirectory, "agent"),
                new CompletionCallLogContext(
                    Command: "galatea/agent"
                )
            );
    }

    internal static ICompletionClient CreateMaintainerClient(
        ICompletionClient inner,
        CompletionConnectionConfig connection,
        string? callLogDirectory,
        RecapMaintainerProfileDescriptor descriptor
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(descriptor);
        return callLogDirectory is null
            ? inner
            : new LoggingCompletionClient(
                inner,
                connection,
                Path.Combine(
                    callLogDirectory,
                    "maintenance",
                    SafePathSegment(descriptor.MaintainerId)
                ),
                new CompletionCallLogContext(
                    Command: "galatea/maintenance",
                    MaintainerId: descriptor.MaintainerId,
                    TargetCarrier:
                        ContextHeaderCarrierTokens.ToStorageToken(
                            descriptor.Target.Carrier
                        ),
                    TargetBlockId: descriptor.Target.BlockKey
                )
            );
    }

    private static string SafePathSegment(string value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        char[] characters = value.ToCharArray();
        for (int index = 0; index < characters.Length; index++) {
            char current = characters[index];
            if (!(char.IsAsciiLetterOrDigit(current)
                    || current is '-' or '_' or '.')) {
                characters[index] = '_';
            }
        }
        string result = new(characters);
        return result is "." or ".." ? $"_{result}_" : result;
    }
}
