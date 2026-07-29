using System.Text.RegularExpressions;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using SJ = Atelia.SessionJournal;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class MemoryMaintainerProducerFingerprintTests {
    [Fact]
    public void Fingerprint_IsStableAndExcludesOperationalIdentityAndSecrets() {
        RecapMaintainerProfileDescriptor profile = CreateProfile();
        var client = new FingerprintCompletionClient("client-a", "api-a");
        CompletionConnectionConfig connection = CreateConnection();

        string first = Compute(profile, client, connection);
        string second = Compute(
            CreateProfile(),
            new FingerprintCompletionClient("client-a", "api-a"),
            connection with {
                Id = "different-local-id",
                ApiKey = "different-secret",
                ApiKeyEnv = "DIFFERENT_API_KEY_ENV",
                BaseAddressEnv = "DIFFERENT_BASE_ADDRESS_ENV"
            }
        );

        Assert.Equal(first, second);
        Assert.Matches(new Regex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant), first);
    }

    [Fact]
    public void Fingerprint_ChangesWithEveryIncludedSemanticFieldFamily() {
        RecapMaintainerProfileDescriptor profile = CreateProfile();
        var client = new FingerprintCompletionClient("client-a", "api-a");
        CompletionConnectionConfig connection = CreateConnection();
        string baseline = Compute(profile, client, connection);

        var variants = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["profile name"] = Compute(CreateProfile(profileName: "preset-b"), client, connection),
            ["role id"] = Compute(CreateProfile(roleId: "role-b"), client, connection),
            ["maintainer id"] = Compute(CreateProfile(maintainerId: "maintainer-b"), client, connection),
            ["target carrier"] = Compute(CreateProfile(targetCarrier: SJ.ContextHeaderCarrier.Action), client, connection),
            ["target block"] = Compute(CreateProfile(targetBlockId: "summary-b"), client, connection),
            ["system prompt"] = Compute(CreateProfile(systemPrompt: "system-b"), client, connection),
            ["user prompt"] = Compute(CreateProfile(userPrompt: "user-b"), client, connection),
            ["connection kind"] = Compute(profile, client, connection with { Kind = "kind-b" }),
            ["model"] = Compute(profile, client, connection with { ModelId = "model-b" }),
            ["surface"] = Compute(profile, client, connection with { CompletionSurfaceId = "surface-b" }),
            ["resolved base address"] = Compute(profile, client, connection with { BaseAddress = "https://b.example/v1/" }),
            ["max tokens"] = Compute(profile, client, connection with { MaxTokens = 8192 }),
            ["client name"] = Compute(profile, new FingerprintCompletionClient("client-b", "api-a"), connection),
            ["client api spec"] = Compute(profile, new FingerprintCompletionClient("client-a", "api-b"), connection)
        };

        foreach (var variant in variants) {
            Assert.NotEqual(baseline, variant.Value);
        }

        Assert.Equal(variants.Count, variants.Values.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Compute(
        RecapMaintainerProfileDescriptor profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) => MemoryMaintainerProducerIdentity.ComputeProducerFingerprint(
        profile,
        client,
        connection
    );

    private static RecapMaintainerProfileDescriptor CreateProfile(
        string profileName = "preset-a",
        string roleId = "role-a",
        string maintainerId = "maintainer-a",
        SJ.ContextHeaderCarrier targetCarrier = SJ.ContextHeaderCarrier.Observation,
        string targetBlockId = "summary-a",
        string systemPrompt = "system-a",
        string userPrompt = "user-a"
    ) => new(
        profileName,
        roleId,
        new RecapRewriteProfile(
            maintainerId,
            new SJ.ContextHeaderBlockPath(targetCarrier, targetBlockId),
            systemPrompt,
            userPrompt
        )
    );

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

    private sealed class FingerprintCompletionClient(string name, string apiSpecId) : ICompletionClient {
        public string Name { get; } = name;

        public string ApiSpecId { get; } = apiSpecId;

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Fingerprint tests do not invoke completion.");
    }
}
