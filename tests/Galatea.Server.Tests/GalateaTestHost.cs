using System.Buffers;
using System.Text.Json;
using Atelia.Completion;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Cadence;
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
            Version: GalateaStrictConfigReader.CurrentConfigVersion,
            Users: [
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
        WriteConnectionsFile(
            Path.Combine(
                configurationDirectory,
                GalateaConfigLoader.ConnectionsFileName
            ),
            connections,
            defaultConnectionId
        );
        return configPath;
    }

    internal static void WriteConnectionsFile(
        string path,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId
    ) {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("connections");
            foreach (CompletionConnectionConfig connection in connections) {
                if (!string.IsNullOrWhiteSpace(connection.BaseAddressEnv)
                    || !string.IsNullOrWhiteSpace(connection.ApiKeyEnv)) {
                    throw new InvalidOperationException(
                        "Galatea test fixtures use explicit inline sources."
                    );
                }
                writer.WriteStartObject();
                writer.WriteString("id", connection.Id);
                writer.WriteString("kind", connection.Kind);
                writer.WriteString("modelId", connection.ModelId);
                writer.WriteString(
                    "completionSurfaceId",
                    connection.CompletionSurfaceId
                );
                writer.WriteString("baseAddress", connection.BaseAddress);
                if (!string.IsNullOrWhiteSpace(connection.ApiKey)) {
                    writer.WriteString("apiKey", connection.ApiKey);
                }
                if (connection.MaxTokens is int maxTokens) {
                    writer.WriteNumber("maxTokens", maxTokens);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("defaultConnectionId", defaultConnectionId);
            writer.WriteEndObject();
        }
        byte[] bytes = output.WrittenSpan.ToArray();
        _ = CompletionConnectionConfigLoader.Decode(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static RecapGridAgentControlProfile AssertBuiltInProfile() {
        if (!RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
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

        RecapGridCadenceCreateResult cadence =
            RecapGridCadenceFactory.Create(
                engine,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad: 1,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    targetHistoryLoad: 1,
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                )
            );
        if (cadence is not RecapGridCadenceCreateResult.Created) {
            throw new InvalidOperationException(
                $"The Galatea test Cadence could not be provisioned: "
                + cadence.GetType().Name
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
