using System.Text.Json;
using Atelia.Completion;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atelia.Galatea.Server.Tests;

internal sealed class GalateaTestHost : IAsyncDisposable {
    private const string TestUserId = "alice";
    private const string TestPassword = "pw1";

    private readonly string _tempRoot;
    private readonly bool _deleteFilesOnDispose;

    private GalateaTestHost(
        string tempRoot,
        string sessionDirectory,
        string configPath,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        bool deleteFilesOnDispose
    ) {
        _tempRoot = tempRoot;
        _deleteFilesOnDispose = deleteFilesOnDispose;
        SessionDirectory = sessionDirectory;
        ConfigPath = configPath;
        Factory = new GalateaWebApplicationFactory(
            configPath,
            completionClientFactory,
            normalizer
        );
    }

    public GalateaWebApplicationFactory Factory { get; }

    internal string RootDirectory => _tempRoot;

    internal string SessionDirectory { get; }

    internal string ConfigPath { get; }

    public static GalateaTestHost Create(
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        bool deleteFilesOnDispose = true,
        string? callLogDirectory = null
    ) {
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(normalizer);

        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-server-tests",
            Guid.NewGuid().ToString("N")
        );
        string configDirectory = Path.Combine(
            tempRoot,
            ".atelia",
            "galatea"
        );
        Directory.CreateDirectory(configDirectory);

        string sessionDirectory = Path.Combine(tempRoot, "session");
        using (SessionJournalEngine engine =
               SessionJournalEngine.Create(
                   sessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "test system prompt",
                       "openai-chat/strict"
                   )
               )) {
            DerivedRecapStore.Open(
                    sessionDirectory,
                    engine.BranchRefId
                )
                .CreateAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        _ = RecapPlannerConfigInitializer.Initialize(
            sessionDirectory,
            new RecapPlannerConfigDocument(
                RecapPlannerConfigCodec.SchemaV2,
                RecapPlanningPolicyIds.BoundedMaintainAllV1,
                new RecapCadenceConfigDocument(
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    MinimumRecentHistoryLoad: 1_000_000,
                    RecapBuildIntervalHistoryLoad: 1_000_000
                ),
                [
                    new RecapPlannerCatalogEntryDocument(
                        RecapMaintainerProfileCatalog
                            .WorldUnderstandingRewrite,
                        32_768
                    ),
                    new RecapPlannerCatalogEntryDocument(
                        RecapMaintainerProfileCatalog
                            .AutobiographicalRewrite,
                        32_768
                    )
                ],
                new RecapPlannerLimitsDocument(
                    MaxRawGrowthEventCount: 512,
                    MaxRouteEndpointsPerBlock: 4,
                    MaxMaintainerCallsPerBuild: 8,
                    MaxRawEventsPerStep: 64,
                    MaxRawEventsPerBuild: 512
                )
            )
        );
        string configPath = WriteConfiguration(
            configDirectory,
            Path.GetFullPath(sessionDirectory),
            [
                new CompletionConnectionConfig(
                    "test",
                    "openai-chat",
                    "model-a",
                    "openai-chat/strict",
                    "http://localhost:8000/",
                    ApiKey: "test-key"
                )
            ],
            "test",
            "test system prompt",
            callLogDirectory
        );

        return new GalateaTestHost(
            tempRoot,
            sessionDirectory,
            configPath,
            completionClientFactory,
            normalizer,
            deleteFilesOnDispose
        );
    }

    /// <summary>
    /// Creates only an isolated Galatea configuration root and points it at an
    /// already provisioned SessionJournal repository. The repository is never
    /// created, initialized, or owned by this host and therefore is never
    /// removed during disposal.
    /// </summary>
    public static GalateaTestHost OpenExisting(
        string sessionDirectory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        string systemPrompt = "test system prompt",
        string? callLogDirectory = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConnectionId);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        string absoluteSessionDirectory =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory)
            );
        if (!Directory.Exists(absoluteSessionDirectory)) {
            throw new DirectoryNotFoundException(
                absoluteSessionDirectory
            );
        }

        string configurationRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-existing-repo-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(configurationRoot);
        try {
            string configPath = WriteConfiguration(
                configurationRoot,
                absoluteSessionDirectory,
                connections,
                defaultConnectionId,
                systemPrompt,
                callLogDirectory
            );
            return new GalateaTestHost(
                configurationRoot,
                absoluteSessionDirectory,
                configPath,
                completionClientFactory,
                normalizer,
                deleteFilesOnDispose: true
            );
        }
        catch {
            Directory.Delete(configurationRoot, recursive: true);
            throw;
        }
    }

    public HttpClient CreateClient() => Factory.CreateClient(
        new WebApplicationFactoryClientOptions {
            AllowAutoRedirect = false,
            HandleCookies = true
        }
    );

    public static Task<HttpResponseMessage> LoginAsync(
        HttpClient client
    ) {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["userId"] = TestUserId,
                ["password"] = TestPassword
            })
        );
    }

    public async ValueTask DisposeAsync() {
        await Factory.DisposeAsync().ConfigureAwait(false);
        if (_deleteFilesOnDispose && Directory.Exists(_tempRoot)) {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static string WriteConfiguration(
        string configurationDirectory,
        string absoluteSessionDirectory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        string systemPrompt,
        string? callLogDirectory
    ) {
        var users = new GalateaUsersFileConfig(
            [
                new GalateaUserConfig(
                    TestUserId,
                    TestPassword,
                    absoluteSessionDirectory,
                    SystemPrompt: systemPrompt
                )
            ],
            CallLogDir: callLogDirectory
        );
        var connectionsFile = new CompletionConnectionsFileConfig(
            connections,
            defaultConnectionId
        );
        var jsonOptions = new JsonSerializerOptions(
            JsonSerializerDefaults.Web
        );
        string configPath = Path.Combine(
            configurationDirectory,
            "config.json"
        );
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(users, jsonOptions)
        );
        File.WriteAllText(
            Path.Combine(
                configurationDirectory,
                GalateaConfigLoader.ConnectionsFileName
            ),
            JsonSerializer.Serialize(connectionsFile, jsonOptions)
        );
        return configPath;
    }
}

internal sealed class GalateaWebApplicationFactory(
    string configPath,
    ICompletionClientFactory completionClientFactory,
    IGalateaUserMessageNormalizer normalizer
) : WebApplicationFactory<Program> {
    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Galatea:ConfigPath", configPath);
        builder.ConfigureTestServices(services => {
            services.RemoveAll<ICompletionClientFactory>();
            services.AddSingleton(completionClientFactory);
            services.RemoveAll<IGalateaUserMessageNormalizer>();
            services.AddSingleton(normalizer);
        });
    }
}
