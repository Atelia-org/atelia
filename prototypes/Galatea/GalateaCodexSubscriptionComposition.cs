using Atelia.Completion;
using Atelia.Completion.OpenAI;

namespace Atelia.Galatea.Server;

internal static class GalateaCodexSubscriptionComposition {
    internal const string ConnectionKind =
        CodexSubscriptionCompletionClientFactory.ConnectionKind;
    internal const string AccountFingerprintEnvironmentVariable =
        "ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT";
    internal const string OriginatorEnvironmentVariable =
        "ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR";
    internal const string AuthFileEnvironmentVariable =
        "ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE";

    private const string DefaultOriginator = "galatea";

    internal static ICompletionClientFactory CreateFactory(
        GalateaConfig config
    ) => CreateFactory(config, Environment.GetEnvironmentVariable);

    internal static ICompletionClientFactory CreateFactory(
        GalateaConfig config,
        Func<string, string?> readEnvironmentVariable
    ) {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        var fallback = new DefaultCompletionClientFactory();
        if (!ContainsCodexConnection(config)) { return fallback; }

        string expectedAccountFingerprint = RequireEnvironmentValue(
            readEnvironmentVariable,
            AccountFingerprintEnvironmentVariable
        );
        string? configuredOriginator = readEnvironmentVariable(
            OriginatorEnvironmentVariable
        );
        string originator = configuredOriginator is null
            ? DefaultOriginator
            : RequireNonBlankEnvironmentValue(
                configuredOriginator,
                OriginatorEnvironmentVariable
            );
        string? configuredAuthFile = readEnvironmentVariable(
            AuthFileEnvironmentVariable
        );
        ICodexSubscriptionCredentialProvider credentialProvider;
        if (configuredAuthFile is null) {
            credentialProvider = new CodexCliAuthFileCredentialProvider();
        }
        else {
            string authFile = RequireNonBlankEnvironmentValue(
                configuredAuthFile,
                AuthFileEnvironmentVariable
            );
            if (!Path.IsPathFullyQualified(authFile)) {
                throw new InvalidOperationException(
                    $"{AuthFileEnvironmentVariable} must contain an "
                    + "absolute path when configured."
                );
            }
            credentialProvider = new CodexCliAuthFileCredentialProvider(
                authFile
            );
        }

        return new CodexSubscriptionCompletionClientFactory(
            credentialProvider,
            expectedAccountFingerprint,
            originator,
            fallback,
            productName: "Atelia.Galatea"
        );
    }

    internal static void ConfigureWebHost(
        IWebHostBuilder webHost,
        GalateaConfig config
    ) {
        ArgumentNullException.ThrowIfNull(webHost);
        if (config.ListenUrls is { Count: > 0 }) {
            webHost.UseUrls(config.ListenUrls.ToArray());
        }
    }

    private static bool ContainsCodexConnection(GalateaConfig config)
        => config.Connections.Any(static connection => string.Equals(
            connection.Kind,
            ConnectionKind,
            StringComparison.Ordinal
        ));

    private static string RequireEnvironmentValue(
        Func<string, string?> readEnvironmentVariable,
        string name
    ) => RequireNonBlankEnvironmentValue(
        readEnvironmentVariable(name),
        name
    );

    private static string RequireNonBlankEnvironmentValue(
        string? value,
        string name
    ) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException(
                $"{name} is required and must not be blank when Galatea "
                + "uses an openai-codex-responses connection."
            );
        }
        return value;
    }
}
