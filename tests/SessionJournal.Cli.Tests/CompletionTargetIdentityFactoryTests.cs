using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class CompletionTargetIdentityFactoryTests {
    [Fact]
    public void Create_PreservesWireFingerprintsAndExcludesSecrets() {
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
            + "209495f1b83d253c60d6c52865485bba"
            + "0851b295f5710f6547050a7ed250244e",
            identity.ConnectionFingerprint
        );
        Assert.Equal(
            "sha256:"
            + "02cf26e32afe666f0d3f112b7f896b5f"
            + "aac856cf25439d903ecc80c1c923937e",
            identity.RequestAdapterFingerprint
        );
        Assert.Equal(identity, changedSecrets);
    }

    [Fact]
    public void FingerprintsCoverEverySemanticFieldFamily() {
        CompletionConnectionConfig connection = CreateConnection();
        var client = new IdentityCompletionClient(
            "client-a",
            "api-a"
        );
        string connectionBaseline =
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(connection);
        string adapterBaseline =
            CompletionTargetIdentityFactory
                .ComputeRequestAdapterFingerprint(
                    client,
                    connection
                );

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
            CompletionTargetIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { MaxTokens = 8192 }
                )
        ];
        string[] adapterVariants = [
            CompletionTargetIdentityFactory
                .ComputeRequestAdapterFingerprint(
                    new IdentityCompletionClient(
                        "client-b",
                        "api-a"
                    ),
                    connection
                ),
            CompletionTargetIdentityFactory
                .ComputeRequestAdapterFingerprint(
                    new IdentityCompletionClient(
                        "client-a",
                        "api-b"
                    ),
                    connection
                ),
            CompletionTargetIdentityFactory
                .ComputeRequestAdapterFingerprint(
                    client,
                    connection with { Kind = "kind-b" }
                ),
            CompletionTargetIdentityFactory
                .ComputeRequestAdapterFingerprint(
                    client,
                    connection with {
                        CompletionSurfaceId = "surface-b"
                    }
                )
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
        Assert.All(
            adapterVariants,
            fingerprint => Assert.NotEqual(
                adapterBaseline,
                fingerprint
            )
        );
        Assert.Equal(
            adapterVariants.Length,
            adapterVariants.Distinct(StringComparer.Ordinal).Count()
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
            ApiKeyEnv: "API_KEY_ENV",
            MaxTokens: 4096
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
