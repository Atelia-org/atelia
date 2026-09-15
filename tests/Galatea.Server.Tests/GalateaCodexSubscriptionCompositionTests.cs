using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Galatea.Prompts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData(false, "listenUrls")]
    [InlineData(true, "listenUrls")]
    [InlineData(false, "urls")]
    [InlineData(true, "urls")]
    [InlineData(false, "kestrel")]
    [InlineData(true, "kestrel")]
    public async Task ConfigureWebHost_UsesOrdinaryWildcardListenerConfiguration(
        bool codex, string configurationSource
    ) {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            EnvironmentName = "Production"
        });
        if (configurationSource == "urls") {
            builder.Configuration["urls"] = "http://0.0.0.0:0";
        }
        if (configurationSource == "kestrel") {
            builder.Configuration["Kestrel:Endpoints:Public:Url"] = "http://0.0.0.0:0";
        }
        GalateaCodexSubscriptionComposition.ConfigureWebHost(
            builder.WebHost,
            Config(
                connections: codex ? [CodexConnection()] : [RegularConnection()],
                listenUrls: configurationSource == "listenUrls" ? ["http://0.0.0.0:0"]
                    : configurationSource == "kestrel" ? ["http://127.0.0.1:0"] : null,
                useDefaultListenUrls: false
            )
        );
        await using WebApplication app = builder.Build();
        app.MapGet("/", static () => "ok");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        try {
            var address = new Uri(Assert.Single(app.Urls));
            Assert.Equal("0.0.0.0", address.Host);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            Assert.Equal("ok", await client.GetStringAsync(
                $"http://127.0.0.1:{address.Port}/", timeout.Token));
        }
        finally { await app.StopAsync(timeout.Token); }
    }

    private const string OriginatorEnvironmentVariableName =
        "ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR";
    private const string AuthFileEnvironmentVariableName =
        "ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE";

    private static GalateaConfig Config(
        IReadOnlyList<CompletionConnectionConfig>? connections = null,
        IReadOnlyList<GalateaCharacterConfig>? users = null,
        IReadOnlyList<string>? listenUrls = null,
        bool useDefaultListenUrls = true
    ) {
        IReadOnlyList<CompletionConnectionConfig> effectiveConnections =
            connections ?? [CodexConnection()];
        return new GalateaConfig(
            users ?? [User("alice", effectiveConnections[0].Id)],
            GalateaDelegateTestConfiguration.Players,
            effectiveConnections,
            effectiveConnections.Select(static value => value.Id).ToArray(),
            InputNormalizerConnectionId: null,
            Delegates: GalateaDelegateTestConfiguration.Create(),
            ListenUrls: useDefaultListenUrls
                ? listenUrls ?? ["http://127.0.0.1:3510/"]
                : listenUrls
        );
    }

    private static GalateaCharacterConfig User(
        string id,
        string defaultConnectionId = "codex"
    ) => new(
        id,
        new GalateaCharacterName("Galatea"),
        Path.Combine(Path.GetTempPath(), "galatea-codex", id),
        Path.Combine(
            Path.GetTempPath(),
            "galatea-codex-delegation-state",
            id
        ),
        Path.Combine(
            Path.GetTempPath(),
            "galatea-codex-character-memory-state",
            id
        ),
        GalateaDelegateTestConfiguration.CreateHomeDirectory(Path.Combine(Path.GetTempPath(), "galatea-codex", id), id),
        GalateaSessionProvisioning.ExistingOnly,
        SystemPrompt: "prompt",
        DefaultConnectionId: defaultConnectionId
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

}
