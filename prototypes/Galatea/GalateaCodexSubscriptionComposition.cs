using Atelia.Completion;
using Atelia.Completion.OpenAI;

namespace Atelia.Galatea.Server;

internal static class GalateaCodexSubscriptionComposition {
    internal const string ConnectionKind =
        CodexSubscriptionCompletionClientFactory.ConnectionKind;
    internal const string AccountFingerprintEnvironmentVariable =
        CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable;
    internal const string OriginatorEnvironmentVariable =
        CodexSubscriptionCompletionClientFactory.OriginatorEnvironmentVariable;
    internal const string AuthFileEnvironmentVariable =
        CodexSubscriptionCompletionClientFactory.AuthFileEnvironmentVariable;

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

        return CodexSubscriptionCompletionClientFactory.CreateFromEnvironment(
            fallback, DefaultOriginator, "Atelia.Galatea", readEnvironmentVariable);
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

}
