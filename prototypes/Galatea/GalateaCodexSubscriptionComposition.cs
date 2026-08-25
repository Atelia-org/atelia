using System.Net;
using Atelia.Completion;
using Atelia.Completion.OpenAI;
using Microsoft.AspNetCore.Server.Kestrel.Core;

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

    /// <summary>
    /// In Codex subscription mode, code owns every effective Kestrel endpoint.
    /// This replaces the default reloadable Kestrel endpoint loader so an
    /// appsettings/environment Kestrel:Endpoints value cannot override the
    /// loopback-only deployment boundary.
    /// </summary>
    internal static void ConfigureWebHost(
        IWebHostBuilder webHost,
        GalateaConfig config
    ) {
        ArgumentNullException.ThrowIfNull(webHost);
        if (!ContainsCodexConnection(config)) {
            if (config.ListenUrls is { Count: > 0 }) {
                webHost.UseUrls(config.ListenUrls.ToArray());
            }
            return;
        }

        webHost.PreferHostingUrls(preferHostingUrls: false);
        IConfiguration emptyKestrelConfiguration =
            new ConfigurationBuilder().Build();
        webHost.ConfigureKestrel(options => {
            _ = options.Configure(
                emptyKestrelConfiguration,
                reloadOnChange: false
            );
            foreach (string configured in config.ListenUrls!) {
                AddCodeOwnedLoopbackEndpoint(options, new Uri(configured));
            }
        });
    }

    private static void AddCodeOwnedLoopbackEndpoint(
        KestrelServerOptions options,
        Uri uri
    ) {
        void Configure(ListenOptions listen) {
            if (string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase
                )) {
                listen.UseHttps();
            }
        }

        if (string.Equals(
                uri.Host,
                "localhost",
                StringComparison.OrdinalIgnoreCase
            )) {
            options.ListenLocalhost(uri.Port, Configure);
            return;
        }
        if (!IPAddress.TryParse(uri.Host, out IPAddress? address)
            || !IPAddress.IsLoopback(address)) {
            throw new InvalidOperationException(
                "Validated Galatea Codex listener is no longer loopback."
            );
        }
        options.Listen(address, uri.Port, Configure);
    }

    private static bool ContainsCodexConnection(GalateaConfig config)
        => config.Connections.Any(static connection => string.Equals(
            connection.Kind,
            ConnectionKind,
            StringComparison.Ordinal
        ));

    private static bool HasOnlyRootPathText(string configured) {
        int schemeDelimiter = configured.IndexOf(
            "://",
            StringComparison.Ordinal
        );
        if (schemeDelimiter < 0) { return false; }
        int authorityStart = schemeDelimiter + 3;
        int pathStart = configured.IndexOf('/', authorityStart);
        if (pathStart < 0) { return true; }

        int queryStart = configured.IndexOf('?', pathStart);
        int fragmentStart = configured.IndexOf('#', pathStart);
        int pathEnd = configured.Length;
        if (queryStart >= 0) { pathEnd = Math.Min(pathEnd, queryStart); }
        if (fragmentStart >= 0) {
            pathEnd = Math.Min(pathEnd, fragmentStart);
        }
        return string.Equals(
            configured[pathStart..pathEnd],
            "/",
            StringComparison.Ordinal
        );
    }

    private static bool IsExactLoopbackHost(Uri uri) {
        string host = uri.IdnHost;
        if (string.Equals(
                host,
                "localhost",
                StringComparison.OrdinalIgnoreCase
            )) {
            return true;
        }
        return IPAddress.TryParse(host, out IPAddress? address)
            && IPAddress.IsLoopback(address)
            && uri.IsLoopback;
    }

    private static InvalidOperationException InvalidListenUrl(int index)
        => new(
            $"Galatea Codex subscription listenUrls[{index}] must be an "
            + "absolute HTTP or HTTPS loopback URL with no userinfo, query, "
            + "fragment, or non-root path."
        );

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
