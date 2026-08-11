using System.Text.Json;
using Atelia.Completion;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
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
        string? callLogDirectory = null,
        bool maintenanceMode = false,
        RecapGridAgentControlProfile? agentControlProfile = null,
        bool provisionRawOnly = true
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
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
                   sessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "test system prompt",
                       "openai-chat/strict"
                   ))) {
            if (provisionRawOnly) {
                ProvisionRawOnlyRecapGrid(engine);
            }
        }
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
            callLogDirectory,
            maintenanceMode,
            agentControlProfile
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
        string? callLogDirectory = null,
        bool maintenanceMode = false,
        RecapGridAgentControlProfile? agentControlProfile = null
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
                callLogDirectory,
                maintenanceMode,
                agentControlProfile
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
        string? callLogDirectory,
        bool maintenanceMode,
        RecapGridAgentControlProfile? agentControlProfile
    ) {
        string agentControlProfileFile = "recap-grid-profile.json";
        RecapGridAgentControlProfile profile = agentControlProfile
            ?? AssertBuiltInProfile();
        File.WriteAllBytes(
            Path.Combine(
                configurationDirectory,
                agentControlProfileFile
            ),
            profile.ToCanonicalBytes()
        );
        var users = new GalateaUsersFileConfig(
            [
                new GalateaUserConfig(
                    TestUserId,
                    TestPassword,
                    absoluteSessionDirectory,
                    SystemPrompt: systemPrompt
                )
            ],
            CallLogDir: callLogDirectory,
            MaintenanceMode: maintenanceMode,
            RecapGrid: new GalateaRecapGridFileConfig(
                "recap-grid-routes.json",
                [agentControlProfileFile],
                profile.ProfileId
            )
        );
        var connectionsFile = new GalateaConnectionsFileConfig(
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

    private static RecapGridAgentControlProfile AssertBuiltInProfile() {
        if (!RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV1,
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            throw new InvalidOperationException(
                "The code-owned RecapGrid test asset is unavailable."
            );
        }
        return RecapGridAgentControlProfile.Create(
            "test-profile",
            new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                bundle.Families.Select(static value => value.Digest),
                bundle.Definitions.Select(static value =>
                    value.Capability.CapabilityFingerprint).Distinct(),
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: 64,
                maximumProjectedCalls: 1_024
            )
        );
    }

    private static void ProvisionRawOnlyRecapGrid(
        SessionJournalEngine engine
    ) {
        HistoryTimelineCreateResult timeline =
            HistoryTimelineFactory.Create(
                engine.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                ),
                new O200kBaseHistoryUnitLoadEstimator()
            );
        if (timeline is not HistoryTimelineCreateResult.Created) {
            throw new InvalidOperationException(
                $"The Galatea test Timeline could not be provisioned: "
                + timeline.GetType().Name
            );
        }

        RecapGridControlCreateResult control =
            RecapGridControlFactory.Create(
                engine.Path,
                engine.BranchRefId,
                new RecapGridControlAdmission(
                    RecapGridControlPermission.Create,
                    Array.Empty<FamilyDefinitionDigest>(),
                    Array.Empty<string>(),
                    Array.Empty<ContextHeaderCarrier>(),
                    ["test."],
                    maximumBootstrapRows: 64,
                    maximumProjectedCalls: 1_024
                )
            );
        if (control is not RecapGridControlCreateResult.Created) {
            throw new InvalidOperationException(
                $"The Galatea test Control could not be provisioned: "
                + control.GetType().Name
            );
        }
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
