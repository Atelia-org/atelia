using Atelia.Completion;
using Atelia.Completion.Abstractions;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class CompletionTargetIdentityFactory {
    internal static SJ.SessionCompletionTargetIdentity Create(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) => Create(
        CompletionDispatchIdentityFactory.Create(connection, client)
    );

    internal static SJ.SessionCompletionTargetIdentity Create(
        CompletionDispatchIdentity identity
    ) => new(
        identity.ConnectionId,
        identity.Kind,
        identity.ConnectionFingerprint,
        identity.RequestAdapterFingerprint
    );

    internal static string ComputeConnectionFingerprint(
        CompletionConnectionConfig connection
    ) => CompletionDispatchIdentityFactory
        .ComputeConnectionFingerprint(connection);

    internal static string ComputeRequestAdapterFingerprint(
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) => CompletionDispatchIdentityFactory
        .ComputeRequestAdapterFingerprint(client, connection);
}
