using System.Net;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCodexSubscriptionCompositionTests {
    private const string ExpectedAccountFingerprint =
        "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void CreateFactoryRequiresFingerprintOnlyForCodexConfig() {
        var reads = new List<string>();

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException
        >(() => GalateaCodexSubscriptionComposition.CreateFactory(
            Config(),
            name => {
                reads.Add(name);
                return null;
            }
        ));

        Assert.Equal(
            [GalateaCodexSubscriptionComposition.AccountFingerprintEnvironmentVariable],
            reads
        );
        Assert.Contains(
            GalateaCodexSubscriptionComposition.AccountFingerprintEnvironmentVariable,
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("galatea-test")]
    public void CreateFactoryUsesDefaultOrConfiguredOriginatorAndDoesNotReadAuth(
        string? configuredOriginator
    ) {
        string nonexistentAuthFile = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-codex-tests",
            Guid.NewGuid().ToString("N"),
            "auth.json"
        );
        var environment = new Dictionary<string, string?>(
            StringComparer.Ordinal
        ) {
            [GalateaCodexSubscriptionComposition.AccountFingerprintEnvironmentVariable]
                = ExpectedAccountFingerprint,
            [GalateaCodexSubscriptionComposition.AuthFileEnvironmentVariable]
                = nonexistentAuthFile
        };
        if (configuredOriginator is not null) {
            environment[
                GalateaCodexSubscriptionComposition
                    .OriginatorEnvironmentVariable
            ] = configuredOriginator;
        }

        ICompletionClientFactory factory =
            GalateaCodexSubscriptionComposition.CreateFactory(
                Config(),
                name => environment.GetValueOrDefault(name)
            );

        Assert.IsType<CodexSubscriptionCompletionClientFactory>(factory);
        Assert.False(File.Exists(nonexistentAuthFile));
    }

    [Theory]
    [InlineData(OriginatorEnvironmentVariableName, " ")]
    [InlineData(AuthFileEnvironmentVariableName, "relative/auth.json")]
    [InlineData(AuthFileEnvironmentVariableName, " ")]
    public void CreateFactoryRejectsInvalidOptionalEnvironmentBeforeAuthRead(
        string invalidName,
        string invalidValue
    ) {
        var environment = new Dictionary<string, string?>(
            StringComparer.Ordinal
        ) {
            [GalateaCodexSubscriptionComposition.AccountFingerprintEnvironmentVariable]
                = ExpectedAccountFingerprint,
            [invalidName] = invalidValue
        };

        _ = Assert.Throws<InvalidOperationException>(() =>
            GalateaCodexSubscriptionComposition.CreateFactory(
                Config(),
                name => environment.GetValueOrDefault(name)
            )
        );
    }

    [Fact]
    public async Task ConfigureWebHost_CodeOwnsEffectiveLoopbackEndpointsDespiteHostileConfigurationAndReload() {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            EnvironmentName = "Production"
        });
        builder.Configuration["urls"] = "http://0.0.0.0:0";
        builder.Configuration[
            "Kestrel:Endpoints:Public:Url"
        ] = "http://0.0.0.0:0";
        GalateaCodexSubscriptionComposition.ConfigureWebHost(
            builder.WebHost,
            Config(listenUrls: ["http://127.0.0.1:0/"])
        );

        await using WebApplication app = builder.Build();
        app.MapGet("/", static () => "ok");
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15)
        );
        await app.StartAsync(timeout.Token);
        try {
            AssertActualListenersAreLoopback(app.Urls);
            KestrelServerOptions kestrel = app.Services
                .GetRequiredService<IOptions<KestrelServerOptions>>()
                .Value;
            Assert.NotNull(kestrel.ConfigurationLoader);
            Assert.Empty(
                kestrel.ConfigurationLoader.Configuration
                    .GetSection("Endpoints")
                    .GetChildren()
            );

            builder.Configuration["urls"] = "http://0.0.0.0:45678";
            builder.Configuration[
                "Kestrel:Endpoints:Public:Url"
            ] = "http://0.0.0.0:45679";
            ((IConfigurationRoot)builder.Configuration).Reload();
            await Task.Yield();

            AssertActualListenersAreLoopback(app.Urls);
        }
        finally {
            await app.StopAsync(timeout.Token);
        }
    }

    private static void AssertActualListenersAreLoopback(
        ICollection<string> addresses
    ) {
        Assert.NotEmpty(addresses);
        Assert.All(addresses, configured => {
            var uri = new Uri(configured, UriKind.Absolute);
            Assert.True(
                string.Equals(
                    uri.Host,
                    "localhost",
                    StringComparison.OrdinalIgnoreCase
                )
                || IPAddress.TryParse(uri.Host, out IPAddress? address)
                    && IPAddress.IsLoopback(address),
                $"Effective listener escaped loopback: {configured}"
            );
        });
    }

    private const string OriginatorEnvironmentVariableName =
        "ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR";
    private const string AuthFileEnvironmentVariableName =
        "ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE";

    private static GalateaConfig Config(
        IReadOnlyList<CompletionConnectionConfig>? connections = null,
        IReadOnlyList<GalateaUserConfig>? users = null,
        IReadOnlyList<string>? listenUrls = null,
        bool useDefaultListenUrls = true
    ) {
        IReadOnlyList<CompletionConnectionConfig> effectiveConnections =
            connections ?? [CodexConnection()];
        return new GalateaConfig(
            users ?? [User("alice")],
            effectiveConnections,
            effectiveConnections[0].Id,
            effectiveConnections.Select(static value => value.Id).ToArray(),
            InputNormalizerConnectionId: null,
            ListenUrls: useDefaultListenUrls
                ? listenUrls ?? ["http://127.0.0.1:3510/"]
                : listenUrls
        );
    }

    private static GalateaUserConfig User(string id) => new(
        id,
        "pw",
        Path.Combine(Path.GetTempPath(), "galatea-codex", id),
        GalateaSessionProvisioning.ExistingOnly,
        SystemPrompt: "prompt"
    );

    private static CompletionConnectionConfig CodexConnection(
        string id = "codex"
    ) => new(
        id,
        GalateaCodexSubscriptionComposition.ConnectionKind,
        "codex-model",
        CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
        CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress
    );

    private static CompletionConnectionConfig RegularConnection() => new(
        "regular",
        "openai-chat",
        "model-a",
        "openai-chat/strict",
        "http://localhost:8000/"
    );

    private sealed class NeverCalledFactory : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(ref _createCallCount);

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            _ = connection;
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "Codex preflight must run before factory effects."
            );
        }
    }
}
