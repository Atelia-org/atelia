using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class CliCompletionClientFactoryTests {
    [Fact]
    public void ConstructionAndOtherProvidersDoNotReadSubscriptionEnvironment() {
        var fallback = new TrackingFallback();
        var factory = new CliCompletionClientFactory(fallback,
            _ => throw new InvalidOperationException("Environment must stay untouched."));
        CompletionConnectionConfig connection = Connection() with { Kind = "test" };
        Assert.Throws<FallbackReachedException>(() => factory.Create(connection));
        Assert.Same(connection, fallback.Connection);
    }

    [Fact]
    public void OnlyActualCodexCreationRequiresFingerprint() {
        var reads = new List<string>();
        var factory = new CliCompletionClientFactory(readEnvironmentVariable: name => {
            reads.Add(name);
            return null;
        });
        Assert.Empty(reads);
        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(Connection()));
        Assert.Equal([CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable], reads);
        Assert.Contains(CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable, error.Message);
    }

    [Fact]
    public void SubscriptionEnvironmentIsFrozenOnceWithoutReadingAuth() {
        var reads = new List<string>();
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "auth.json");
        var factory = new CliCompletionClientFactory(readEnvironmentVariable: name => {
            reads.Add(name);
            return name switch {
                CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable => "sha256:" + new string('0', 64),
                CodexSubscriptionCompletionClientFactory.AuthFileEnvironmentVariable => missing,
                _ => null
            };
        });
        using var first = Assert.IsType<OpenAICodexResponsesClient>(factory.Create(Connection()));
        using var second = Assert.IsType<OpenAICodexResponsesClient>(factory.Create(Connection()));
        Assert.Equal(3, reads.Count);
        Assert.False(File.Exists(missing));
    }

    private static CompletionConnectionConfig Connection() => new("codex",
        CodexSubscriptionCompletionClientFactory.ConnectionKind, "synthetic-model",
        CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
        CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);

    private sealed class FallbackReachedException : Exception;
    private sealed class TrackingFallback : ICompletionClientFactory {
        internal CompletionConnectionConfig? Connection { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Connection = connection;
            throw new FallbackReachedException();
        }
    }
}
