using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace Atelia.SessionJournal.Cli;

/// <summary>Subscription configuration is needed only when an actual route creates its client.</summary>
internal sealed class CliCompletionClientFactory : ICompletionClientFactory {
    private readonly ICompletionClientFactory _fallback;
    private readonly Lazy<CodexSubscriptionCompletionClientFactory> _subscription;

    internal CliCompletionClientFactory(
        ICompletionClientFactory? fallback = null,
        Func<string, string?>? readEnvironmentVariable = null
    ) {
        _fallback = fallback ?? new DefaultCompletionClientFactory();
        _subscription = new Lazy<CodexSubscriptionCompletionClientFactory>(() =>
            CodexSubscriptionCompletionClientFactory.CreateFromEnvironment(
                _fallback, "session-journal-cli", "Atelia.SessionJournal.Cli", readEnvironmentVariable));
    }

    public ICompletionClient Create(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);
        return string.Equals(connection.Kind, CodexSubscriptionCompletionClientFactory.ConnectionKind,
            StringComparison.Ordinal) ? _subscription.Value.Create(connection) : _fallback.Create(connection);
    }
}
