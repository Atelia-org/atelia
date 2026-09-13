using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class CompletionTargetIdentityFactoryTests {
    [Fact]
    public void Create_PreservesConnectionIdentityAndExcludesSecrets() {
        CompletionConnectionConfig connection = CreateConnection();
        var client = new IdentityCompletionClient(
            "client-a",
            "api-a"
        );

        var identity = CompletionTargetIdentityFactory.Create(
            connection,
            client
        );
        var changedSecrets = CompletionTargetIdentityFactory.Create(
            connection with {
                ApiKey = "different-secret",
                ApiKeyEnv = "DIFFERENT_API_KEY_ENV",
                BaseAddressEnv = "DIFFERENT_BASE_ADDRESS_ENV"
            },
            client
        );

        Assert.Equal("local-id", identity.ConnectionId);
        Assert.Equal("kind-a", identity.Kind);
        Assert.Equal(
            "sha256:"
            + "d252b9c28316fb0440afe6bf9773be15"
            + "da4e5c52666959b02dceb9eaaf88275e",
            identity.ConnectionFingerprint
        );
        Assert.Equal(identity, changedSecrets);
    }

    [Fact]
    public void ConnectionFingerprintCoversSemanticFields() {
        CompletionConnectionConfig connection = CreateConnection();
        string connectionBaseline =
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(connection);
        string[] connectionVariants = [
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { Id = "local-b" }
                ),
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { Kind = "kind-b" }
                ),
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { ModelId = "model-b" }
                ),
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with {
                        CompletionSurfaceId = "surface-b"
                    }
                ),
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with {
                        BaseAddress = "https://b.example/v1/"
                    }
                ),
        ];
        Assert.All(
            connectionVariants,
            fingerprint => Assert.NotEqual(
                connectionBaseline,
                fingerprint
            )
        );
        Assert.Equal(
            connectionVariants.Length,
            connectionVariants.Distinct(StringComparer.Ordinal).Count()
        );
    }

    private static CompletionConnectionConfig CreateConnection()
        => new(
            Id: "local-id",
            Kind: "kind-a",
            ModelId: "model-a",
            CompletionSurfaceId: "surface-a",
            BaseAddress: "https://a.example/v1/",
            ApiKey: "secret-a",
            BaseAddressEnv: "BASE_ADDRESS_ENV",
            ApiKeyEnv: "API_KEY_ENV"
        );

    private sealed class IdentityCompletionClient(
        string name,
        string apiSpecId
    ) : ICompletionClient {
        public string Name { get; } = name;

        public string ApiSpecId { get; } = apiSpecId;

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException(
            "Identity tests do not invoke completion."
        );
    }
}
